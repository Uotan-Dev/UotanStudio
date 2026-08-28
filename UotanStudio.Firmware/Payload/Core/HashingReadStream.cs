using System.Security.Cryptography;
using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Incrementally hashes bytes as they are read and verifies the digest on completion.
/// <para>在读取字节时增量计算哈希，并在完成时验证摘要。</para>
/// </summary>
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
    /// <param name="input">Input stream to hash.<para>要计算哈希的输入流。</para></param>
    /// <param name="expectedHash">Expected SHA-256 digest, or an empty value to skip comparison.<para>预期 SHA-256 摘要；空值表示跳过比较。</para></param>
    internal HashingReadStream(Stream input, byte[]? expectedHash)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _expectedHash = expectedHash is { Length: > 0 } ? expectedHash : null;
    }

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _input.Length;
    /// <inheritdoc />
    public override long Position { get => _input.Position; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _input.Read(buffer, offset, count);
        if (read > 0) _hash.AppendData(buffer, offset, read);
        return read;
    }

    /// <inheritdoc />
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
    /// <exception cref="PartitionExtractionException">The input is truncated or its digest differs.<para>输入被截断或摘要不匹配时抛出。</para></exception>
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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}