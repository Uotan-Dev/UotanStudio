using System.Buffers;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

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
    /// <param name="dataStream">The data stream to write. 要写入的数据流。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    /// <exception cref="EdlException">Thrown when the target NAKs the program. 目标对编程返回 NAK 时抛出。</exception>
    public Task<FirehoseResponse> ProgramAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
        => ProgramSerializedAsync(dataStream, new FirehoseSectorAddress(startSector, false), numSectors, filename, physicalPartition, cancellationToken);

    /// <summary>Programs a constant or disk-relative sector range without buffering the complete image.</summary>
    /// <param name="dataStream">The stream containing the complete declared range.</param>
    /// <param name="startSector">A constant or NUM_DISK_SECTORS-minus-offset expression.</param>
    /// <param name="numSectors">The number of sectors to write.</param>
    /// <param name="filename">The target filename metadata.</param>
    /// <param name="physicalPartition">The physical storage region or LUN.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The final target acknowledgement.</returns>
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
        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        startSector.ValidateRange(numSectors);
        var totalBytes = ValidateStorageRange(0, numSectors, physicalPartition);
        var payloadSize = GetProgramPayloadSize();
        if (!dataStream.CanRead || (dataStream.CanSeek && dataStream.Length - dataStream.Position < totalBytes))
        {
            throw new ArgumentException("The source must contain the entire declared program range.", nameof(dataStream));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var xml = FirehoseXmlBuilder.BuildProgram(Configuration.SectorSize, numSectors, filename, physicalPartition, startSector, slot: EffectiveSlot);
            var ready = await SendCommandAndWaitResponseAsync(xml, cancellationToken);
            RequireRawMode(ready, EdlErrorCode.ProgramFailed);
            await SendProgramPayloadAsync(dataStream, totalBytes, payloadSize, cancellationToken);
            return await ReceiveTransferCompletionAsync(EdlErrorCode.ProgramFailed, cancellationToken);
        }
        catch
        {
            // An interrupted raw transfer has no sequence number with which to resume safely.
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Validates an Android sparse image before writing RAW/FILL ranges. DONT_CARE ranges
    /// advance the target address without overwriting the existing storage contents.
    /// </summary>
    /// <param name="sparseStream">The sparse image stream (must be seekable). 稀疏镜像流（必须可寻址）。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public Task<FirehoseResponse> ProgramSparseAsync(Stream sparseStream, long startSector, string filename = "sparse.img", int physicalPartition = 0, CancellationToken cancellationToken = default)
        => ProgramSparseCoreAsync(sparseStream, new FirehoseSectorAddress(startSector, false), filename, physicalPartition, null, cancellationToken);

    /// <summary>Writes sparse data ranges to a constant or disk-relative address, preserving DONT_CARE contents.</summary>
    /// <param name="sparseStream">The seekable, caller-owned image stream.</param>
    /// <param name="startSector">The target sector or NUM_DISK_SECTORS-minus-offset expression.</param>
    /// <param name="filename">The filename metadata sent with each data range.</param>
    /// <param name="physicalPartition">The physical storage region.</param>
    /// <param name="maximumSectors">Optional declared partition capacity, including skipped ranges.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The last actual target acknowledgement, or a health-probe response for an all-skipped image.</returns>
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
        ArgumentNullException.ThrowIfNull(sparseStream);
        if (!sparseStream.CanSeek || !sparseStream.CanRead)
        {
            throw new ArgumentException("Sparse image stream must be readable and seekable.", nameof(sparseStream));
        }

        ValidateStorageRange(0, 1, physicalPartition);
        // Complete structural and checksum preflight before sending the first program command.
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
    /// Verifies a previously programmed partition by reading the data back and comparing it
    /// byte by byte with the source stream.
    /// <para>通过回读并逐字节比较源流来校验先前编程的分区。</para>
    /// </summary>
    /// <param name="dataStream">The original data stream (must be seekable). 原始数据流（必须可寻址）。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    public async Task VerifyProgramAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        if (!dataStream.CanSeek)
        {
            throw new ArgumentException("Data stream must be seekable for read-back verification.", nameof(dataStream));
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
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public Task<FirehoseResponse> EraseAsync(long startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
        => EraseCoreAsync(new FirehoseSectorAddress(startSector, false), numSectors, physicalPartition, cancellationToken);

    /// <summary>Erases a constant or disk-relative range after validating its expression and length.</summary>
    /// <param name="startSector">A constant or NUM_DISK_SECTORS-minus-offset expression.</param>
    /// <param name="numSectors">The number of sectors to erase.</param>
    /// <param name="physicalPartition">The physical storage region or LUN.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The target erase acknowledgement.</returns>
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
        startSector.ValidateRange(numSectors);
        ValidateStorageRange(0, numSectors, physicalPartition);
        var xml = FirehoseXmlBuilder.BuildErase(Configuration.SectorSize, numSectors, physicalPartition, startSector, EffectiveSlot);
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken, GetEraseTimeoutMs());
    }

    /// <summary>
    /// Applies a patch to a partition via &lt;patch&gt;. Patches targeting filename="DISK" are
    /// skipped (they apply to no physical partition).
    /// <para>通过 &lt;patch&gt; 向分区应用补丁。目标为 filename="DISK" 的补丁会被跳过（其不
    /// 针对任何物理分区）。</para>
    /// </summary>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="byteOffset">The byte offset within the sector. 扇区内字节偏移。</param>
    /// <param name="value">The value to patch (hex string). 待写入的值（十六进制字符串）。</param>
    /// <param name="sizeInBytes">The size in bytes. 大小（字节）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public Task<FirehoseResponse> PatchAsync(string filename, int physicalPartition, long startSector, long byteOffset, string value, long sizeInBytes, CancellationToken cancellationToken = default)
        => PatchAsync(filename, physicalPartition, startSector.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byteOffset, value, sizeInBytes, cancellationToken);

    /// <summary>Applies a patch using a constant sector or a bounded NUM_DISK_SECTORS-minus-offset expression.</summary>
    /// <param name="filename">The patch target, including DISK for physical storage.</param>
    /// <param name="physicalPartition">The target storage region or LUN.</param>
    /// <param name="startSector">The constant or disk-relative sector expression.</param>
    /// <param name="byteOffset">The byte offset within the sector.</param>
    /// <param name="value">The value expression interpreted by the target programmer.</param>
    /// <param name="sizeInBytes">The patch width, from one through eight bytes.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The target's actual patch acknowledgement.</returns>
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
        var address = ValidatePatch(Configuration.SectorSize, filename, physicalPartition, startSector, byteOffset, value, sizeInBytes);
        var xml = FirehoseXmlBuilder.BuildPatch(Configuration.SectorSize, filename, physicalPartition, address, byteOffset, value, sizeInBytes, EffectiveSlot);

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
    /// Wipes the first N sectors of a partition by programming zeros (mirrors fh_loader
    /// --wipefirst).
    /// <para>通过编程零字节擦写分区首部 N 个扇区（对齐 fh_loader --wipefirst）。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors to wipe. 待擦写扇区数。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="zeroFilename">The filename label for the zeros payload. 零字节负载的文件名标签。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
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
        Log($"Sending <wipe> (program-with-zeros) pp={physicalPartition}, start={startSector}, sectors={numSectors}");
        using var zeros = new ZeroFilledStream();
        return await ProgramCoreAsync(zeros, new FirehoseSectorAddress(startSector, false), numSectors,
            zeroFilename, physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the raw program payload from the data stream to the device, with a per-storage
    /// write timeout. A failed write is never replayed because its accepted length is unknown.
    /// </summary>
    /// <param name="dataStream">The data stream to write. 要写入的数据流。</param>
    /// <param name="totalBytes">The total number of bytes to send. 待发送的总字节数。</param>
    /// <param name="maxPayload">The validated, sector-aligned transfer size.</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task SendProgramPayloadAsync(Stream dataStream, long totalBytes, int maxPayload, CancellationToken cancellationToken)
    {
        var zlpTimeout = GetZlpTimeoutMs();
        var buffer = ArrayPool<byte>.Shared.Rent(maxPayload);
        try
        {
            long bytesWritten = 0;
            while (bytesWritten < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(maxPayload, totalBytes - bytesWritten);
                await dataStream.ReadExactlyAsync(buffer.AsMemory(0, toRead), cancellationToken);
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                var written = await _transport.WriteAsync(buffer, 0, toRead, zlpTimeout, cancellationToken);
                if (written != toRead)
                {
                    throw new EdlException(EdlErrorCode.WritePortTimeout,
                        "Incomplete raw write; device contents are uncertain. Reconnect before further commands.");
                }

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
        /// <summary>Gets whether the stream can be read. 流是否可读。</summary>
        public override bool CanRead => true;
        /// <summary>Gets whether the stream can seek. 流是否可寻址。</summary>
        public override bool CanSeek => false;
        /// <summary>Gets whether the stream can be written. 流是否可写。</summary>
        public override bool CanWrite => false;
        /// <summary>Gets the stream length (not supported). 流长度（不支持）。</summary>
        public override long Length => throw new NotSupportedException();
        /// <summary>Gets or sets the stream position (not supported). 流位置（不支持）。</summary>
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>Reads zeros into the buffer. 将零读入缓冲。</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        /// <summary>Flush (no-op). 刷新（空操作）。</summary>
        public override void Flush() { }

        /// <summary>Seek (not supported). 寻址（不支持）。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>SetLength (not supported). 设置长度（不支持）。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Write (not supported). 写入（不支持）。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}