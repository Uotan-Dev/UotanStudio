namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Write stream wrapper that checks a <see cref="CancellationToken"/> before and after every write, plus before flush
/// or resize, so long builds abort promptly even when a single write consumes the whole remaining payload; positioning
/// is passed through and the wrapper never disposes the inner stream.
/// <para>写入流包装，在每次写入前后、以及刷新或改变长度前检查 <see cref="CancellationToken"/>，即使单次写入就消耗完
/// 全部剩余数据也能及时中止；定位操作直接透传，包装器不会释放内层流。</para>
/// </summary>
/// <param name="inner">Inner stream to write to. <para>写入目标内层流。</para></param>
/// <param name="cancellationToken">Token checked before and after each write. <para>每次写入前后都检查的取消令牌。</para></param>
internal sealed class CancellationWriteStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    private readonly CancellationToken _cancellationToken = cancellationToken;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc/>
    public override long Length => inner.Length;

    /// <inheritdoc/>
    public override long Position { get => inner.Position; set => inner.Position = value; }

    /// <inheritdoc/>
    public override void Flush()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        inner.Flush();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        inner.SetLength(value);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        inner.Write(buffer);
        _cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _cancellationToken.ThrowIfCancellationRequested();
    }
}