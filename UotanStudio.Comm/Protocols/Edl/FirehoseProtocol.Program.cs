using System.Buffers;
using System.Buffers.Binary;
using System.Text;

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
    public async Task<FirehoseResponse> ProgramAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        var xml = FirehoseXmlBuilder.BuildProgram(Configuration.SectorSize, numSectors, filename, physicalPartition, startSector, slot: EffectiveSlot);
        await WriteDataAsync(Encoding.UTF8.GetBytes(xml), GetZlpTimeoutMs(), cancellationToken);
        Log($"Sent <program> for {filename}, sectors={numSectors}");

        var totalBytes = numSectors * Configuration.SectorSize;
        await SendProgramPayloadAsync(dataStream, totalBytes, cancellationToken);

        var response = await ReceiveProgramResponseAsync(cancellationToken);

        // A NAK must fail loudly: returning it silently left callers believing the write
        // succeeded.
        // NAK 必须显式失败：静默返回会让调用方误以为写入成功。
        return !response.IsAck
            ? throw new EdlException(EdlErrorCode.ProgramFailed,
                $"Program failed for {filename}. Response: {response.RawValue}")
            : response;
    }

    /// <summary>
    /// Programs a chunk-aware Android sparse image: the target sector count is computed from
    /// the raw/fill/dont-care chunks, and the expanded data (fill/dont-care chunks become
    /// pattern/zeros) is streamed to the device.
    /// <para>编程按块感知的 Android 稀疏镜像：目标扇区数由 raw/fill/dont-care 块计算得出，
    /// 展开后的数据（fill/dont-care 块展开为模式/零）流式写入设备。</para>
    /// </summary>
    /// <param name="sparseStream">The sparse image stream (must be seekable). 稀疏镜像流（必须可寻址）。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> ProgramSparseAsync(Stream sparseStream, long startSector, string filename = "sparse.img", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (sparseStream == null)
        {
            throw new ArgumentNullException(nameof(sparseStream));
        }

        if (!sparseStream.CanSeek)
        {
            throw new ArgumentException("Sparse image stream must be seekable.", nameof(sparseStream));
        }

        long totalSectors;
        var originalPosition = sparseStream.Position;
        try
        {
            totalSectors = AndroidSparseImage.CountDataSectors(sparseStream, Configuration.SectorSize);
            sparseStream.Position = originalPosition;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"Failed to parse sparse image: {ex.Message}", ex);
        }

        if (totalSectors <= 0)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "Sparse image contains no data chunks.");
        }

        using var rawStream = new AndroidSparseImage.SparseRawStream(sparseStream);
        return await ProgramAsync(rawStream, startSector, totalSectors, filename, physicalPartition, cancellationToken);
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
        ThrowIfDisposed();
        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        if (!dataStream.CanSeek)
        {
            throw new ArgumentException("Data stream must be seekable for read-back verification.", nameof(dataStream));
        }

        Log($"Starting read-back verification for {filename}, sectors={numSectors}");

        (var response, var readBackData) = await ReadAsync(startSector, numSectors, filename, physicalPartition, cancellationToken);
        if (!response.IsAck)
        {
            throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                $"Read-back verification read failed for {filename}. Response: {response.RawValue}");
        }

        dataStream.Position = 0;
        var originalData = new byte[dataStream.Length];
        var totalRead = 0;
        while (totalRead < originalData.Length)
        {
            var n = await dataStream.ReadAsync(originalData.AsMemory(totalRead), cancellationToken);
            if (n == 0)
            {
                break;
            }

            totalRead += n;
        }

        if (readBackData.Length != totalRead)
        {
            throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                $"Read-back verification size mismatch for {filename}: expected {totalRead}, got {readBackData.Length}");
        }

        for (var i = 0; i < totalRead; i++)
        {
            if (originalData[i] != readBackData[i])
            {
                throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                    $"Read-back verification mismatch for {filename} at byte {i}: expected 0x{originalData[i]:X2}, got 0x{readBackData[i]:X2}");
            }
        }

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
    public async Task<FirehoseResponse> EraseAsync(long startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
    public async Task<FirehoseResponse> PatchAsync(string filename, int physicalPartition, long startSector, long byteOffset, string value, long sizeInBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = FirehoseXmlBuilder.BuildPatch(Configuration.SectorSize, filename, physicalPartition, startSector, byteOffset, value, sizeInBytes, EffectiveSlot);

        if (string.IsNullOrEmpty(xml))
        {
            Log($"Skipping DISK patch for {filename}");
            return new FirehoseResponse { IsAck = true, RawValue = "SKIPPED" };
        }

        return await SendCommandAndWaitResponseAsync(xml, cancellationToken);
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
        ThrowIfDisposed();
        Log($"Sending <wipe> (program-with-zeros) pp={physicalPartition}, start={startSector}, sectors={numSectors}");
        using var zeros = new ZeroFilledStream();
        return await ProgramAsync(zeros, startSector, numSectors, zeroFilename, physicalPartition, cancellationToken);
    }

    /// <summary>
    /// Streams the raw program payload from the data stream to the device, with a per-storage
    /// write (ZLP) timeout and drain+retry on write timeouts.
    /// <para>将编程原始负载从数据流写入设备，使用按存储类型的写入（ZLP）超时，写超时时排出
    /// 数据后重试。</para>
    /// </summary>
    /// <param name="dataStream">The data stream to write. 要写入的数据流。</param>
    /// <param name="totalBytes">The total number of bytes to send. 待发送的总字节数。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task SendProgramPayloadAsync(Stream dataStream, long totalBytes, CancellationToken cancellationToken)
    {
        var maxPayload = (int)Math.Min(Configuration.MaxPayloadSizeToTarget, int.MaxValue);
        var zlpTimeout = GetZlpTimeoutMs();
        var buffer = ArrayPool<byte>.Shared.Rent(maxPayload);
        try
        {
            long bytesWritten = 0;
            while (bytesWritten < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(maxPayload, totalBytes - bytesWritten);
                var bytesRead = await dataStream.ReadAsync(buffer, 0, toRead, cancellationToken);
                if (bytesRead == 0)
                {
                    throw new EdlException(EdlErrorCode.ProgramFailed,
                        $"Program data stream ended after {bytesWritten}/{totalBytes} bytes.");
                }

                // Drain + retry on write timeout: re-send the SAME chunk (the failed chunk is
                // never advanced past).
                // 写超时时排出后重试：重发同一数据块（绝不越过失败的数据块）。
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var w = await _transport.WriteAsync(buffer, 0, bytesRead, zlpTimeout, cancellationToken);
                        if (w != bytesRead)
                        {
                            throw new EdlException(EdlErrorCode.WritePortTimeout);
                        }

                        break;
                    }
                    catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.TransportTimeout or EdlErrorCode.WritePortTimeout)
                    {
                        Log("Write timed out, draining pending log data and retrying...");
                        await DrainPendingDataAsync(cancellationToken);
                    }
                }

                bytesWritten += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Receives the program response, draining and retrying once when the device entered raw
    /// mode.
    /// <para>接收编程响应；设备进入 raw 模式时排出数据后再次接收。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    private async Task<FirehoseResponse> ReceiveProgramResponseAsync(CancellationToken cancellationToken)
    {
        var response = await ReceiveResponseAsync(cancellationToken);
        if (response.IsRawMode)
        {
            Log("Device entered raw mode after program, draining data...");
            await DrainPendingDataAsync(cancellationToken);
            response = await ReceiveResponseAsync(cancellationToken);
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

/// <summary>
/// Minimal Android sparse image (magic 0xED26FF3A) reader used for chunk-aware sparse
/// programming: counts data sectors and expands raw/fill/dont-care chunks into a plain data
/// stream.
/// <para>最小化的 Android 稀疏镜像（魔数 0xED26FF3A）读取器，用于按块感知的稀疏编程：统计数据
/// 扇区并将 raw/fill/dont-care 块展开为普通数据流。</para>
/// </summary>
internal static class AndroidSparseImage
{
    /// <summary>Android sparse image magic. Android 稀疏镜像魔数。</summary>
    public const uint Magic = 0xED26FF3A;

    /// <summary>Raw chunk type. Raw 块类型。</summary>
    public const ushort ChunkTypeRaw = 0xCAC1;
    /// <summary>Fill chunk type. Fill 块类型。</summary>
    public const ushort ChunkTypeFill = 0xCAC2;
    /// <summary>Don't-care chunk type. Don't-care 块类型。</summary>
    public const ushort ChunkTypeDontCare = 0xCAC3;
    /// <summary>CRC32 chunk type. CRC32 块类型。</summary>
    public const ushort ChunkTypeCrc32 = 0xCAC4;

    /// <summary>
    /// Counts the number of target sectors the sparse image expands to (raw/fill/dont-care
    /// chunks only).
    /// <para>统计稀疏镜像展开后的目标扇区数（仅 raw/fill/dont-care 块）。</para>
    /// </summary>
    /// <param name="source">The sparse image stream. 稀疏镜像流。</param>
    /// <param name="sectorSize">The target sector size. 目标扇区大小。</param>
    /// <returns>The total number of sectors. 总扇区数。</returns>
    public static long CountDataSectors(Stream source, long sectorSize)
    {
        if (sectorSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize));
        }

        (var blockSize, var chunkHdrSz, var totalChunks) = ReadHeader(source);
        long totalSectors = 0;

        var chunkHeader = new byte[chunkHdrSz];
        for (var i = 0; i < totalChunks; i++)
        {
            ReadExactly(source, chunkHeader, chunkHdrSz);
            var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(chunkHeader);
            var chunkSzBlocks = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            var totalSz = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(8));

            long dataBytes;
            switch (chunkType)
            {
                case ChunkTypeRaw:
                    dataBytes = chunkSzBlocks * blockSize;
                    // Skip the raw payload; it will be streamed by SparseRawStream.
                    // 跳过 raw 负载；该负载由 SparseRawStream 流式输出。
                    source.Seek(totalSz - chunkHdrSz, SeekOrigin.Current);
                    break;
                case ChunkTypeFill:
                    dataBytes = chunkSzBlocks * blockSize;
                    source.Seek(4, SeekOrigin.Current);
                    break;
                case ChunkTypeDontCare:
                    dataBytes = chunkSzBlocks * blockSize;
                    break;
                case ChunkTypeCrc32:
                    dataBytes = 0;
                    source.Seek(totalSz - chunkHdrSz, SeekOrigin.Current);
                    break;
                default:
                    throw new InvalidDataException($"Unknown sparse chunk type 0x{chunkType:X4}.");
            }

            if (dataBytes > 0)
            {
                totalSectors += (dataBytes + sectorSize - 1) / sectorSize;
            }
        }

        return totalSectors;
    }

    /// <summary>
    /// Reads and validates the sparse file header, returning (blockSize, chunkHeaderSize,
    /// totalChunks).
    /// <para>读取并校验稀疏文件头，返回（块大小、块头大小、总块数）。</para>
    /// </summary>
    private static (long BlockSize, int ChunkHeaderSize, int TotalChunks) ReadHeader(Stream source)
    {
        var header = new byte[28];
        ReadExactly(source, header, header.Length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            throw new InvalidDataException("Not an Android sparse image (bad magic).");
        }

        var fileHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        var chunkHdrSz = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        if (fileHdrSz < 28 || chunkHdrSz < 12)
        {
            throw new InvalidDataException("Sparse image has an unsupported header size.");
        }

        if (fileHdrSz > 28)
        {
            ReadExactly(source, new byte[fileHdrSz - 28], fileHdrSz - 28);
        }

        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (blockSize == 0)
        {
            throw new InvalidDataException("Sparse image has an invalid block size.");
        }

        var totalChunks = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
        return (blockSize, chunkHdrSz, totalChunks);
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream.
    /// <para>从流中精确读取指定字节数。</para>
    /// </summary>
    private static void ReadExactly(Stream source, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = source.Read(buffer, total, count - total);
            if (n == 0)
            {
                throw new EndOfStreamException("Sparse image stream ended unexpectedly.");
            }

            total += n;
        }
    }

    /// <summary>
    /// A stream that expands an Android sparse image into its plain (unsparsed) data: raw
    /// chunks are copied verbatim, fill chunks expand to the repeating 4-byte pattern, and
    /// dont-care chunks expand to zeros.
    /// <para>将 Android 稀疏镜像展开为普通（非稀疏）数据的流：raw 块原样复制，fill 块展开为
    /// 重复的 4 字节模式，dont-care 块展开为零。</para>
    /// </summary>
    public sealed class SparseRawStream : Stream
    {
        private readonly Stream _source;
        private readonly int _chunkHdrSz;
        private readonly long _blockSize;
        private readonly int _totalChunks;
        private readonly byte[] _chunkHeader;
        private int _chunkIndex;
        private ushort _chunkType;
        private long _chunkRemaining;
        private long _chunkTotalSize;
        private readonly byte[] _fillPattern = new byte[4];
        private bool _fillActive;
        private bool _finished;

        /// <summary>
        /// Initializes a new instance of the <see cref="SparseRawStream"/> class, reading the
        /// sparse header from the current position of the source stream.
        /// <para>初始化 <see cref="SparseRawStream"/> 类的新实例，从源流当前位置读取稀疏头。</para>
        /// </summary>
        /// <param name="source">The sparse image stream. 稀疏镜像流。</param>
        public SparseRawStream(Stream source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            (_blockSize, _chunkHdrSz, _totalChunks) = ReadHeader(_source);
            _chunkHeader = new byte[_chunkHdrSz];
        }

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

        /// <summary>Reads expanded sparse data. 读取展开后的稀疏数据。</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count && !_finished)
            {
                if (_chunkRemaining <= 0)
                {
                    if (!AdvanceToNextChunk())
                    {
                        break;
                    }

                    if (_chunkRemaining <= 0)
                    {
                        continue;
                    }
                }

                if (_chunkType == ChunkTypeRaw)
                {
                    var toRead = (int)Math.Min(count - total, _chunkRemaining);
                    var n = _source.Read(buffer, offset + total, toRead);
                    if (n == 0)
                    {
                        throw new EndOfStreamException("Sparse image stream ended unexpectedly.");
                    }

                    total += n;
                    _chunkRemaining -= n;
                }
                else
                {
                    // Fill / dont-care: generate the expanded bytes locally. The fill pattern
                    // repeats from the chunk start, so index by the bytes already consumed.
                    // Fill / dont-care：本地生成展开字节。填充模式自块起始重复，因此按已消费
                    // 字节数索引。
                    var toGenerate = (int)Math.Min(count - total, _chunkRemaining);
                    var consumed = _chunkTotalSize - _chunkRemaining;
                    if (_fillActive)
                    {
                        for (var i = 0; i < toGenerate; i++)
                        {
                            buffer[offset + total + i] = _fillPattern[(int)((consumed + i) % 4)];
                        }
                    }
                    else
                    {
                        Array.Clear(buffer, offset + total, toGenerate);
                    }
                    total += toGenerate;
                    _chunkRemaining -= toGenerate;
                }
            }

            return total;
        }

        /// <summary>Flush (no-op). 刷新（空操作）。</summary>
        public override void Flush() { }

        /// <summary>Seek (not supported). 寻址（不支持）。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>SetLength (not supported). 设置长度（不支持）。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Write (not supported). 写入（不支持）。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private bool AdvanceToNextChunk()
        {
            while (_chunkIndex < _totalChunks)
            {
                _chunkIndex++;
                _source.ReadExactly(_chunkHeader, 0, _chunkHdrSz);
                var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(_chunkHeader);
                var chunkSzBlocks = BinaryPrimitives.ReadUInt32LittleEndian(_chunkHeader.AsSpan(4));
                var totalSz = BinaryPrimitives.ReadUInt32LittleEndian(_chunkHeader.AsSpan(8));

                switch (chunkType)
                {
                    case ChunkTypeRaw:
                        _chunkType = ChunkTypeRaw;
                        _chunkTotalSize = chunkSzBlocks * _blockSize;
                        _chunkRemaining = _chunkTotalSize;
                        _fillActive = false;
                        // totalSz includes the header; the payload follows immediately.
                        // totalSz 含头部长度；负载紧随其后。
                        return true;
                    case ChunkTypeFill:
                        _source.ReadExactly(_fillPattern, 0, 4);
                        _chunkType = ChunkTypeFill;
                        _chunkTotalSize = chunkSzBlocks * _blockSize;
                        _chunkRemaining = _chunkTotalSize;
                        _fillActive = true;
                        return true;
                    case ChunkTypeDontCare:
                        _chunkType = ChunkTypeDontCare;
                        _chunkTotalSize = chunkSzBlocks * _blockSize;
                        _chunkRemaining = _chunkTotalSize;
                        _fillActive = false;
                        return true;
                    case ChunkTypeCrc32:
                        _source.Seek(totalSz - _chunkHdrSz, SeekOrigin.Current);
                        continue;
                    default:
                        throw new InvalidDataException($"Unknown sparse chunk type 0x{chunkType:X4}.");
                }
            }

            _finished = true;
            return false;
        }
    }
}