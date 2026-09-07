using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Crypto;

public interface ICryptoProvider
{
    private static readonly int MaximumBufferedFallbackBytes = Array.MaxLength;

    /// <summary>
    /// Decrypts data using AES-256-CBC mode.
    /// <para>使用 AES-256-CBC 模式解密数据。</para>
    /// </summary>
    byte[] Decrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// Encrypts data using AES-256-CBC mode.
    /// <para>使用 AES-256-CBC 模式加密数据。</para>
    /// </summary>
    byte[] Encrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// Creates a read stream that consumes ciphertext from the input stream and yields decrypted data.
    /// <para>创建从输入流读取密文并输出解密密文的读取流。</para>
    /// </summary>
    Stream CreateDecryptStream(Stream input, byte[] key, byte[] iv, bool leaveOpen = true)
        => new BufferedDecryptStream(input, key, iv, leaveOpen, Decrypt, MaximumBufferedFallbackBytes);

    /// <summary>
    /// Creates a write stream that encrypts written data into the output stream.
    /// <para>创建向输出流写入加密密文的写入流。</para>
    /// </summary>
    Stream CreateEncryptStream(Stream output, byte[] key, byte[] iv, bool leaveOpen = true)
        => new BufferedEncryptStream(output, key, iv, leaveOpen, Encrypt, MaximumBufferedFallbackBytes);
}

public interface IKeyProvider
{
    /// <summary>
    /// Gets the encryption key and initialization vector for the specified region.
    /// <para>获取指定区域的加密密钥和初始化向量。</para>
    /// </summary>
    (byte[] Key, byte[] Iv)? GetKey(RegionType regionType);
}

public sealed class VersionKeyProvider : IKeyProvider
{
    private readonly Dictionary<int, NtpiVersionKey> _versionKeys;

    /// <summary>
    /// Initializes a new instance of VersionKeyProvider with the specified version numbers.
    /// <para>使用指定的版本号初始化 VersionKeyProvider 的新实例。</para>
    /// </summary>
    public VersionKeyProvider(ulong major, ulong minor, ulong patch)
    {
        var keys = NtpiVersionRegistry.GetKeysForVersion(major, minor, patch)
            ?? throw new Exceptions.UnsupportedVersionException(major, minor, patch);
        _versionKeys = keys;
    }

    /// <summary>
    /// Returns the encryption key.
    /// <para>返回加密密钥。</para>
    /// </summary>
    public (byte[] Key, byte[] Iv)? GetKey(RegionType regionType)
    {
        return _versionKeys.TryGetValue((int)regionType, out var versionKey)
            ? (versionKey.Key, versionKey.Iv)
            : null;
    }
}

internal sealed class BufferedDecryptStream : Stream
{
    private readonly MemoryStream _buffer;
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private bool _disposed;

    internal BufferedDecryptStream(
        Stream input, byte[] key, byte[] iv, bool leaveOpen,
        Func<byte[], byte[], byte[], byte[]> decrypt, int maximumBufferedBytes)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _leaveOpen = leaveOpen;
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBufferedBytes);
            using var encrypted = new MemoryStream();
            CopyBounded(input, encrypted, maximumBufferedBytes);
            var plaintext = decrypt(encrypted.ToArray(), key, iv);
            if (plaintext.Length > maximumBufferedBytes)
            {
                throw new InvalidDataException($"Decrypted data exceeds the {maximumBufferedBytes}-byte fallback limit.");
            }
            _buffer = new MemoryStream(plaintext, writable: false);
        }
        catch
        {
            if (!leaveOpen)
            {
                input.Dispose();
            }
            throw;
        }
    }

    private static void CopyBounded(Stream input, Stream output, int maximumBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException($"Encrypted input exceeds the {maximumBytes}-byte fallback limit.");
            }
            output.Write(buffer, 0, read);
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Position; set => _buffer.Position = value; }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(Span<byte> buffer) => _buffer.Read(buffer);
    /// <summary>
    /// Sets the stream position relative to the specified origin.
    /// <para>相对于指定起点设置流位置。</para>
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);
    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public override void Flush() { }
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

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            if (!_leaveOpen)
            {
                _input.Dispose();
            }
            _buffer.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}

internal sealed class BufferedEncryptStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly Stream _output;
    private readonly byte[] _key;
    private readonly byte[] _iv;
    private readonly bool _leaveOpen;
    private readonly Func<byte[], byte[], byte[], byte[]> _encrypt;
    private readonly int _maximumBufferedBytes;
    private bool _finalized;
    private bool _disposed;

    internal BufferedEncryptStream(
        Stream output, byte[] key, byte[] iv, bool leaveOpen,
        Func<byte[], byte[], byte[], byte[]> encrypt, int maximumBufferedBytes)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _iv = iv ?? throw new ArgumentNullException(nameof(iv));
        _leaveOpen = leaveOpen;
        _encrypt = encrypt ?? throw new ArgumentNullException(nameof(encrypt));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBufferedBytes);
        _maximumBufferedBytes = maximumBufferedBytes;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _buffer.Length; set => throw new NotSupportedException(); }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }
        ThrowIfDisposed();
        if (_finalized)
        {
            throw new InvalidOperationException("The encrypt stream is already finalized.");
        }
        EnsureCapacity(count);
        _buffer.Write(buffer, offset, count);
    }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (_finalized)
        {
            throw new InvalidOperationException("The encrypt stream is already finalized.");
        }
        EnsureCapacity(buffer.Length);
        _buffer.Write(buffer);
    }

    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
    /// Finalizes the encryption and writes all ciphertext to the output stream.
    /// <para>完成剩余块加密并将全部密文写入输出流。</para>
    /// </summary>
    public void FlushFinalBlock()
    {
        ThrowIfDisposed();
        if (_finalized)
        {
            return;
        }
        var ciphertext = _encrypt(_buffer.ToArray(), _key, _iv);
        if (ciphertext.Length > _maximumBufferedBytes)
        {
            throw new InvalidDataException($"Encrypted output exceeds the {_maximumBufferedBytes}-byte fallback limit.");
        }
        _output.Write(ciphertext, 0, ciphertext.Length);
        _finalized = true;
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public override void Flush() { }

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            try
            {
                if (!_finalized)
                {
                    FlushFinalBlock();
                }
            }
            finally
            {
                try
                {
                    if (!_leaveOpen)
                    {
                        _output.Dispose();
                    }
                }
                finally
                {
                    _buffer.Dispose();
                    _disposed = true;
                }
            }
        }
        base.Dispose(disposing);
    }

    private void EnsureCapacity(int additionalBytes)
    {
        if (additionalBytes > _maximumBufferedBytes - _buffer.Length)
        {
            throw new InvalidDataException($"Plaintext input exceeds the {_maximumBufferedBytes}-byte fallback limit.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
