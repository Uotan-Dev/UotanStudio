using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Logging;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps;

public class OpsDecryptor(Stream stream, IOnePlusLogger? logger = null)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly IOnePlusLogger _logger = logger ?? NullLogger.Instance;

    private static readonly byte[] IdentitySBox =
        [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static readonly byte[] DefaultKey =
        Encoding.UTF8.GetBytes("OnePlus#@!$^&*()_+");

    /// <summary>
    /// Parses the OPS header from the current stream position
    /// <para>从当前流位置解析OPS头部</para>
    /// </summary>
    public OpsHeader ParseHeader()
    {
        _logger.Information("Parsing OPS header");
        var data = new byte[OpsConstants.HEADER_SIZE];
        ReadExact(_stream, data, 0, data.Length);
        var header = OpsHeader.FromBytes(data);
        if (!header.IsValid)
        {
            throw new InvalidDataException("Invalid OPS magic signature");
        }

        _logger.Debug($"Version: {header.Version}, Entries: {header.EntryCount}");
        return header;
    }

    /// <summary>
    /// Parses the OPS entry table following the header
    /// <para>解析头部之后的OPS条目表</para>
    /// </summary>
    public List<OpsEntry> ParseEntries(uint entryCount)
    {
        _logger.Debug($"Parsing {entryCount} OPS entries");
        var entries = new List<OpsEntry>((int)entryCount);

        var buffer = new byte[OpsConstants.ENTRY_HEADER_SIZE];
        for (uint i = 0; i < entryCount; i++)
        {
            ReadExact(_stream, buffer, 0, buffer.Length);
            entries.Add(OpsEntry.FromBytes(buffer));
        }

        return entries;
    }

    /// <summary>
    /// Reads an entry's raw data from the stream at the given absolute offset
    /// <para>从流的指定绝对偏移读取条目的原始数据</para>
    /// </summary>
    public byte[] ReadEntryData(uint offset, uint size)
    {
        if (size == 0)
        {
            return [];
        }

        _stream.Seek(offset, SeekOrigin.Begin);
        var data = new byte[size];
        ReadExact(_stream, data, 0, data.Length);
        return data;
    }

    /// <summary>
    /// Decrypts data using the SBox-based stream cipher with a default key
    /// <para>使用基于SBox的流密码和默认密钥解密数据</para>
    /// </summary>
    public byte[] DecryptData(byte[] encryptedData, int variant = 0)
    {
        var key = DeriveKey(variant);
        var sbox = GenerateSBox(key);
        return DecryptData(encryptedData.AsSpan(), sbox);
    }

    /// <summary>
    /// Decrypts a span using the SBox-based stream cipher, writing into a new array.
    /// The XOR is vectorized in 256-byte segments matching the repeating key period.
    /// <para>按 SBox 流密码解密 Span 并写入新数组；XOR 按 256 字节密钥周期分段向量化。</para>
    /// </summary>
    private static byte[] DecryptData(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> sbox)
    {
        var output = new byte[encrypted.Length];
        DecryptInPlace(output, sbox);
        return output;
    }

    /// <summary>
    /// Decrypts a buffer in place with the SBox-based stream cipher.
    /// <para>使用 SBox 流密码对缓冲就地解密。</para>
    /// </summary>
    private static void DecryptInPlace(Span<byte> data, ReadOnlySpan<byte> sbox)
    {
        // 按 256 字节密钥周期分段：段内 key 序列固定，可用 Vector<byte>.Xor 批量处理。
        // Process in 256-byte key periods: the key sequence is constant within a segment,
        // enabling batched Vector<byte>.Xor.
        var width = System.Numerics.Vector<byte>.Count;
        var i = 0;
        while (i < data.Length)
        {
            var period = Math.Min(256, data.Length - i);
            var j = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated && period >= width)
            {
                for (; j <= period - width; j += width)
                {
                    System.Numerics.Vector.Xor(
                        new System.Numerics.Vector<byte>(data.Slice(i + j, width)),
                        new System.Numerics.Vector<byte>(sbox.Slice(j, width)))
                        .CopyTo(data.Slice(i + j, width));
                }
            }
            for (; j < period; j++)
            {
                data[i + j] ^= sbox[j];
            }
            i += period;
        }
    }

    /// <summary>
    /// Decrypts data using AES-ECB mode with the specified key
    /// <para>使用指定密钥以AES-ECB模式解密数据</para>
    /// </summary>
    public byte[] DecryptAesEcb(byte[] encryptedData, byte[] key)
    {
        if (encryptedData == null)
        {
            throw new ArgumentNullException(nameof(encryptedData));
        }

        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (encryptedData.Length == 0 || encryptedData.Length % OpsConstants.AES_BLOCK_SIZE != 0)
        {
            throw new ArgumentException("Encrypted data length must be a multiple of 16 bytes", nameof(encryptedData));
        }

        if (key.Length is not 16 and not 24 and not 32)
        {
            throw new ArgumentException("Key length must be 16, 24, or 32 bytes", nameof(key));
        }

        _logger.Debug($"Decrypting {encryptedData.Length} bytes with AES-ECB (key size: {key.Length * 8} bits)");

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
    }

    /// <summary>
    /// Decrypts data using AES-ECB mode with a key from the OnePlusKeyTable.
    /// Only the AES-suitable entry (index 0) is accepted; MBox data entries
    /// (indices 1–3) are rejected with <see cref="NotSupportedException"/> because
    /// they belong to a different decryption path and must not be fed to AES.
    /// <para>使用 OnePlusKeyTable 中的密钥以 AES-ECB 模式解密数据。
    /// 仅接受适合 AES 的条目（索引 0）；MBox 数据条目（索引 1–3）以
    /// <see cref="NotSupportedException"/> 拒绝，因为它们属于另一解密路径，
    /// 不得传入 AES。</para>
    /// </summary>
    /// <param name="encryptedData">The ciphertext to decrypt. 待解密的密文。</param>
    /// <param name="keyIndex">Index into <see cref="OnePlusKeyTable"/>; must be
    /// <see cref="OnePlusKeyTable.AesKeyIndex"/> (0) for the AES path.</param>
    /// <returns>The decrypted plaintext. 解密后的明文。</returns>
    /// <exception cref="NotSupportedException"><paramref name="keyIndex"/> refers to
    /// an MBox data entry that cannot be used with the AES-ECB interface.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keyIndex"/> is
    /// outside the table bounds.</exception>
    public byte[] DecryptAesEcb(byte[] encryptedData, int keyIndex)
    {
        if (keyIndex < 0 || keyIndex >= OnePlusKeyTable.KeyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIndex),
                $"Key index must be between 0 and {OnePlusKeyTable.KeyCount - 1}.");
        }

        if (keyIndex != OnePlusKeyTable.AesKeyIndex)
        {
            throw new NotSupportedException(
                $"Table entry {keyIndex} ('{OnePlusKeyTable.GetKeyName(keyIndex)}') is MBox data, not an AES key. " +
                "MBox data cannot be used with the AES-ECB interface; use the SBox stream-cipher path instead.");
        }

        var key = OnePlusKeyTable.GetAesKey(keyIndex);
        return DecryptAesEcb(encryptedData, key);
    }

    /// <summary>
    /// Derives a key from the default key with the specified variant
    /// <para>从默认密钥派生指定变体的密钥</para>
    /// </summary>
    private static byte[] DeriveKey(int variant)
    {
        var baseKey = new List<byte>(DefaultKey);
        for (var i = 0; i < variant; i++)
        {
            baseKey.Insert(0, (byte)(i ^ 0x5A));
        }

        return [.. baseKey.Take(32)];
    }

    /// <summary>
    /// Generates an SBox from the specified key using a key-scheduling algorithm
    /// <para>使用密钥调度算法从指定密钥生成SBox</para>
    /// </summary>
    private static byte[] GenerateSBox(byte[] key)
    {
        var sbox = new byte[256];
        Array.Copy(IdentitySBox, sbox, 256);
        for (int i = 255, j = 0; i > 0; i--)
        {
            j = (j + key[i % key.Length] + sbox[i]) & 0xFF;
            (sbox[i], sbox[j]) = (sbox[j], sbox[i]);
        }
        return sbox;
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException($"Unexpected end of stream: expected {count} bytes, read {totalRead} bytes.");
            }

            totalRead += bytesRead;
        }
    }
}