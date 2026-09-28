namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Read-only stream wrapper that checks a <see cref="CancellationToken"/> before every read so long copies abort
/// promptly; the wrapper never disposes the inner stream.
/// <para>只读流包装，在每次读取前检查 <see cref="CancellationToken"/>，让长时间复制能及时中止；包装器不会释放内层流。</para>
/// </summary>
/// <param name="source">Inner stream to read from. <para>读取来源的内层流。</para></param>
/// <param name="cancellationToken">Token checked before each read. <para>每次读取前检查的取消令牌。</para></param>
internal sealed class CancellationReadStream(Stream source, CancellationToken cancellationToken) : Stream
{
    private readonly CancellationToken _cancellationToken = cancellationToken;

    /// <inheritdoc/>
    public override bool CanRead => source.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return source.Read(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return source.Read(buffer);
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return source.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}