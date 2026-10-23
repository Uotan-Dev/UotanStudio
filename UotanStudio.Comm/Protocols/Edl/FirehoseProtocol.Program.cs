using System.Buffers;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol program, erase, patch and wipe operations.
/// <para>Firehose 协议的编程、擦除、补丁与擦写操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Programs (flashes) data to a partition.
    /// <para>将数据编程（刷写）到分区。</para>
    /// </summary>
    /// <param name="dataStream">The data stream to write. <para>要写入的数据流。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    /// <exception cref="EdlException">Thrown when the target NAKs the program. <para>目标对编程返回 NAK 时抛出。</para></exception>
    public Task<FirehoseResponse> ProgramAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
        => ProgramSerializedAsync(dataStream, new FirehoseSectorAddress(startSector, false), numSectors, filename, physicalPartition, cancellationToken);

    /// <summary>Programs a constant or disk-relative sector range without buffering the complete image.
    /// <para>编程常量或磁盘相对地址的扇区范围，不缓冲完整镜像。</para></summary>
    /// <param name="dataStream">The stream containing the complete declared range.
    /// <para>包含完整声明范围的流。</para></param>
    /// <param name="startSector">A constant or NUM_DISK_SECTORS-minus-offset expression.
    /// <para>常量，或 NUM_DISK_SECTORS 减去偏移量的表达式。</para></param>
    /// <param name="numSectors">The number of sectors to write.<para>待写入的扇区数量。</para></param>
    /// <param name="filename">The target filename metadata.<para>随命令发送的目标文件名元数据。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN.
    /// <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The final target acknowledgement.<para>目标的最终确认响应。</para></returns>
    public Task<FirehoseResponse> ProgramAsync(Stream dataStream, string startSector, long numSectors,
        string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
        => ProgramSerializedAsync(dataStream, FirehoseSectorAddress.Parse(startSector), numSectors, filename, physicalPartition, cancellationToken);

    private async Task<FirehoseResponse> ProgramSerializedAsync(Stream dataStream, FirehoseSectorAddress startSector,
        long numSectors, string filename, int physicalPartition, CancellationToken cancellationToken)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ProgramCoreAsync(dataStream, startSector, numSectors, filename, physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Programs while the caller holds the operation lease.</summary>
    internal async Task<FirehoseResponse> ProgramCoreAsync(Stream dataStream, FirehoseSectorAddress startSector,
        long numSectors, string filename, int physicalPartition, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _programPayloadDigest = null;
        RequireCapability(EdlFirehoseCommand.Program);
        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        startSector.ValidateRange(numSectors);
        var totalBytes = ValidateStorageRange(0, numSectors, physicalPartition);
        var payloadSize = GetProgramPayloadSize();
        if (!dataStream.CanRead)
        {
            throw new ArgumentException("The program source must be readable.", nameof(dataStream));
        }

        var tailPaddingBytes = 0L;
        if (dataStream.CanSeek)
        {
            var available = dataStream.Length - dataStream.Position;
            if (available < totalBytes)
            {
                try
                {
                    tailPaddingBytes = FlashProgramLength.ResolveTailPadding(available, numSectors, Configuration.SectorSize);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new ArgumentException(
                        exception.Message, nameof(dataStream), exception);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payloadStartPosition = dataStream.CanSeek ? dataStream.Position : -1;
        using var payloadDigest = dataStream.CanSeek ? Sha256.CreateAccumulator() : null;
        var transferStarted = false;
        var payloadSent = false;
        var responseReceived = false;
        try
        {
            var xml = FirehoseXmlBuilder.BuildProgram(Configuration.SectorSize, numSectors, filename, physicalPartition, startSector,
                slot: EffectiveSlot,
                pagesPerBlock: NandPagesPerBlockHint);
            var ready = await SendCommandAndWaitResponseAsync(xml, cancellationToken,
                skipResponseAcknowledgement: Configuration.SkipResponseOnReadWrite ? SkipResponseAcknowledgement(true) : null);
            responseReceived = true;
            RequireRawMode(ready, EdlErrorCode.ProgramFailed);
            transferStarted = true;
            await SendProgramPayloadAsync(dataStream, totalBytes, tailPaddingBytes, payloadSize, payloadDigest,
                cancellationToken);
            payloadSent = true;
            var completion = await ReceiveTransferCompletionAsync(EdlErrorCode.ProgramFailed, cancellationToken);
            if (payloadDigest is not null)
            {
                RecordProgramPayloadDigest(dataStream, startSector, numSectors, physicalPartition,
                    payloadStartPosition, payloadDigest.GetHashAndReset());
            }

            return completion;
        }
        catch (Exception ex)
        {
            _faulted = payloadSent || transferStarted || !responseReceived;
            RecordFailure(ex, transferStarted, payloadSent);
            throw;
        }
    }

    /// <summary>
    /// Validates an Android sparse image before writing RAW/FILL ranges. DONT_CARE ranges
    /// advance the target address without overwriting the existing storage contents.
    /// <para>在写入 RAW/FILL 范围前先校验 Android sparse 镜像。DONT_CARE 范围仅推进目标地址，
    /// 不覆盖存储上的既有内容。</para>
    /// </summary>
    /// <param name="sparseStream">The sparse image stream (must be seekable). <para>稀疏镜像流（必须可寻址）。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public Task<FirehoseResponse> ProgramSparseAsync(Stream sparseStream, long startSector, string filename = "sparse.img", int physicalPartition = 0, CancellationToken cancellationToken = default)
        => ProgramSparseCoreAsync(sparseStream, new FirehoseSectorAddress(startSector, false), filename, physicalPartition, null, cancellationToken);

    /// <summary>Writes sparse data ranges to a constant or disk-relative address, preserving DONT_CARE contents.
    /// <para>将 sparse 数据范围写入常量或磁盘相对地址，保留 DONT_CARE 范围内的既有内容。</para></summary>
    /// <param name="sparseStream">The seekable, caller-owned image stream.
    /// <para>可寻址、由调用方持有的镜像流。</para></param>
    /// <param name="startSector">The target sector or NUM_DISK_SECTORS-minus-offset expression.
    /// <para>目标扇区常量，或 NUM_DISK_SECTORS 减去偏移量的表达式。</para></param>
    /// <param name="filename">The filename metadata sent with each data range.
    /// <para>随每个数据范围一同发送的文件名元数据。</para></param>
    /// <param name="physicalPartition">The physical storage region.<para>物理存储区域。</para></param>
    /// <param name="maximumSectors">Optional declared partition capacity, including skipped ranges.
    /// <para>可选的声明分区容量，包含被跳过的范围。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The last actual target acknowledgement, or a health-probe response for an all-skipped image.
    /// <para>最后一次真实的目标确认；若镜像全部被跳过，则返回健康探测响应。</para></returns>
    public Task<FirehoseResponse> ProgramSparseAsync(Stream sparseStream, string startSector, string filename = "sparse.img",
        int physicalPartition = 0, long? maximumSectors = null, CancellationToken cancellationToken = default)
        => ProgramSparseCoreAsync(sparseStream, FirehoseSectorAddress.Parse(startSector), filename, physicalPartition, maximumSectors, cancellationToken);

    private async Task<FirehoseResponse> ProgramSparseCoreAsync(Stream sparseStream, FirehoseSectorAddress startSector,
        string filename, int physicalPartition, long? maximumSectors, CancellationToken cancellationToken)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ProgramSparseOwnedAsync(sparseStream, startSector, filename, physicalPartition,
            maximumSectors, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Programs sparse ranges under the caller's lease, reporting bytes only after each final ACK.</summary>
    internal async Task<FirehoseResponse> ProgramSparseOwnedAsync(Stream sparseStream, FirehoseSectorAddress startSector,
        string filename, int physicalPartition, long? maximumSectors, CancellationToken cancellationToken,
        Action<long>? rangeCompleted = null)
    {
        ThrowIfDisposed();
        RequireCapability(EdlFirehoseCommand.Sparse);
        ArgumentNullException.ThrowIfNull(sparseStream);
        if (!sparseStream.CanSeek || !sparseStream.CanRead)
        {
            throw new ArgumentException("Sparse image stream must be readable and seekable.", nameof(sparseStream));
        }

        ValidateStorageRange(0, 1, physicalPartition);
        var sectorSize = Configuration.SectorSize;
        using var image = FirehoseSparseLayout.Read(sparseStream, sectorSize, startSector, maximumSectors, cancellationToken);
        using var logical = new SparseReadStream(image, requireExactRawData: true);
        FirehoseResponse? response = null;
        foreach (var chunk in image.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Header.ChunkType == (ushort)ChunkType.DontCare)
            {
                continue;
            }

            var offset = (long)chunk.StartBlock * image.Header.BlockSize;
            var sectors = (long)chunk.Header.ChunkSize * image.Header.BlockSize / sectorSize;
            var address = new FirehoseSectorAddress(startSector.RelativeToEnd
                ? checked(startSector.Value - offset / sectorSize)
                : checked(startSector.Value + offset / sectorSize), startSector.RelativeToEnd);
            logical.Position = offset;
            response = await ProgramCoreAsync(logical, address, sectors, filename, physicalPartition, cancellationToken).ConfigureAwait(false);
            rangeCompleted?.Invoke(checked(sectors * sectorSize));
        }

        return response ?? await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildNop(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a partition programmed earlier in the same session by reading the data back and comparing it
    /// byte by byte with the source stream.
    /// <para>通过回读数据并与源流逐字节比较，校验同一会话中较早编程的分区。</para>
    /// </summary>
    /// <param name="dataStream">The original data stream (must be seekable). <para>原始数据流（必须可寻址）。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    public async Task VerifyProgramAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await VerifyProgramOwnedAsync(dataStream, startSector, numSectors, filename, physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a program entry on an already-owned operation lease; the caller must hold the lease.
    /// <para>在已持有的操作租约上校验 program 条目；调用方必须已持有该租约。</para>
    /// </summary>
    /// <param name="dataStream">The original data stream (must be seekable). <para>原始数据流（必须可寻址）。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    internal async Task VerifyProgramOwnedAsync(Stream dataStream, long startSector, long numSectors, string filename, int physicalPartition, CancellationToken cancellationToken)
    {
        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        if (!dataStream.CanSeek)
        {
            throw new ArgumentException("Data stream must be seekable for read-back verification.", nameof(dataStream));
        }

        if (await TryVerifyProgramWithTargetDigestOwnedAsync(dataStream, startSector, numSectors,
                physicalPartition, cancellationToken).ConfigureAwait(false))
        {
            Log($"On-target digest verification passed for {filename}, sectors={numSectors}");
            return;
        }

        Log($"Starting read-back verification for {filename}, sectors={numSectors}");

        using var comparison = new ReadBackComparisonStream(dataStream, filename);
        var response = await ReadToStreamCoreAsync(comparison, startSector, numSectors, filename, physicalPartition, cancellationToken).ConfigureAwait(false);
        if (!response.IsAck)
        {
            throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                $"Read-back verification read failed for {filename}. Response: {response.RawValue}");
        }

        await comparison.CompleteAsync(cancellationToken).ConfigureAwait(false);

        Log($"Read-back verification passed for {filename}");
    }

    /// <summary>
    /// Erases sectors from a partition using a per-storage erase timeout.
    /// <para>使用按存储类型的擦除超时擦除分区中的扇区。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public Task<FirehoseResponse> EraseAsync(long startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
        => EraseCoreAsync(new FirehoseSectorAddress(startSector, false), numSectors, physicalPartition, cancellationToken);

    /// <summary>Erases a constant or disk-relative range after validating its expression and length.
    /// <para>在校验表达式与长度后，擦除常量或磁盘相对地址的范围。</para></summary>
    /// <param name="startSector">A constant or NUM_DISK_SECTORS-minus-offset expression.
    /// <para>常量，或 NUM_DISK_SECTORS 减去偏移量的表达式。</para></param>
    /// <param name="numSectors">The number of sectors to erase.<para>待擦除的扇区数量。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN.
    /// <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The target erase acknowledgement.<para>目标的擦除确认响应。</para></returns>
    public Task<FirehoseResponse> EraseAsync(string startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
        => EraseCoreAsync(FirehoseSectorAddress.Parse(startSector), numSectors, physicalPartition, cancellationToken);

    private async Task<FirehoseResponse> EraseCoreAsync(FirehoseSectorAddress startSector, long numSectors,
        int physicalPartition, CancellationToken cancellationToken)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await EraseOwnedAsync(startSector, numSectors, physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Erases while the caller holds the operation lease.</summary>
    internal async Task<FirehoseResponse> EraseOwnedAsync(FirehoseSectorAddress startSector, long numSectors,
        int physicalPartition, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await RequireDestructiveCapabilityAsync(EdlFirehoseCommand.Erase, cancellationToken).ConfigureAwait(false);
        startSector.ValidateRange(numSectors);
        ValidateStorageRange(0, numSectors, physicalPartition);
        var xml = FirehoseXmlBuilder.BuildErase(Configuration.SectorSize, numSectors, physicalPartition, startSector,
            EffectiveSlot, NandPagesPerBlockHint);
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken, GetEraseTimeoutMs(),
            skipResponseAcknowledgement: Configuration.SkipResponseOnErase ? SkipResponseAcknowledgement(false) : null);
    }

    /// <summary>Applies a patch to a partition via &lt;patch&gt;. Patches targeting filename="DISK" are skipped (they apply to no physical partition).
    /// <para>通过 &lt;patch&gt; 向分区应用补丁。目标为 filename="DISK" 的补丁会被跳过（其不 针对任何物理分区）。</para></summary>
    /// <param name="filename">The target filename.<para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number.<para>物理分区编号。</para></param>
    /// <param name="startSector">The starting sector.<para>起始扇区。</para></param>
    /// <param name="byteOffset">The byte offset within the sector.<para>扇区内字节偏移。</para></param>
    /// <param name="value">The value to patch (hex string).<para>待写入的值（十六进制字符串）。</para></param>
    /// <param name="sizeInBytes">The size in bytes.<para>大小（字节）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The Firehose response.<para>Firehose 响应。</para></returns>
    public Task<FirehoseResponse> PatchAsync(string filename, int physicalPartition, long startSector, long byteOffset, string value, long sizeInBytes, CancellationToken cancellationToken = default)
        => PatchAsync(filename, physicalPartition, startSector.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byteOffset, value, sizeInBytes, cancellationToken);

    /// <summary>Applies a patch using a constant sector or a bounded NUM_DISK_SECTORS-minus-offset expression.
    /// <para>使用常量扇区或有界的 NUM_DISK_SECTORS 减去偏移量表达式应用补丁。</para></summary>
    /// <param name="filename">The patch target, including DISK for physical storage.
    /// <para>补丁目标；对物理存储整体可使用 DISK。</para></param>
    /// <param name="physicalPartition">The target storage region or LUN.
    /// <para>目标存储区域或 LUN。</para></param>
    /// <param name="startSector">The constant or disk-relative sector expression.
    /// <para>常量扇区，或磁盘相对扇区表达式。</para></param>
    /// <param name="byteOffset">The byte offset within the sector.<para>扇区内的字节偏移。</para></param>
    /// <param name="value">The value expression interpreted by the target programmer.
    /// <para>由目标 programmer 解释的值表达式。</para></param>
    /// <param name="sizeInBytes">The patch width, from one through eight bytes.
    /// <para>补丁宽度，取 1 到 8 字节。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The target's actual patch acknowledgement.<para>目标的实际补丁确认响应。</para></returns>
    public async Task<FirehoseResponse> PatchAsync(string filename, int physicalPartition, string startSector, long byteOffset,
        string value, long sizeInBytes, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await PatchOwnedAsync(filename, physicalPartition, startSector, byteOffset, value, sizeInBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Patches while the caller holds the operation lease.</summary>
    internal async Task<FirehoseResponse> PatchOwnedAsync(string filename, int physicalPartition, string startSector,
        long byteOffset, string value, long sizeInBytes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await RequireDestructiveCapabilityAsync(EdlFirehoseCommand.Patch, cancellationToken).ConfigureAwait(false);
        var address = ValidatePatch(Configuration.SectorSize, filename, physicalPartition, startSector, byteOffset, value, sizeInBytes);
        var xml = FirehoseXmlBuilder.BuildPatch(Configuration.SectorSize, filename, physicalPartition, address, byteOffset,
            value, sizeInBytes, EffectiveSlot);

        return await SendCommandAndWaitResponseAsync(xml, cancellationToken);
    }

    /// <summary>Validates patch parameters without accessing the transport.</summary>
    internal static FirehoseSectorAddress ValidatePatch(long sectorSize, string filename, int physicalPartition,
        string startSector, long byteOffset, string value, long sizeInBytes)
    {
        var address = FirehoseSectorAddress.Parse(startSector);
        address.ValidateRange(1);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeInBytes, 8);
        if (checked(byteOffset + sizeInBytes) > sectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "A patch must fit within its target sector.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256)
        {
            throw new ArgumentException("The patch value expression is too long.", nameof(value));
        }

        return address;
    }

    /// <summary>
    /// Wipes the first N sectors of a partition by programming zeros.
    /// <para>通过编程零字节擦写分区首部 N 个扇区。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors to wipe. <para>待擦写扇区数。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="zeroFilename">The filename label for the zeros payload. <para>零字节负载的文件名标签。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> WipeAsync(long startSector, long numSectors, int physicalPartition = 0, string zeroFilename = "Zeros16KB.bin", CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await WipeOwnedAsync(startSector, numSectors, physicalPartition, zeroFilename, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Programs zeros while the caller holds the operation lease.</summary>
    internal async Task<FirehoseResponse> WipeOwnedAsync(long startSector, long numSectors, int physicalPartition,
        string zeroFilename, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await RequireDestructiveCapabilityAsync(EdlFirehoseCommand.Wipe, cancellationToken).ConfigureAwait(false);
        Log($"Sending <wipe> (program-with-zeros) pp={physicalPartition}, start={startSector}, sectors={numSectors}");
        using var zeros = new ZeroFilledStream();
        return await ProgramCoreAsync(zeros, new FirehoseSectorAddress(startSector, false), numSectors,
            zeroFilename, physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the raw program payload from the data stream to the device, with a per-storage
    /// write timeout. A failed write is never replayed because its accepted length is unknown.
    /// The trailing <paramref name="tailPaddingBytes"/> are sent as zeros so a source whose last
    /// sector is only partially filled still covers the whole programmed range.
    /// <para>把原始 program 载荷从数据流发送到设备，并带每存储写入超时。写入失败绝不重放，因为其被接受的长度未知。
    /// 末尾的 <paramref name="tailPaddingBytes"/> 字节以零发送，使最后一个扇区仅部分填充的源文件也能覆盖完整编程范围。</para>
    /// </summary>
    /// <param name="dataStream">The data stream to write. 要写入的数据流。</param>
    /// <param name="totalBytes">The total number of bytes to send. 待发送的总字节数。</param>
    /// <param name="tailPaddingBytes">The trailing byte count supplied as zeros, always below one sector.
    /// 以零补齐的尾部字节数，恒小于一个扇区。</param>
    /// <param name="maxPayload">The validated, sector-aligned transfer size.</param>
    /// <param name="digest">Optional accumulator receiving every byte accepted by the transport, including the
    /// trailing zero padding, so the target digest can be compared without reading the source again.
    /// 可选的累加器；接收传输层接受的每个字节（含尾部补零），使目标摘要无需再次读取源即可比对。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task SendProgramPayloadAsync(Stream dataStream, long totalBytes, long tailPaddingBytes, int maxPayload,
        DigestAccumulator? digest, CancellationToken cancellationToken)
    {
        var zlpTimeout = GetZlpTimeoutMs();
        var buffer = ArrayPool<byte>.Shared.Rent(maxPayload);
        try
        {
            var dataBytes = totalBytes - tailPaddingBytes;
            long bytesWritten = 0;
            while (bytesWritten < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(maxPayload, totalBytes - bytesWritten);
                var readable = (int)Math.Min(toRead, dataBytes - bytesWritten);
                if (readable > 0)
                {
                    await dataStream.ReadExactlyAsync(buffer.AsMemory(0, readable), cancellationToken);
                }

                if (readable < toRead)
                {
                    buffer.AsSpan(readable, toRead - readable).Clear();
                }

                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                long written;
                try
                {
                    written = await _transport.WriteAsync(buffer, 0, toRead, zlpTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new EdlException(EdlErrorCode.WritePortTimeout, ex.Message, ex);
                }

                if (written != toRead)
                {
                    _faulted = true;
                    throw new EdlException(EdlErrorCode.WritePortTimeout,
                        $"The transport accepted {written} of {toRead} raw payload bytes; the block is not replayed because its accepted length is unknown.");
                }

                digest?.Append(buffer.AsSpan(0, toRead));
                bytesWritten += toRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Receives and validates the final response after a raw transfer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <param name="errorCode">The operation-specific failure code.</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    private async Task<FirehoseResponse> ReceiveTransferCompletionAsync(EdlErrorCode errorCode, CancellationToken cancellationToken)
    {
        var response = await ReceiveResponseAsync(cancellationToken);
        if (!response.IsAck || response.IsRawMode)
        {
            throw new EdlException(errorCode, "The target did not acknowledge completion outside raw mode.");
        }
        return response;
    }

    /// <summary>
    /// A stream that produces an endless sequence of zero bytes (used for wipe operations).
    /// <para>产生无限零字节的流（用于擦写操作）。</para>
    /// </summary>
    private sealed class ZeroFilledStream : Stream
    {
        /// <summary>
        /// Gets whether the stream can be read.
        /// <para>流是否可读。</para>
        /// </summary>
        public override bool CanRead => true;
        /// <summary>
        /// Gets whether the stream can seek.
        /// <para>流是否可寻址。</para>
        /// </summary>
        public override bool CanSeek => false;
        /// <summary>
        /// Gets whether the stream can be written.
        /// <para>流是否可写。</para>
        /// </summary>
        public override bool CanWrite => false;
        /// <summary>
        /// Gets the stream length (not supported).
        /// <para>流长度（不支持）。</para>
        /// </summary>
        public override long Length => throw new NotSupportedException();
        /// <summary>
        /// Gets or sets the stream position (not supported).
        /// <para>流位置（不支持）。</para>
        /// </summary>
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>
        /// Reads zeros into the buffer.
        /// <para>将零读入缓冲。</para>
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        /// <summary>
        /// Flush (no-op).
        /// <para>刷新（空操作）。</para>
        /// </summary>
        public override void Flush() { }

        /// <summary>
        /// Seek (not supported).
        /// <para>寻址（不支持）。</para>
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>
        /// SetLength (not supported).
        /// <para>设置长度（不支持）。</para>
        /// </summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Write (not supported).
        /// <para>写入（不支持）。</para>
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}