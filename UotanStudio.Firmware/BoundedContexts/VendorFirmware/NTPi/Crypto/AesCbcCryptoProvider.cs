using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Crypto;

/// <summary>
/// Applies AES-256 in CBC mode with no cipher-level padding; decryption requires block-aligned
/// input and encryption zero-pads the plaintext to a block boundary.
/// <para>以 CBC 模式执行 AES-256 加解密，不使用密码器内置填充；解密输入必须按块对齐，加密输入会零填充至块边界。</para>
/// </summary>
public sealed class AesCbcCryptoProvider : ICryptoProvider
{
    private const int BlockSize = 16;

    private static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return aes;
    }

    /// <summary>
    /// Decrypts the input data.
    /// <para>解密输入数据。</para>
    /// </summary>
    /// <param name="data">
    /// Cipher text whose length must be a non-zero multiple of the 16-byte block size.
    /// <para>密文，长度必须是 16 字节块大小的非零整数倍。</para>
    /// </param>
    /// <param name="key">
    /// AES key material; the underlying provider requires a valid AES key size.
    /// <para>AES 密钥材料；密钥长度必须是底层提供程序支持的 AES 有效值。</para>
    /// </param>
    /// <param name="iv">The 16-byte initialization vector. <para>16 字节初始化向量。</para></param>
    /// <returns>
    /// The decrypted bytes as a newly allocated array.
    /// <para>解密后的字节，以新分配的数组返回。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="data"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The data length is zero or not a multiple of the block size.
    /// <para>数据长度为 0 或不是块大小的整数倍。</para>
    /// </exception>
    public byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);

        if (data.Length == 0 || data.Length % BlockSize != 0)
        {
            throw new ArgumentException(
                $"Data length ({data.Length}) must be a non-zero multiple of the block size ({BlockSize} bytes).",
                nameof(data));
        }

        using var aes = CreateAes();
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>
    /// Encrypts the input data.
    /// <para>加密输入数据。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext bytes; an empty array is allowed and produces empty output.
    /// <para>明文字节；允许空数组，输出同样为空。</para>
    /// </param>
    /// <param name="key">
    /// AES key material; the underlying provider requires a valid AES key size.
    /// <para>AES 密钥材料；密钥长度必须是底层提供程序支持的 AES 有效值。</para>
    /// </param>
    /// <param name="iv">The 16-byte initialization vector. <para>16 字节初始化向量。</para></param>
    /// <returns>
    /// The cipher text, zero-padded up to the next multiple of the 16-byte block size.
    /// <para>密文，已零填充至 16 字节块大小的下一个整数倍。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="data"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public byte[] Encrypt(byte[] data, byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);

        var paddedData = PadToBlockSize(data);
        using var aes = CreateAes();
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(paddedData, 0, paddedData.Length);
    }

    /// <summary>
    /// Creates a stream that decrypts the input.
    /// <para>创建用于解密输入的流。</para>
    /// </summary>
    /// <param name="input">
    /// Readable cipher-text stream consumed from its current position.
    /// <para>从当前位置开始读取的密文可读流。</para>
    /// </param>
    /// <param name="key">
    /// AES key material; the underlying provider requires a valid AES key size.
    /// <para>AES 密钥材料；密钥长度必须是底层提供程序支持的 AES 有效值。</para>
    /// </param>
    /// <param name="iv">The 16-byte initialization vector. <para>16 字节初始化向量。</para></param>
    /// <param name="leaveOpen">
    /// When <see langword="false"/>, disposing the returned stream also closes <paramref name="input"/>.
    /// <para>为 <see langword="false"/> 时，释放返回流会同时关闭 <paramref name="input"/>。</para>
    /// </param>
    /// <returns>
    /// A read-oriented, non-seekable decrypting stream that owns its AES instance and transform
    /// until disposed.
    /// <para>面向读取、不可寻址的解密流；其内部的 AES 实例与转换对象由该流持有直至被释放。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="input"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> cannot be read. <para><paramref name="input"/> 不可读。</para>
    /// </exception>
    public Stream CreateDecryptStream(Stream input, byte[] key, byte[] iv, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        if (!input.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(input));
        }

        Aes? aes = null;
        ICryptoTransform? decryptor = null;
        try
        {
            aes = CreateAes();
            aes.Key = key;
            aes.IV = iv;
            decryptor = aes.CreateDecryptor();
            var crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read, leaveOpen);
            return new OwningCryptoStream(crypto, decryptor, aes);
        }
        catch
        {
            decryptor?.Dispose();
            aes?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a stream that encrypts the output.
    /// <para>创建用于加密输出的流。</para>
    /// </summary>
    /// <param name="output">
    /// Writable destination stream; disposed together with the returned stream when
    /// <paramref name="leaveOpen"/> is <see langword="false"/>.
    /// <para>可写目标流；当 <paramref name="leaveOpen"/> 为 <see langword="false"/> 时，随返回流一起被关闭。</para>
    /// </param>
    /// <param name="key">
    /// AES key material; the underlying provider requires a valid AES key size.
    /// <para>AES 密钥材料；密钥长度必须是底层提供程序支持的 AES 有效值。</para>
    /// </param>
    /// <param name="iv">The 16-byte initialization vector. <para>16 字节初始化向量。</para></param>
    /// <param name="leaveOpen">
    /// When <see langword="false"/>, disposing the returned stream also closes <paramref name="output"/>.
    /// <para>为 <see langword="false"/> 时，释放返回流会同时关闭 <paramref name="output"/>。</para>
    /// </param>
    /// <returns>
    /// A write-only, non-seekable encrypting stream whose <c>Position</c> reports bytes written;
    /// disposing it zero-pads the trailing partial block before finalizing.
    /// <para>只写、不可寻址的加密流，其 <c>Position</c> 返回已写入字节数；释放时会先把末尾不完整块零填充再结束。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/>, <paramref name="key"/> or <paramref name="iv"/> is <see langword="null"/>.
    /// <para><paramref name="output"/>、<paramref name="key"/> 或 <paramref name="iv"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="output"/> cannot be written. <para><paramref name="output"/> 不可写。</para>
    /// </exception>
    public Stream CreateEncryptStream(Stream output, byte[] key, byte[] iv, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(output));
        }

        Aes? aes = null;
        ICryptoTransform? encryptor = null;
        try
        {
            aes = CreateAes();
            aes.Key = key;
            aes.IV = iv;
            encryptor = aes.CreateEncryptor();
            var crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write, leaveOpen);
            return new ZeroPaddingEncryptStream(crypto, encryptor, aes);
        }
        catch
        {
            encryptor?.Dispose();
            aes?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Calculates the total size after padding data to the AES block size boundary.
    /// <para>计算数据填充至 AES 块大小边界后的总大小。</para>
    /// </summary>
    /// <param name="dataLength">
    /// The unpadded byte count; a value already aligned to the block size is returned unchanged.
    /// <para>未填充的字节数；已按块对齐的值原样返回。</para>
    /// </param>
    /// <returns>
    /// The length rounded up to the next multiple of the 16-byte block size.
    /// <para>向上取整到 16 字节块大小下一个整数倍后的长度。</para>
    /// </returns>
    public static int CalculatePaddedLength(int dataLength)
    {
        var remainder = dataLength % BlockSize;
        return remainder == 0 ? dataLength : dataLength + (BlockSize - remainder);
    }

    private sealed class OwningCryptoStream(CryptoStream inner, ICryptoTransform transform, Aes aes) : Stream
    {
        private bool _disposed;
        public override bool CanRead => !_disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                try { inner.Dispose(); }
                finally
                {
                    transform.Dispose();
                    aes.Dispose();
                    _disposed = true;
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ZeroPaddingEncryptStream(CryptoStream inner, ICryptoTransform transform, Aes aes) : Stream
    {
        private long _bytesWritten;
        private bool _disposed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _bytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _bytesWritten = checked(_bytesWritten + count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _bytesWritten = checked(_bytesWritten + buffer.Length);
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                try
                {
                    var remainder = (int)(_bytesWritten % BlockSize);
                    if (remainder != 0)
                    {
                        inner.Write(new byte[BlockSize - remainder]);
                    }
                    inner.FlushFinalBlock();
                }
                finally
                {
                    try { inner.Dispose(); }
                    finally
                    {
                        transform.Dispose();
                        aes.Dispose();
                        _disposed = true;
                    }
                }
            }
            base.Dispose(disposing);
        }
    }

    private static byte[] PadToBlockSize(byte[] data)
    {
        var remainder = data.Length % BlockSize;
        if (remainder == 0)
        {
            return data;
        }

        var paddedLength = data.Length + (BlockSize - remainder);
        var padded = new byte[paddedLength];
        Array.Copy(data, padded, data.Length);
        return padded;
    }
}

/// <summary>
/// Derives per-block AES-256 keys by reading a 32-byte window at a rotating offset through a
/// caller-supplied key-map blob.
/// <para>通过在调用方提供的 KeyMap 数据中按轮转偏移读取 32 字节窗口，派生每个块对应的 AES-256 密钥。</para>
/// </summary>
public sealed class KeyMapKeyProvider
{
    private readonly byte[] _keyMap;

    /// <summary>
    /// Initializes a new instance of KeyMapKeyProvider with the specified KeyMap data.
    /// <para>使用指定的 KeyMap 数据初始化 KeyMapKeyProvider 的新实例。</para>
    /// </summary>
    /// <param name="keyMap">
    /// Key-map bytes retained by reference without copying; later mutations of the array are
    /// visible to derived keys.
    /// <para>按引用保留、不做复制的 KeyMap 字节；对该数组的后续修改会影响派生的密钥。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="keyMap"/> is <see langword="null"/>.
    /// <para><paramref name="keyMap"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyMap"/> is empty. <para><paramref name="keyMap"/> 为空。</para>
    /// </exception>
    public KeyMapKeyProvider(byte[] keyMap)
    {
        ArgumentNullException.ThrowIfNull(keyMap);
        if (keyMap.Length == 0)
        {
            throw new ArgumentException("KeyMap cannot be empty.", nameof(keyMap));
        }
        _keyMap = keyMap;
    }

    /// <summary>
    /// Gets the AES-256 key for the specified key index and block index.
    /// <para>根据密钥索引和块索引获取对应的 AES-256 密钥。</para>
    /// </summary>
    /// <param name="keyIndex">
    /// The caller-selected key index; combined with <paramref name="blockIdx"/> it selects the window offset.
    /// <para>调用方选择的密钥索引；与 <paramref name="blockIdx"/> 共同决定窗口偏移。</para>
    /// </param>
    /// <param name="blockIdx">
    /// The block ordinal added to <paramref name="keyIndex"/> before the offset is folded into the key map.
    /// <para>块序号，与 <paramref name="keyIndex"/> 相加后折入 KeyMap 长度范围内。</para>
    /// </param>
    /// <returns>
    /// A newly allocated 32-byte key window starting at <c>(keyIndex + blockIdx) × 32</c> modulo
    /// the key-map length, wrapping across the array end.
    /// <para>新分配的 32 字节密钥窗口，起始偏移为 <c>(keyIndex + blockIdx) × 32</c> 对 KeyMap 长度取模，并在数组尾部环绕。</para>
    /// </returns>
    public byte[] GetKey(int keyIndex, int blockIdx)
    {
        var key = new byte[32];
        GetKey(keyIndex, blockIdx, key);
        return key;
    }

    /// <summary>
    /// Writes the AES-256 key into the caller-provided buffer, avoiding heap allocation.
    /// <para>根据密钥索引和块索引将 AES-256 密钥写入调用方提供的缓冲区,避免堆分配。</para>
    /// </summary>
    /// <param name="keyIndex">
    /// The caller-selected key index; combined with <paramref name="blockIdx"/> it selects the window offset.
    /// <para>调用方选择的密钥索引；与 <paramref name="blockIdx"/> 共同决定窗口偏移。</para>
    /// </param>
    /// <param name="blockIdx">
    /// The block ordinal added to <paramref name="keyIndex"/> before the offset is folded into the key map.
    /// <para>块序号，与 <paramref name="keyIndex"/> 相加后折入 KeyMap 长度范围内。</para>
    /// </param>
    /// <param name="destination">
    /// Buffer receiving exactly the first 32 bytes of the key window; bytes beyond index 31 are untouched.
    /// <para>接收密钥窗口前 32 字节的缓冲区；索引 31 之后的字节不会被修改。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="destination"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is shorter than 32 bytes. <para><paramref name="destination"/> 少于 32 字节。</para>
    /// </exception>
    public void GetKey(int keyIndex, int blockIdx, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < 32)
        {
            throw new ArgumentException("Destination buffer must be at least 32 bytes.", nameof(destination));
        }

        var offset = (keyIndex + blockIdx) * 32 % _keyMap.Length;
        if (offset + 32 <= _keyMap.Length)
        {
            Buffer.BlockCopy(_keyMap, offset, destination, 0, 32);
        }
        else
        {
            var firstPart = _keyMap.Length - offset;
            Buffer.BlockCopy(_keyMap, offset, destination, 0, firstPart);
            Buffer.BlockCopy(_keyMap, 0, destination, firstPart, 32 - firstPart);
        }
    }

    /// <summary>
    /// Gets the standard zero initialization vector used for Region6 block encryption.
    /// <para>获取 Region6 块加密使用的标准零初始化向量。</para>
    /// </summary>
    /// <returns>
    /// A freshly allocated 16-byte all-zero IV, safe for callers to mutate.
    /// <para>新分配的 16 字节全零 IV，调用方可安全修改。</para>
    /// </returns>
    public static byte[] GetZeroIv() => new byte[16];
}