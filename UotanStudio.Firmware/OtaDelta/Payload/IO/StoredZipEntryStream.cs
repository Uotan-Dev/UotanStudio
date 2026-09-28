
namespace UotanStudio.Firmware.OtaDelta.Payload.IO;

/// <summary>
/// <para>对容器文件中某个 STORED（未压缩）ZIP 条目原始数据的可随机访问只读视图。
/// 载荷逻辑偏移 [0, length) 映射到容器文件偏移 [dataStart, dataStart + length)；
/// 读取使用 <see cref="RandomAccess"/> 位置化 I/O，因此每个句柄可独立寻址，
/// 可被各分区专属的提取句柄安全并发使用，而无需先物化到临时文件。</para>
/// A seekable, read-only view of a STORED (uncompressed) ZIP entry's raw data within its containing
/// archive. Logical payload offsets [0, length) map to file offsets [dataStart, dataStart + length);
/// reads use positional I/O via <see cref="RandomAccess"/>, so each handle is independently seekable
/// and safe for the per-partition extractors to use concurrently, without materializing a temp copy.
/// </summary>
internal sealed class StoredZipEntryStream : Stream
{
    private readonly FileStream _outer;
    private readonly long _dataStart;
    private readonly long _length;
    private long _position;

    /// <summary>
    /// <para>在容器文件中定位到 STORED 条目数据起点，并为后续读取保持一个只读句柄。</para>
    /// Locates the STORED entry's data start in the archive and keeps a read-only handle for reads.
    /// </summary>
    /// <param name="zipPath"><para>容器（ZIP）文件的路径。</para>The archive (ZIP) file path.</param>
    /// <param name="dataStart"><para>条目原始数据在容器文件内的绝对字节偏移。</para>Absolute byte offset of the entry data within the archive.</param>
    /// <param name="length"><para>载荷（条目未压缩）字节数。</para>Payload (entry uncompressed) byte count.</param>
    internal StoredZipEntryStream(string zipPath, long dataStart, long length)
    {
        _length = length;
        _dataStart = dataStart;
        _outer = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length)
        {
            return 0;
        }

        var target = buffer[..(int)Math.Min(buffer.Length, _length - _position)];
        var read = RandomAccess.Read(_outer.SafeFileHandle, target, _dataStart + _position);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position >= _length)
        {
            return 0;
        }

        var read = await RandomAccess.ReadAsync(
            _outer.SafeFileHandle,
            buffer[..(int)Math.Min(buffer.Length, _length - _position)],
            _dataStart + _position,
            cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return Position;
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _outer.Dispose();
        }

        base.Dispose(disposing);
    }
}