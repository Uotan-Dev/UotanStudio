using System.Security.Cryptography;
using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

internal sealed class HashingReadStream : Stream
{
    private readonly Stream _input;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[]? _expectedHash;
    private bool _completed;

    /// <summary>
    /// Initializes a non-owning hashing read wrapper.
    /// <para>初始化不拥有底层流的哈希读取包装器。</para>
    /// </summary>
    internal HashingReadStream(Stream input, byte[]? expectedHash)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _expectedHash = expectedHash is { Length: > 0 } ? expectedHash : null;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _input.Length;
    public override long Position { get => _input.Position; set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _input.Read(buffer, offset, count);
        if (read > 0) _hash.AppendData(buffer, offset, read);
        return read;
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        var read = _input.Read(buffer);
        if (read > 0) _hash.AppendData(buffer[..read]);
        return read;
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

        var actual = _hash.GetHashAndReset();
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
        if (disposing) _hash.Dispose();
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
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
