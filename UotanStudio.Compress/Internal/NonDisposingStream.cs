namespace UotanStudio.Compress.Internal;

/// <summary>
/// Wraps a stream and forwards every member except disposal, so a codec can leave the caller's stream open.
/// <para>包装一个流并转发除释放外的所有成员，使编解码器不会关闭调用方的流。</para>
/// </summary>
/// <param name="inner">The wrapped stream; disposal is ignored. <para>被包装的流；释放操作被忽略。</para></param>
public sealed class NonDisposingStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner;

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;
    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;
    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;
    /// <inheritdoc />
    public override long Length => _inner.Length;
    /// <inheritdoc />
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    /// <inheritdoc />
    public override void Flush() => _inner.Flush();
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    /// <inheritdoc />
    public override int ReadByte() => _inner.ReadByte();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    /// <inheritdoc />
    public override void WriteByte(byte value) => _inner.WriteByte(value);
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);
}