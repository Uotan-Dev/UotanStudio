using System.Buffers;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.OtaDelta.Payload.IO;

/// <summary>
/// Read stream wrapper that accumulates a SHA-256 digest while reading and
/// verifies it against an expected hash on <see cref="Complete"/>.
/// <para>读取流包装器，在读取过程中累积 SHA-256 摘要，并在 <see cref="Complete"/> 时与期望哈希校验。</para>
/// </summary>
internal sealed class VerifyingReadStream : Stream
{
    private readonly Stream _input;
    private readonly HashingReadStream _hashing;
    private readonly byte[]? _expectedHash;
    private bool _completed;
    private const int BufferSize = 64 * 1024;
    private byte[]? _buffer;
    private int _bufferOffset;
    private int _bufferEnd;

    /// <summary>
    /// Initializes a non-owning verifying read wrapper.
    /// <para>初始化不拥有底层流的校验读取包装器。</para>
    /// </summary>
    internal VerifyingReadStream(Stream input, byte[]? expectedHash)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _expectedHash = expectedHash is { Length: > 0 } ? expectedHash : null;
        _hashing = new HashingReadStream(input, leaveOpen: true);
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _input.Length;
    public override long Position { get => _input.Position - (_bufferEnd - _bufferOffset); set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    /// <param name="buffer">Destination array receiving the digested bytes.
    /// <para>接收已计入摘要字节的目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where the copied bytes start.
    /// <para><paramref name="buffer"/> 中复制字节的起始索引。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The number of bytes copied, fewer than <paramref name="count"/> at a buffering boundary or
    /// end of input, <c>0</c> once the input is exhausted.
    /// <para>复制的字节数；在缓冲边界或输入结尾处少于 <paramref name="count"/>；输入耗尽后为 <c>0</c>。</para></returns>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    /// <param name="buffer">Destination span to fill; a span of at least 64 KiB is served straight from the
    /// hashing wrapper, otherwise the pooled buffer is refilled on demand. An empty span reads nothing.
    /// <para>要填充的目标区间；不小于 64 KiB 的区间由带摘要的包装器直接服务，否则按需重填池化缓冲区；
    /// 空区间不读取任何字节。</para></param>
    /// <returns>The number of bytes copied, fewer than the span length at a buffering boundary or end of
    /// input, <c>0</c> once the input is exhausted.
    /// <para>复制的字节数；在缓冲边界或输入结尾处少于区间长度；输入耗尽后为 <c>0</c>。</para></returns>
    /// <exception cref="ObjectDisposedException">The pooled buffer has been returned by disposal.
    /// <para>池化缓冲区已随释放归还。</para></exception>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (buffer.IsEmpty) return 0;
        if (_bufferOffset == _bufferEnd)
        {
            if (buffer.Length >= BufferSize) return _hashing.Read(buffer);
            if (!Refill()) return 0;
        }
        var count = Math.Min(buffer.Length, _bufferEnd - _bufferOffset);
        _buffer.AsSpan(_bufferOffset, count).CopyTo(buffer);
        _bufferOffset += count;
        return count;
    }

    /// <summary>Serves codec byte reads without a lock, seek, allocation or hash update per byte.
    /// <para>为编解码器的逐字节读取服务：每个字节都不加锁、不定位、不分配，也不单独更新摘要。</para></summary>
    /// <returns>The next byte of decompressed input as an <c>int</c> in the range 0 to 255, or <c>-1</c> once the input is exhausted.
    /// <para>解压输入的下一个字节，取值 0 到 255；输入耗尽后为 <c>-1</c>。</para></returns>
    /// <exception cref="ObjectDisposedException">The pooled buffer has been returned by disposal.
    /// <para>池化缓冲区已随释放归还。</para></exception>
    public override int ReadByte()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (_bufferOffset == _bufferEnd && !Refill()) return -1;
        return _buffer[_bufferOffset++];
    }

    private bool Refill()
    {
        _bufferOffset = 0;
        _bufferEnd = _hashing.Read(_buffer!, 0, BufferSize);
        return _bufferEnd > 0;
    }

    /// <summary>
    /// Drains unread input and verifies its SHA-256 digest.
    /// <para>读完剩余输入并验证其 SHA-256 摘要。</para>
    /// </summary>
    internal void Complete()
    {
        if (_completed) return;
        Span<byte> buffer = stackalloc byte[8192];
        while (Read(buffer) != 0) { }
        if (_input.Position != _input.Length)
        {
            throw new PartitionExtractionException($"Payload operation data is truncated: expected {_input.Length} bytes, read {_input.Position}", string.Empty);
        }

        var actual = _hashing.GetHashAndReset();
        _completed = true;
        if (_expectedHash is not null && !CryptographicOperations.FixedTimeEquals(actual, _expectedHash))
        {
            throw new PartitionExtractionException(
                $"Data SHA256 hash verification failed. Expected: {Convert.ToHexString(_expectedHash)}, Actual: {Convert.ToHexString(actual)}",
                string.Empty);
        }
    }

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
            _hashing.Dispose();
        }
        base.Dispose(disposing);
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
    /// <param name="offset">Displacement ignored; seeking would skip bytes out of the SHA-256 digest.
    /// <para>被忽略的偏移量；定位会让字节跳过 SHA-256 摘要计算。</para></param>
    /// <param name="origin">Reference point ignored together with the call. <para>随调用一并被忽略的参照点。</para></param>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="value">Requested length ignored; the input length belongs to the wrapped operation stream.
    /// <para>被忽略的请求长度；输入长度归被包装的操作流所有。</para></param>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="buffer">Array never touched, since the stream is read-only. <para>从不接触的数组，因为该流只读。</para></param>
    /// <param name="offset">Index ignored together with the call. <para>随调用一并被忽略的索引。</para></param>
    /// <param name="count">Byte count ignored together with the call. <para>随调用一并被忽略的字节数。</para></param>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}