namespace UotanStudio.Firmware.Compress.Internal;

/// <summary>
/// 忽略 Dispose/Close 的流包装器：某些第三方压缩流（如 SharpCompress 的 ZlibStream）
/// 在释放时会关闭底层流，用本包装器保护调用方持有的流。
/// <para>Stream wrapper that ignores Dispose/Close: some third-party compression streams
/// (e.g. SharpCompress's ZlibStream) close the underlying stream on dispose; this wrapper
/// protects the caller-owned stream.</para>
/// <para>The span-based Read/Write are not overridden — the base <see cref="Stream"/> routes
/// them to the byte[] overloads, so no per-TFM #if is needed.</para>
/// </summary>
internal sealed class NonDisposingStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
}