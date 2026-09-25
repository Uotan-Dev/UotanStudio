using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Crypto;

/// <summary>
/// AES-256-CBC codec used by the NTPI reader and writer: one-shot byte-array transforms plus stream adapters, with
/// every cipher parameter taken from the arguments of the call that uses it.
/// <para>NTPI 读取器与写入器使用的 AES-256-CBC 编解码器：一次性字节数组转换与流适配器，加密参数全部取自使用它的
/// 那次调用的实参。</para>
/// </summary>
public interface ICryptoProvider
{
    private static readonly int MaximumBufferedFallbackBytes = Array.MaxLength;

    /// <summary>
    /// Decrypts data using AES-256-CBC mode.
    /// <para>使用 AES-256-CBC 模式解密数据。</para>
    /// </summary>
    /// <param name="data">
    /// Complete ciphertext block sequence; the padding-free CBC codec used here requires a non-empty length that is a
    /// multiple of the 16-byte AES block.
    /// <para>完整的密文块序列；此处使用的无填充 CBC 编解码要求长度非零且为 16 字节 AES 块的整数倍。</para>
    /// </param>
    /// <param name="key">
    /// AES key bytes, 32 bytes for the AES-256 NTPI payloads.
    /// <para>AES 密钥字节；NTPI 载荷使用的 AES-256 为 32 字节。</para>
    /// </param>
    /// <param name="iv">
    /// Sixteen-byte CBC chaining block applied to the first ciphertext block.
    /// <para>作用于首个密文块的 16 字节 CBC 链接向量。</para>
    /// </param>
    /// <returns>
    /// A newly allocated array holding the plaintext, with the same length as <paramref name="data"/>.
    /// <para>新分配的明文字节数组，长度与 <paramref name="data"/> 相同。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="data"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is empty or not a whole number of AES blocks.
    /// <para><paramref name="data"/> 为空，或长度不是整数个 AES 块。</para>
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The key or IV length is not valid for the algorithm. <para>密钥或 IV 长度对该算法无效。</para>
    /// </exception>
    byte[] Decrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// Encrypts data using AES-256-CBC mode.
    /// <para>使用 AES-256-CBC 模式加密数据。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext bytes; any length is accepted because the CBC codec in use pads the tail up to the next 16-byte block
    /// boundary.
    /// <para>明文字节；任意长度均可，因为所用的 CBC 编解码会把尾部补齐到下一个 16 字节块边界。</para>
    /// </param>
    /// <param name="key">
    /// AES key bytes, 32 bytes for the AES-256 NTPI payloads.
    /// <para>AES 密钥字节；NTPI 载荷使用的 AES-256 为 32 字节。</para>
    /// </param>
    /// <param name="iv">
    /// Sixteen-byte CBC chaining block applied to the first plaintext block.
    /// <para>作用于首个明文块的 16 字节 CBC 链接向量。</para>
    /// </param>
    /// <returns>
    /// A newly allocated ciphertext array whose length is <paramref name="data"/> rounded up to a whole number of AES
    /// blocks; an empty input yields an empty array.
    /// <para>新分配的密文数组，长度为 <paramref name="data"/> 向上取整到整数个 AES 块；空输入返回空数组。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="data"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The key or IV length is not valid for the algorithm. <para>密钥或 IV 长度对该算法无效。</para>
    /// </exception>
    byte[] Encrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// Creates a read stream that consumes ciphertext from the input stream and yields decrypted data.
    /// <para>创建从输入流读取密文并输出解密密文的读取流。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream positioned at the first ciphertext byte; the default implementation reads it to its end before
    /// returning and never seeks it.
    /// <para>定位在首个密文字节处的可读流；默认实现在返回前读到流末尾，且不会寻址。</para>
    /// </param>
    /// <param name="key">
    /// AES key bytes for this payload. <para>该载荷使用的 AES 密钥字节。</para>
    /// </param>
    /// <param name="iv">
    /// Sixteen-byte CBC chaining block. <para>16 字节 CBC 链接向量。</para>
    /// </param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="input"/> open when the returned stream is disposed;
    /// <see langword="false"/> transfers ownership, and a failed construction also disposes the input in that case.
    /// <para>为 <see langword="true"/> 时释放返回流后仍保持 <paramref name="input"/> 打开；为 <see langword="false"/>
    /// 表示转移所有权，此时构造失败也会释放输入流。</para>
    /// </param>
    /// <returns>
    /// A readable, seekable stream over the whole decrypted payload; writing and length changes are unsupported, and
    /// disposing it releases the buffered plaintext.
    /// <para>覆盖整段解密载荷的可读、可寻址流；不支持写入和修改长度，释放时一并释放缓冲的明文。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The ciphertext or the decrypted result exceeds the buffered fallback limit.
    /// <para>密文或解密结果超过缓冲回退上限。</para>
    /// </exception>
    Stream CreateDecryptStream(Stream input, byte[] key, byte[] iv, bool leaveOpen = true)
        => new BufferedDecryptStream(input, key, iv, leaveOpen, Decrypt, MaximumBufferedFallbackBytes);

