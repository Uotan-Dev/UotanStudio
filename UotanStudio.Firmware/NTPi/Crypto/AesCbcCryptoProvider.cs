using System.Security.Cryptography;

namespace UotanStudio.Firmware.NTPi.Crypto;

/// <summary>
/// <para>使用 AES-256-CBC 模式提供加密解密服务的实现。</para>
/// Implementation of encryption and decryption services using AES-256-CBC mode.
/// </summary>
/// <remarks>
/// <para>此实现使用 PaddingMode.None,要求解密数据必须是块对齐的(16 字节的倍数)。加密时若数据未对齐,将使用零填充至块大小。</para>
/// This implementation uses PaddingMode.None, requiring decrypted data to be block-aligned (multiple of 16 bytes). If data is not aligned during encryption, it is zero-padded to the block size.
/// </remarks>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
    /// <para>计算数据填充至 AES 块大小边界后的总大小。</para>
    /// Calculates the total size after padding data to the AES block size boundary.
    /// </summary>
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
/// <para>Region6 文件级加密的密钥映射提供者。</para>
/// Key map provider for Region6 file-level encryption.
/// </summary>
/// <remarks>
/// <para>此类根据密钥索引和块索引从 KeyMap 数据中提取 32 字节的 AES 密钥,用于 Region6 数据块的逐块加密解密。</para>
/// This class extracts 32-byte AES keys from KeyMap data based on key index and block index, used for per-block encryption/decryption of Region6 data.
/// </remarks>
public sealed class KeyMapKeyProvider
{
    private readonly byte[] _keyMap;

    /// <summary>
    /// <para>使用指定的 KeyMap 数据初始化 KeyMapKeyProvider 的新实例。</para>
    /// Initializes a new instance of KeyMapKeyProvider with the specified KeyMap data.
    /// </summary>
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
    /// <para>根据密钥索引和块索引获取对应的 AES-256 密钥。</para>
    /// Gets the AES-256 key for the specified key index and block index.
    /// </summary>
    /// <remarks>
    /// <para>密钥偏移量计算公式为 (keyIndex + blockIdx) * 32 % keyMap.Length。</para>
    /// The key offset is calculated as (keyIndex + blockIdx) * 32 % keyMap.Length.
    /// </remarks>
    public byte[] GetKey(int keyIndex, int blockIdx)
    {
        var key = new byte[32];
        GetKey(keyIndex, blockIdx, key);
        return key;
    }

    /// <summary>
    /// <para>根据密钥索引和块索引将 AES-256 密钥写入调用方提供的缓冲区,避免堆分配。</para>
    /// Writes the AES-256 key into the caller-provided buffer, avoiding heap allocation.
    /// </summary>
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
    /// <para>获取 Region6 块加密使用的标准零初始化向量。</para>
    /// Gets the standard zero initialization vector used for Region6 block encryption.
    /// </summary>
    public static byte[] GetZeroIv() => new byte[16];
}