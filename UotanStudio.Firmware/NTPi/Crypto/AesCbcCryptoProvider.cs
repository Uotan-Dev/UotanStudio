using System.Security.Cryptography;

namespace UotanStudio.Firmware.NTPi.Crypto;

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

public sealed class KeyMapKeyProvider
{
    private readonly byte[] _keyMap;

    /// <summary>
    /// Initializes a new instance of KeyMapKeyProvider with the specified KeyMap data.
    /// <para>使用指定的 KeyMap 数据初始化 KeyMapKeyProvider 的新实例。</para>
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
    /// Gets the AES-256 key for the specified key index and block index.
    /// <para>根据密钥索引和块索引获取对应的 AES-256 密钥。</para>
    /// </summary>
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
    public static byte[] GetZeroIv() => new byte[16];
}
