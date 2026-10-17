using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Read-only pass-through that folds every byte read through it into an incremental SHA-256 digest while keeping
/// the inner stream's position, length and seeking intact. It measures a stream that a parser already consumes, so
/// a caller can report byte progress and a digest over the exact bytes the parser saw without buffering them.
/// <para>只读直通包装流：把经其读出的每个字节并入增量 SHA-256 摘要，同时保持底层流的位置、长度与定位能力不变。
/// 它度量解析器已经消费的流，使调用方无需缓冲即可对解析器实际读取的字节上报字节进度与摘要。</para>
/// </summary>
internal sealed class ReadThroughHashingStream : Stream
{
    private readonly Stream inner;
    private readonly bool leaveOpen;
    private readonly DigestAccumulator hash = Sha256.CreateAccumulator();
    private readonly Action<int>? bytesRead;
    private bool disposed;
    private string? digestHex;

    /// <summary>
    /// Wraps a readable stream so its bytes are digested as they pass through.
    /// <para>包装可读流，使其字节在通过时被计入摘要。</para>
    /// </summary>
    /// <param name="inner">Readable stream whose bytes are digested.
    /// <para>其字节会被计入摘要的可读流。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this wrapper leaves
    /// <paramref name="inner"/> open. <para>为 <see langword="true"/> 时，释放本包装器后 <paramref name="inner"/>
    /// 仍保持打开。</para></param>
    /// <param name="bytesRead">Optional sink receiving every byte count returned to the caller; may be
    /// <see langword="null"/>. <para>可选回调，接收每个交给调用方的字节数；可为 <see langword="null"/>。</para></param>
    internal ReadThroughHashingStream(Stream inner, bool leaveOpen, Action<int>? bytesRead = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(inner));
        }
        this.inner = inner;
        this.leaveOpen = leaveOpen;
        this.bytesRead = bytesRead;
    }

    /// <summary>
    /// Gets the accumulated digest without clearing it, so later reads keep extending the same value. The value
    /// stays readable after this wrapper was disposed by a downstream parser, because disposal caches it first.
    /// <para>获取当前累计摘要且不清空，之后的读取会继续扩展同一摘要。下游解析器先释放本包装器后该值仍可读取，
    /// 因为释放时会先缓存摘要。</para>
    /// </summary>
    /// <value>The 64-character lowercase hexadecimal SHA-256 of everything read through this wrapper.
    /// <para>经本包装器读出的全部字节的 64 字符小写十六进制 SHA-256。</para></value>
    internal string DigestHex => digestHex ??= Convert.ToHexStringLower(hash.GetCurrentHash());

    /// <inheritdoc/>
    public override bool CanRead => !disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !disposed && inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer), buffer);

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Span);

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Appends the bytes the inner stream returned and forwards the byte count to the sink.
    /// <para>把底层流返回的字节追加进摘要，并把字节数转发给回调。</para>
    /// </summary>
    /// <param name="read">Byte count returned by the inner stream.
    /// <para>底层流返回的字节数。</para></param>
    /// <param name="buffer">Buffer the bytes were stored in.
    /// <para>存放这些字节的缓冲区。</para></param>
    /// <returns>The same count, so the wrapper stays transparent.
    /// <para>同一个字节数，使包装器保持透明。</para></returns>
    private int Count(int read, ReadOnlySpan<byte> buffer)
    {
        if (read > 0)
        {
            hash.Append(buffer[..read]);
            bytesRead?.Invoke(read);
        }
        return read;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            digestHex ??= Convert.ToHexStringLower(hash.GetCurrentHash());
            hash.Dispose();
            if (!leaveOpen)
            {
                inner.Dispose();
            }
            disposed = true;
        }
        base.Dispose(disposing);
    }
}