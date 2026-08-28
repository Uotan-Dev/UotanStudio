using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Crypto;

/// <summary>
/// <para>AES 加密解密服务接口。</para>
/// Interface for AES encryption and decryption services.
/// </summary>
public interface ICryptoProvider
{
    private static readonly int MaximumBufferedFallbackBytes = Array.MaxLength;

    /// <summary>
    /// <para>使用 AES-256-CBC 模式解密数据。</para>
    /// Decrypts data using AES-256-CBC mode.
    /// </summary>
    /// <param name="data"><para>要解密的密文数据。</para>Ciphertext data to decrypt.</param>
    /// <param name="key"><para>解密密钥。</para>Decryption key.</param>
    /// <param name="iv"><para>初始化向量。</para>Initialization vector.</param>
    /// <returns><para>解密后的明文数据。</para>Decrypted plaintext data.</returns>
    byte[] Decrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// <para>使用 AES-256-CBC 模式加密数据。</para>
    /// Encrypts data using AES-256-CBC mode.
    /// </summary>
    /// <param name="data"><para>要加密的明文数据。</para>Plaintext data to encrypt.</param>
    /// <param name="key"><para>加密密钥。</para>Encryption key.</param>
    /// <param name="iv"><para>初始化向量。</para>Initialization vector.</param>
    /// <returns><para>加密后的密文数据。</para>Encrypted ciphertext data.</returns>
    byte[] Encrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// <para>创建从输入流读取密文并输出解密密文的读取流。</para>
    /// Creates a read stream that consumes ciphertext from the input stream and yields decrypted data.
    /// </summary>
    /// <param name="input"><para>密文输入流。</para>The ciphertext input stream.</param>
    /// <param name="key"><para>解密密钥。</para>Decryption key.</param>
    /// <param name="iv"><para>初始化向量。</para>Initialization vector.</param>
    /// <param name="leaveOpen"><para>释放时是否保持输入流打开。</para>Whether to leave the input stream open after disposal.</param>
    /// <returns><para>读取时产生解密密文的流。</para>A stream that yields decrypted data while read.</returns>
    Stream CreateDecryptStream(Stream input, byte[] key, byte[] iv, bool leaveOpen = true)
        => new BufferedDecryptStream(input, key, iv, leaveOpen, Decrypt, MaximumBufferedFallbackBytes);

    /// <summary>
    /// <para>创建向输出流写入加密密文的写入流。</para>
    /// Creates a write stream that encrypts written data into the output stream.
    /// </summary>
    /// <param name="output"><para>密文输出流。</para>The ciphertext output stream.</param>
    /// <param name="key"><para>加密密钥。</para>Encryption key.</param>
    /// <param name="iv"><para>初始化向量。</para>Initialization vector.</param>
    /// <param name="leaveOpen"><para>释放时是否保持输出流打开。</para>Whether to leave the output stream open after disposal.</param>
    /// <returns><para>写入时产生加密密文的流。</para>A stream that encrypts data while written.</returns>
    Stream CreateEncryptStream(Stream output, byte[] key, byte[] iv, bool leaveOpen = true)
        => new BufferedEncryptStream(output, key, iv, leaveOpen, Encrypt, MaximumBufferedFallbackBytes);
}

/// <summary>
/// <para>密钥提供者接口。</para>
/// Interface for key providers.
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// <para>获取指定区域的加密密钥和初始化向量。</para>
    /// Gets the encryption key and initialization vector for the specified region.
    /// </summary>
    /// <param name="regionType"><para>区域类型。</para>Region type.</param>
    /// <returns><para>密钥和初始化向量,如果未找到则返回 null。</para>Key and IV, or null if not found.</returns>
    (byte[] Key, byte[] Iv)? GetKey(RegionType regionType);
}

/// <summary>
/// <para>基于 NTPI 固件版本号的密钥提供者实现。</para>
/// Key provider implementation based on NTPI firmware version numbers.
/// </summary>
public sealed class VersionKeyProvider : IKeyProvider
{
    private readonly Dictionary<int, NtpiVersionKey> _versionKeys;

    /// <summary>
    /// <para>使用指定的版本号初始化 VersionKeyProvider 的新实例。</para>
    /// Initializes a new instance of VersionKeyProvider with the specified version numbers.
    /// </summary>
    /// <exception cref="Exceptions.UnsupportedVersionException">指定版本未在注册表中找到对应的密钥。</exception>
    public VersionKeyProvider(ulong major, ulong minor, ulong patch)
    {
        var keys = NtpiVersionRegistry.GetKeysForVersion(major, minor, patch)
            ?? throw new Exceptions.UnsupportedVersionException(major, minor, patch);
        _versionKeys = keys;
    }

    /// <inheritdoc />
    public (byte[] Key, byte[] Iv)? GetKey(RegionType regionType)
    {
        return _versionKeys.TryGetValue((int)regionType, out var versionKey)
            ? (versionKey.Key, versionKey.Iv)
            : null;
    }
}

/// <summary>
/// <para>基于 byte[] API 的默认解密流回退实现(整块缓冲后解密)。</para>
/// Default decrypt-stream fallback built on the byte[] API (buffers then decrypts).
/// </summary>
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

    /// <inheritdoc />
    public override bool CanRead => !_disposed;
    /// <inheritdoc />
    public override bool CanSeek => !_disposed;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _buffer.Length;
    /// <inheritdoc />
    public override long Position { get => _buffer.Position; set => _buffer.Position = value; }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _buffer.Read(buffer);
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);
    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
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

/// <summary>
/// <para>基于 byte[] API 的默认加密流回退实现(结束时整块加密写出)。</para>
/// Default encrypt-stream fallback built on the byte[] API (encrypts the whole buffer on completion).
/// </summary>
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

    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => !_disposed;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => _buffer.Length; set => throw new NotSupportedException(); }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// <para>完成剩余块加密并将全部密文写入输出流。</para>
    /// Finalizes the encryption and writes all ciphertext to the output stream.
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

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
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