using System.Buffers.Binary;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Replays an Android sparse image to flash the way <c>write_sparse_lba</c> does: the target range is erased
    /// through ERASE_LBA first, then every raw and fill chunk is written as WRITE_LBA transactions while
    /// don't-care chunks only advance the LBA, which the erase already zeroed.
    /// <para>按 <c>write_sparse_lba</c> 的方式把 Android 稀疏镜像回放到 flash：先用 ERASE_LBA 擦除目标范围，
    /// 再把每个 raw 与 fill 数据块作为 WRITE_LBA 事务写出，don't-care 数据块只推进 LBA（该区域已被擦除）。
    /// </para>
    /// </summary>
    /// <remarks>
    /// The image is parsed and checksum-validated before the first erase, so a malformed sparse file leaves the
    /// device untouched. Raw payloads are streamed from the parsed chunk data providers through bounded
    /// <see cref="RkdlConstants.TransferChunkSize"/> buffers, so neither the expanded image nor a single chunk is
    /// held in memory whole. The declared logical size is bounded by the device capacity reported by
    /// READ_FLASH_INFO, and the chunk count by <see cref="RkdlConstants.MaxSparseChunks"/>.
    /// <para>镜像在第一次擦除之前完成解析与校验和验证，因此畸形的稀疏文件不会改动设备。raw 载荷从解析后的数据块
    /// 数据提供器经有界的 <see cref="RkdlConstants.TransferChunkSize"/> 缓冲流式转出，展开镜像与单个数据块都不会整体
    /// 驻留内存。声明的逻辑大小以 READ_FLASH_INFO 报告的设备容量为上界，数据块数量以
    /// <see cref="RkdlConstants.MaxSparseChunks"/> 为上界。</para>
    /// </remarks>
    /// <param name="address">The first sector the image is written to. <para>镜像写入的首个扇区。</para></param>
    /// <param name="sparseStream">The caller-owned sparse image stream, positioned at the file header and left open
    /// by this call; it must stay readable for the whole replay. <para>调用方持有的稀疏镜像流，定位在文件头部，本调用
    /// 不会释放它；回放期间必须保持可读。</para></param>
    /// <param name="targetBytes">Byte length of the range to erase, or <see langword="null"/> to erase the image's
    /// declared logical size rounded up to a sector boundary. <para>要擦除的范围字节数；为
    /// <see langword="null"/> 时擦除镜像声明的逻辑大小并向上取整到扇区边界。</para></param>
    /// <param name="progress">Optional receiver of cumulative logical image bytes covered, raw and fill writes and
    /// don't-care advances alike. <para>可选的累计已覆盖逻辑镜像字节数接收器，raw、fill 写入与 don't-care 推进都计入。</para></param>
    /// <param name="cancellationToken">Checked between chunks and before each transfer. <para>在数据块之间与每次传输前检查。</para></param>
    /// <returns>The number of logical image bytes covered, including the skipped don't-care regions.
    /// <para>覆盖的逻辑镜像字节数，包含被跳过的 don't-care 区域。</para></returns>
    /// <exception cref="RkdlException">The address or erased range leaves the device geometry, READ_FLASH_INFO failed,
    /// the sparse image is malformed or exceeds the bounds, or a WRITE_LBA or ERASE_LBA transaction failed.
    /// <para>地址或擦除范围超出设备几何、READ_FLASH_INFO 失败、稀疏镜像畸形或超界，或某条 WRITE_LBA 或 ERASE_LBA
    /// 事务失败。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="sparseStream"/> is <see langword="null"/>.
    /// <para><paramref name="sparseStream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before or between transfers.
    /// <para>令牌在传输开始前或两次传输之间被取消。</para></exception>
    public long WriteSparseImage(long address, Stream sparseStream, long? targetBytes = null,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sparseStream);
        if (address is < 0 or > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"LBA address out of range: {address}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var geometry = RequireFlashGeometry();
        var deviceBytes = geometry.SectorCount * RkdlConstants.SectorSize;

        SparseFile sparse;
        try
        {
            sparse = SparseReader.FromStream(sparseStream, validateCrc: true, verbose: false,
                RkdlConstants.MaxSparseChunks, cancellationToken, deviceBytes);
        }
        catch (InvalidDataException error)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Sparse image rejected: {error.Message}", error);
        }

        using (sparse)
        {
            var blockSize = (long)sparse.Header.BlockSize;
            var logicalBytes = (long)sparse.Header.TotalBlocks * blockSize;
            var eraseBytes = SectorsToBytes(SectorsForBytes(targetBytes ?? logicalBytes));
            EraseLbaRange(address, eraseBytes / RkdlConstants.SectorSize, null, cancellationToken);
            CommTrace.State("rkdl",
                $"Sparse replay at LBA {address}: {logicalBytes} declared B in {sparse.Chunks.Count} chunks, {eraseBytes} B erased.");

            var currentSector = address;
            var covered = 0L;
            foreach (var chunk in sparse.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkBytes = (long)chunk.Header.ChunkSize * blockSize;
                switch ((ChunkType)chunk.Header.ChunkType)
                {
                    case ChunkType.Raw when chunk.DataProvider is not null:
                        WriteSparseSource(currentSector,
                            SparseWriteSource.FromProvider(chunk.DataProvider), chunkBytes, covered, progress, cancellationToken);
                        currentSector += SectorsForBytes(chunkBytes);
                        covered += chunkBytes;
                        break;

                    case ChunkType.Fill:
                        WriteSparseSource(currentSector,
                            SparseWriteSource.FromFill(chunk.FillValue, chunkBytes), chunkBytes, covered, progress, cancellationToken);
                        currentSector += SectorsForBytes(chunkBytes);
                        covered += chunkBytes;
                        break;

                    case ChunkType.DontCare:
                        currentSector += SectorsForBytes(chunkBytes);
                        covered += chunkBytes;
                        progress?.Report(covered);
                        break;

                    default:
                        break;
                }
            }

            return covered;
        }
    }

    /// <summary>
    /// Writes one sparse chunk's expanded bytes, rounding the sector count the same way <c>write_sparse_lba</c>
    /// does so the next chunk starts on the sector the vendor tool would use.
    /// <para>写出一个稀疏数据块展开后的字节，并按 <c>write_sparse_lba</c> 的方式取整扇区数，使下一个数据块从厂商工具
    /// 会使用的扇区开始。</para>
    /// </summary>
    /// <param name="sector">The first sector of the chunk. <para>数据块的首个扇区。</para></param>
    /// <param name="source">The chunk's byte source; disposed by this call. <para>数据块的字节源；由本调用释放。</para></param>
    /// <param name="length">The chunk's expanded byte length. <para>数据块展开后的字节长度。</para></param>
    /// <param name="covered">Logical image bytes covered before this chunk, added to every report so the
    /// receiver sees one monotonic total across chunks. <para>本数据块之前已覆盖的逻辑镜像字节数，会加到每次上报上，
    /// 使接收器在跨数据块时看到单调递增的单一总量。</para></param>
    /// <param name="progress">Optional progress receiver. <para>可选进度接收器。</para></param>
    /// <param name="cancellationToken">Checked before each transfer. <para>每次传输前检查。</para></param>
    private void WriteSparseSource(long sector, SparseWriteSource source, long length, long covered,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using (source)
        {
            var reporter = progress is null ? null : new OffsetProgress(progress, covered);
            WriteLBA(sector, source, length, reporter, cancellationToken, RkdlRwMethod.Image);
        }
    }

    /// <summary>
    /// Rounds a byte count up to whole 512-byte sectors. <para>把字节数向上取整到整 512 字节扇区。</para>
    /// </summary>
    /// <param name="bytes">The byte count; must be non-negative. <para>字节数；必须非负。</para></param>
    /// <returns>The sector count. <para>扇区数。</para></returns>
    private static long SectorsForBytes(long bytes)
        => bytes <= 0 ? 0 : (bytes + RkdlConstants.SectorSize - 1) / RkdlConstants.SectorSize;

    /// <summary>
    /// Converts a sector count to bytes. <para>把扇区数换算为字节。</para>
    /// </summary>
    /// <param name="sectors">The sector count. <para>扇区数。</para></param>
    /// <returns>The byte count. <para>字节数。</para></returns>
    private static long SectorsToBytes(long sectors) => sectors * RkdlConstants.SectorSize;
}

