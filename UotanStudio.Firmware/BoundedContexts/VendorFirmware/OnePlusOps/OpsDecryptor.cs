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

        for (uint i = 0; i < entryCount; i++)
        {
            var buffer = new byte[OpsConstants.ENTRY_HEADER_SIZE];
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
        var output = new byte[encryptedData.Length];

        for (var i = 0; i < encryptedData.Length; i++)
        {
            output[i] = (byte)(encryptedData[i] ^ sbox[i % 256]);
        }

        return output;
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
    /// Decrypts data using AES-ECB mode with a key from the OnePlusKeyTable
    /// <para>使用OnePlusKeyTable中的密钥以AES-ECB模式解密数据</para>
    /// </summary>
    public byte[] DecryptAesEcb(byte[] encryptedData, int keyIndex)
    {
        var key = OnePlusKeyTable.GetKey(keyIndex);
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
