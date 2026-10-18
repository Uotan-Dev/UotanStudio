using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Read-only stream wrapper that folds every byte range returned by the inner stream into an
/// incremental SHA-256 digest. Write operations are unsupported and seeking is blocked, so the
/// digest covers exactly the bytes handed to the caller.
/// <para>只读流包装器，把底层流每次返回的字节范围并入增量 SHA-256 摘要。不支持写入操作，并且禁止
/// 定位，因此摘要恰好覆盖交给调用方的字节。</para>
/// </summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly DigestAccumulator _hash = Sha256.CreateAccumulator();
    private long _bytesRead;
    private bool _disposed;

    /// <summary>
    /// Creates a hashing read stream wrapper.
    /// <para>创建哈希读取流包装器。</para>
    /// </summary>
    /// <param name="inner">Readable stream the bytes are taken from.
    /// <para>读取字节的来源流。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this wrapper leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本包装器后 <paramref name="inner"/> 仍保持打开。</para></param>
    /// <exception cref="ArgumentNullException"><c>inner</c> is <see langword="null"/>.
    /// <para><c>inner</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>inner</c> cannot be read.
    /// <para><c>inner</c> 不可读。</para></exception>
    public HashingReadStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The stream must be readable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Gets the total number of bytes returned to callers since construction.
    /// <para>获取自构造以来已交给调用方的字节总数。</para>
    /// </summary>
    /// <value>The cumulative digested byte count; reading a digest does not reset it.
    /// <para>累计参与哈希的字节数；读取摘要不会使其重置。</para></value>
    public long BytesRead => _bytesRead;

    /// <summary>
    /// Returns the SHA-256 hex digest of the data read so far.
    /// <para>返回当前累积数据的 SHA-256 十六进制摘要。</para>
    /// </summary>
    /// <returns>The 64-character lowercase hexadecimal digest; the accumulated state is cleared, so later reads start a new digest.
    /// <para>64 位小写十六进制摘要；调用后累积状态被清空，之后的读取将从新的摘要重新开始。</para></returns>
    public string GetHashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    /// <summary>
    /// Returns the raw SHA-256 digest bytes of the data read so far.
    /// <para>返回当前累积数据的 SHA-256 原始字节摘要。</para>
    /// </summary>
    /// <returns>The 32-byte digest; the accumulated state is cleared, so later reads start a new digest.
    /// <para>32 字节摘要；调用后累积状态被清空，之后的读取将从新的摘要重新开始。</para></returns>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

    /// <summary>The wrapper is readable until it is disposed.
    /// <para>包装器在释放前均可读。</para></summary>
    /// <value><c>true</c> while undisposed. <para>未释放时为 <c>true</c>。</para></value>
    public override bool CanRead => !_disposed;

    /// <summary>Always <c>false</c>: seeking would let bytes skip the digest, so it is blocked.
    /// <para>恒为 <c>false</c>：定位会让字节绕过摘要计算，因此被禁止。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanSeek => false;

    /// <summary>Always <c>false</c>: the wrapper only digests bytes read from the source.
    /// <para>恒为 <c>false</c>：包装器只对从来源流读出的字节做摘要。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanWrite => false;

    /// <summary>The inner stream's length, reported without touching the digest state.
    /// <para>底层流的长度，读取时不影响摘要状态。</para></summary>
    /// <value>The value reported by the inner stream, which throws when that stream has no length support.
    /// <para>由底层流报告的值；当该流不支持长度时会抛出异常。</para></value>
    public override long Length => _inner.Length;

    /// <summary>The inner stream's position, exposed read-only so the digest keeps covering every byte handed out.
    /// <para>底层流的位置，仅以只读方式暴露，从而保证摘要覆盖每个交给调用方的字节。</para></summary>
    /// <value>Getting forwards to the inner stream; setting always throws <see cref="NotSupportedException"/>.
    /// <para>读取时转发到底层流；设置始终抛出 <see cref="NotSupportedException"/>。</para></value>
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    /// <summary>Reads bytes from the stream into the buffer.<para>从流中读取字节到缓冲区。</para></summary>
    /// <param name="buffer">Destination array receiving the bytes; the digest covers exactly the range starting at <paramref name="offset"/> and spanning the returned byte count.
    /// <para>接收字节的目标数组；摘要覆盖的范围恰好是从 <paramref name="offset"/> 起、长度等于返回字节数的区间。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where the inner stream starts writing.
    /// <para>底层流在 <paramref name="buffer"/> 中开始写入的索引。</para></param>
    /// <param name="count">Maximum number of bytes requested from the inner stream.
    /// <para>向底层流请求的最大字节数。</para></param>
    /// <returns>The number of bytes returned by the inner stream and folded into the digest; <c>0</c> at end of stream.
    /// <para>底层流返回并入摘要的字节数；流结束时为 <c>0</c>。</para></returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.Append(buffer.AsSpan(offset, read));
            _bytesRead = checked(_bytesRead + read);
        }
        return read;
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    /// <param name="buffer">Destination span receiving the bytes; the digest covers only the leading
    /// part of the span actually filled.
    /// <para>接收字节的目标区间；摘要仅覆盖该区间中实际被填充的前缀。</para></param>
    /// <returns>The number of bytes returned by the inner stream and folded into the digest; <c>0</c> at
    /// end of stream. <para>底层流返回并入摘要的字节数；流结束时为 <c>0</c>。</para></returns>
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0)
        {
            _hash.Append(buffer[..read]);
            _bytesRead = checked(_bytesRead + read);
        }
        return read;
    }

    /// <summary>
    /// Reads bytes from the stream asynchronously.
    /// <para>异步从流中读取字节。</para>
    /// </summary>
    /// <param name="buffer">Destination memory handed to the inner stream unchanged; only the range the inner
    /// stream returns is folded into the digest. <para>原样交给底层流的目标内存区；只有底层流实际返回的范围会并入摘要。</para></param>
    /// <param name="cancellationToken">Token forwarded to the inner stream; this wrapper does not test it.
    /// <para>转发给底层流的取消令牌；本包装器自身不检查该令牌。</para></param>
    /// <returns>A task producing the number of bytes stored at the start of <paramref name="buffer"/>; <c>0</c>
    /// marks the end of the inner stream and adds nothing to the digest.
    /// <para>任务结果为写入 <paramref name="buffer"/> 起始处的字节数；<c>0</c> 表示底层流已结束，不会向摘要追加内容。</para></returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.Append(buffer[..read].Span);
            _bytesRead = checked(_bytesRead + read);
        }
        return read;
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public override void Flush() { }
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="offset">Displacement ignored, since seeking would let bytes bypass the digest.
    /// <para>被忽略的偏移量；定位会让字节绕过摘要计算。</para></param>
    /// <param name="origin">Reference point ignored together with the call. <para>随调用一并被忽略的参照点。</para></param>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="value">Requested length ignored, since the wrapper exposes no write path.
    /// <para>被忽略的请求长度；包装器不暴露写入路径。</para></param>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="buffer">Buffer never touched, since the wrapper digests reads only.
    /// <para>从不接触的缓冲区，因为包装器只对读取做摘要。</para></param>
    /// <param name="offset">Index rejected together with the call. <para>随调用一并被拒绝的索引。</para></param>
    /// <param name="count">Byte count rejected together with the call. <para>随调用一并被拒绝的字节数。</para></param>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _hash.Dispose();
            if (!_leaveOpen) _inner.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}