/// <summary>
/// Sequential read-only byte source for one sparse chunk, either the chunk's raw payload or its repeated fill
/// pattern. The read cursor only moves forward, so the replay writes each chunk in a single bounded pass.
/// <para>单个稀疏数据块的顺序只读字节源：要么是该块的原始载荷，要么是其重复的填充模式。读取游标只前进，因此回放以
/// 一次有界遍历写出每个数据块。</para>
/// </summary>
internal sealed class SparseWriteSource : Stream
{
    private readonly ISparseDataProvider? _provider;
    private readonly byte[] _fillPattern;
    private readonly long _length;
    private long _position;

    private SparseWriteSource(ISparseDataProvider? provider, byte[] fillPattern, long length)
    {
        _provider = provider;
        _fillPattern = fillPattern;
        _length = length;
    }

    /// <summary>
    /// Creates a source over a chunk's raw payload. <para>创建覆盖数据块原始载荷的字节源。</para>
    /// </summary>
    /// <param name="provider">The chunk's data provider; its lifetime stays with the caller.
    /// <para>数据块的数据提供器；其生命周期仍属于调用方。</para></param>
    /// <returns>A source that returns at most <see cref="ISparseDataProvider.Length"/> bytes and never re-reads.
    /// <para>最多返回 <see cref="ISparseDataProvider.Length"/> 字节且不会重复读取的字节源。</para></returns>
    public static SparseWriteSource FromProvider(ISparseDataProvider provider)
        => new(provider, [], provider.Length);

