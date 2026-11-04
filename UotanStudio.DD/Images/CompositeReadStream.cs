namespace UotanStudio.DD.Images;

/// <summary>
/// Read-only facade that delegates to a decoder and disposes the resources it was built from.
/// <para>只读外观流，委托给解码器并在释放时同时释放构建它所用的资源。</para>
/// </summary>
internal sealed class CompositeReadStream : Stream
{
    private readonly Stream _primary;
    private readonly IDisposable[] _owned;

    /// <summary>
    /// Wraps a decoder stream together with the resources that must be released after it.
    /// <para>包装解码流及其之后必须释放的资源。</para>
    /// </summary>
    /// <param name="primary">Stream that supplies the bytes.<para>提供字节的流。</para></param>
    /// <param name="owned">Additional resources disposed after <paramref name="primary"/>.
    /// <para>在 <paramref name="primary"/> 之后释放的附加资源。</para></param>
    public CompositeReadStream(Stream primary, params IDisposable[] owned)
    {
        _primary = primary;
        _owned = owned;
    }

    /// <inheritdoc />
    public override bool CanRead => _primary.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _primary.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _primary.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _primary.Position;
        set => _primary.Position = value;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _primary.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _primary.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _primary.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _primary.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _primary.Seek(offset, origin);

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _primary.Dispose();
            foreach (var resource in _owned)
            {
                resource.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}