    /// <summary>
    /// Creates a write stream that encrypts written data into the output stream.
    /// <para>创建向输出流写入加密密文的写入流。</para>
    /// </summary>
    /// <param name="output">
    /// Writable stream that receives the ciphertext; the default implementation writes to it once, when the buffered
    /// plaintext is finalized.
    /// <para>接收密文的可写流；默认实现在缓冲明文定稿时一次性写入。</para>
    /// </param>
    /// <param name="key">
    /// AES key bytes for this payload. <para>该载荷使用的 AES 密钥字节。</para>
    /// </param>
    /// <param name="iv">
    /// Sixteen-byte CBC chaining block. <para>16 字节 CBC 链接向量。</para>
    /// </param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to keep <paramref name="output"/> open after the returned stream is disposed;
    /// <see langword="false"/> lets disposal close it.
    /// <para>为 <see langword="true"/> 时返回流释放后仍保持 <paramref name="output"/> 打开；为 <see langword="false"/>
    /// 时由释放操作将其关闭。</para>
    /// </param>
    /// <returns>
    /// A write-only stream that buffers plaintext and, when it is disposed, encrypts the buffered bytes in one shot and
    /// writes the resulting ciphertext to <paramref name="output"/>; reading and seeking are unsupported.
    /// <para>只写流，先缓冲明文，在其被释放时一次性加密已缓冲字节并把所得密文写入 <paramref name="output"/>；
    /// 不支持读取和寻址。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="output"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The buffered plaintext or the produced ciphertext exceeds the fallback limit.
    /// <para>缓冲的明文或生成的密文超过回退上限。</para>
    /// </exception>
    Stream CreateEncryptStream(Stream output, byte[] key, byte[] iv, bool leaveOpen = true)
        => new BufferedEncryptStream(output, key, iv, leaveOpen, Encrypt, MaximumBufferedFallbackBytes);
}

/// <summary>
/// Looks up the AES key and IV registered for one region kind of the NTPI package being processed.
/// <para>查询当前 NTPI 包中某一区域种类所登记的 AES 密钥与 IV。</para>
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// Gets the encryption key and initialization vector for the specified region.
    /// <para>获取指定区域的加密密钥和初始化向量。</para>
    /// </summary>
    /// <param name="regionType">
    /// Region kind whose key material is requested. <para>需要取其密钥材料的区域种类。</para>
    /// </param>
    /// <returns>
    /// The key and IV pair for that region, or <see langword="null"/> when no key is registered for it; an
    /// implementation that stores private copies hands out fresh arrays the caller may modify freely.
    /// <para>该区域的密钥与 IV 配对；未为其登记密钥时返回 <see langword="null"/>。保存私有副本的实现返回全新数组，
    /// 调用方可自由修改。</para>
    /// </returns>
    (byte[] Key, byte[] Iv)? GetKey(RegionType regionType);
}

/// <summary>
/// <see cref="IKeyProvider"/> bound to one NTPI version triple: the constructor resolves that version's key
/// dictionary once, and lookups index it by <see cref="RegionType"/>.
/// <para>绑定到某个 NTPI 版本三元组的 <see cref="IKeyProvider"/>：构造时一次性解析该版本的密钥字典，
/// 查询按 <see cref="RegionType"/> 取索引。</para>
/// </summary>
public sealed class VersionKeyProvider : IKeyProvider
{
    private readonly Dictionary<int, NtpiVersionKey> _versionKeys;

    /// <summary>
    /// Initializes a new instance of VersionKeyProvider with the specified version numbers.
    /// <para>使用指定的版本号初始化 VersionKeyProvider 的新实例。</para>
    /// </summary>
    /// <param name="major">
    /// Major version component read from the package header. <para>从包头部读取的主版本号。</para>
    /// </param>
    /// <param name="minor">
    /// Minor version component read from the package header. <para>从包头部读取的次版本号。</para>
    /// </param>
    /// <param name="patch">
    /// Patch version component; a patch mismatch alone still resolves to the key set registered for the same major and
    /// minor pair. <para>修订版本号；仅修订号不同时仍会落到同一主次号登记的密钥集。</para>
    /// </param>
    /// <exception cref="Exceptions.UnsupportedVersionException">
    /// No key set is registered for the given version triple or for its major and minor pair.
    /// <para>该版本三元组及其主次号组合均未登记密钥集。</para>
    /// </exception>
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
    /// <param name="regionType">
    /// Region kind whose key material is requested; its numeric value is the key index within the version's dictionary.
    /// <para>需要取其密钥材料的区域种类；其数值即该版本字典中的密钥索引。</para>
    /// </param>
    /// <returns>
    /// Fresh copies of the region's key and IV, or <see langword="null"/> when this version registers no key for that
    /// region type.
    /// <para>该区域密钥与 IV 的新副本；此版本未为该区域类型登记密钥时返回 <see langword="null"/>。</para>
    /// </returns>
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