    /// <summary>
    /// Creates a source repeating a little-endian 32-bit fill value. <para>创建重复小端 32 位填充值的字节源。</para>
    /// </summary>
    /// <param name="fillValue">The pattern stored in the fill chunk header. <para>填充数据块中存储的模式。</para></param>
    /// <param name="length">The chunk's expanded byte length. <para>数据块展开后的字节长度。</para></param>
    /// <returns>A source that yields <paramref name="length"/> bytes of the repeating pattern.
    /// <para>产出 <paramref name="length"/> 字节重复模式的字节源。</para></returns>
    public static SparseWriteSource FromFill(uint fillValue, long length) => new(null, FillPattern(fillValue), length);

    /// <summary>Always <c>true</c>: the source yields chunk bytes. <para>恒为 <c>true</c>：该源产出数据块字节。</para></summary>
    /// <value><c>true</c>. <para><c>true</c>。</para></value>
    public override bool CanRead => true;

    /// <summary>Always <c>false</c>: reads are strictly sequential. <para>恒为 <c>false</c>：读取严格顺序。</para></summary>
    /// <value><c>false</c>. <para><c>false</c>。</para></value>
    public override bool CanSeek => false;

    /// <summary>Always <c>false</c>: the source is read-only. <para>恒为 <c>false</c>：该源只读。</para></summary>
    /// <value><c>false</c>. <para><c>false</c>。</para></value>
    public override bool CanWrite => false;

    /// <summary>The chunk's expanded byte length. <para>数据块展开后的字节长度。</para></summary>
    /// <value>The total number of bytes the source yields. <para>该源产出的字节总数。</para></value>
    public override long Length => _length;

    /// <summary>The number of bytes already consumed. <para>已消费的字节数。</para></summary>
    /// <value>A non-negative offset into the chunk, never decreasing; setting it is refused.
    /// <para>数据块内非负的偏移，永不减小；设置它会被拒绝。</para></value>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("SparseWriteSource is sequential and read-only.");
    }

    /// <summary>
    /// Fills the buffer with the next chunk bytes. <para>用后续数据块字节填充缓冲区。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>目标区间。</para></param>
    /// <returns>The number of bytes produced, or <c>0</c> once the chunk is exhausted.
    /// <para>产出的字节数；数据块耗尽后为 <c>0</c>。</para></returns>
    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, remaining);
        var destination = buffer[..count];
        if (_provider is not null)
        {
            var read = _provider.Read(_position, destination);
            if (read <= 0)
            {
                return 0;
            }

            _position += read;
            return read;
        }

        for (var index = 0; index < count; index++)
        {
            destination[index] = _fillPattern[(int)((_position + index) & 3)];
        }

        _position += count;
        return count;
    }

    /// <summary>
    /// Fills the buffer with the next chunk bytes. <para>用后续数据块字节填充缓冲区。</para>
    /// </summary>
    /// <param name="buffer">Destination array. <para>目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where writing starts.
    /// <para><paramref name="buffer"/> 中开始写入的索引。</para></param>
    /// <param name="count">Maximum number of bytes requested. <para>请求的最大字节数。</para></param>
    /// <returns>The number of bytes produced, or <c>0</c> once the chunk is exhausted.
    /// <para>产出的字节数；数据块耗尽后为 <c>0</c>。</para></returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Throws NotSupportedException: the source is sequential. <para>抛出 NotSupportedException：该源是顺序的。</para></summary>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="origin">Ignored. <para>被忽略。</para></param>
    /// <returns>Never returns. <para>永不返回。</para></returns>
    /// <exception cref="NotSupportedException">Always thrown. <para>总是抛出。</para></exception>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("SparseWriteSource is sequential and read-only.");

    /// <summary>Has no effect: the source holds no buffered data. <para>无效果：该源不持有缓冲数据。</para></summary>
    public override void Flush()
    {
    }

    /// <summary>Throws NotSupportedException: the source is read-only. <para>抛出 NotSupportedException：该源只读。</para></summary>
    /// <param name="value">Ignored. <para>被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>总是抛出。</para></exception>
    public override void SetLength(long value) => throw new NotSupportedException("SparseWriteSource is read-only.");

    /// <summary>Throws NotSupportedException: the source is read-only. <para>抛出 NotSupportedException：该源只读。</para></summary>
    /// <param name="buffer">Ignored. <para>被忽略。</para></param>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="count">Ignored. <para>被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>总是抛出。</para></exception>
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("SparseWriteSource is read-only.");

    private static byte[] FillPattern(uint fillValue)
    {
        var pattern = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, fillValue);
        return pattern;
    }
}

/// <summary>
/// Adds a fixed base to every reported value, so a per-chunk byte count is reported as a cumulative total of the
/// whole sparse replay.
/// <para>给每次上报的数值加上固定基数，使按数据块计数的字节数按整次稀疏回放的累计总量上报。</para>
/// </summary>
/// <param name="inner">The receiver that gets the shifted value. <para>接收换算后数值的接收器。</para></param>
/// <param name="offset">The base added to every report. <para>加到每次上报上的基数。</para></param>
internal sealed class OffsetProgress(IProgress<long> inner, long offset) : IProgress<long>
{
    /// <summary>
    /// Forwards the value shifted by the base. <para>把数值加上基数后转发。</para>
    /// </summary>
    /// <param name="value">The per-chunk byte count. <para>按数据块计数的字节数。</para></param>
    public void Report(long value) => inner.Report(offset + value);
}