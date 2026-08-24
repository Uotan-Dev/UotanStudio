using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.OnePlusOps.Logging;

namespace UotanStudio.Firmware.OnePlusOps;

/// <summary>
/// Provides decryption capabilities for OnePlus OPS firmware files,
/// supporting both SBox-based stream cipher and AES-ECB decryption
/// </summary>
/// <para>提供OnePlus OPS固件文件的解密功能，支持基于SBox的流密码和AES-ECB解密</para>
/// <remarks>
/// Initializes a new instance of the OpsDecryptor class with the specified stream and optional logger
/// </remarks>
/// <para>使用指定的流和可选的日志记录器初始化OpsDecryptor类的新实例</para>
/// <param name="stream">The stream containing the OPS firmware data</param>
/// <param name="logger">Optional logger for diagnostic output; defaults to NullLogger if not provided</param>
/// <exception cref="ArgumentNullException">Thrown when stream is null</exception>
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
    /// </summary>
    /// <para>从当前流位置解析OPS头部</para>
    /// <returns>The parsed OpsHeader instance</returns>
    /// <exception cref="InvalidDataException">Thrown when the OPS magic signature is invalid</exception>
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
    /// </summary>
    /// <para>解析头部之后的OPS条目表</para>
    /// <param name="entryCount">The number of entries to read</param>
    /// <returns>The parsed OpsEntry list</returns>
    /// <exception cref="EndOfStreamException">Thrown when the stream ends unexpectedly</exception>
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
    /// </summary>
    /// <para>从流的指定绝对偏移读取条目的原始数据</para>
    /// <param name="offset">The absolute stream offset of the entry data</param>
    /// <param name="size">The size of the entry data in bytes</param>
    /// <returns>The raw entry data</returns>
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
    /// </summary>
    /// <para>使用基于SBox的流密码和默认密钥解密数据</para>
    /// <param name="encryptedData">The encrypted data to decrypt</param>
    /// <param name="variant">The key variant index (default is 0)</param>
    /// <returns>The decrypted data</returns>
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
    /// </summary>
    /// <para>使用指定密钥以AES-ECB模式解密数据</para>
    /// <param name="encryptedData">The encrypted data to decrypt (must be a multiple of 16 bytes)</param>
    /// <param name="key">The AES decryption key (16, 24, or 32 bytes)</param>
    /// <returns>The decrypted data</returns>
    /// <exception cref="ArgumentNullException">Thrown when encryptedData or key is null</exception>
    /// <exception cref="ArgumentException">Thrown when encryptedData length is not a multiple of 16 or key length is invalid</exception>
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
    /// </summary>
    /// <para>使用OnePlusKeyTable中的密钥以AES-ECB模式解密数据</para>
    /// <param name="encryptedData">The encrypted data to decrypt (must be a multiple of 16 bytes)</param>
    /// <param name="keyIndex">The index of the key in the OnePlusKeyTable</param>
    /// <returns>The decrypted data</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when keyIndex is out of range</exception>
    public byte[] DecryptAesEcb(byte[] encryptedData, int keyIndex)
    {
        var key = OnePlusKeyTable.GetKey(keyIndex);
        return DecryptAesEcb(encryptedData, key);
    }

    /// <summary>
    /// Derives a key from the default key with the specified variant
    /// </summary>
    /// <para>从默认密钥派生指定变体的密钥</para>
    /// <param name="variant">The variant index for key derivation</param>
    /// <returns>The derived key bytes (up to 32 bytes)</returns>
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
    /// </summary>
    /// <para>使用密钥调度算法从指定密钥生成SBox</para>
    /// <param name="key">The key used for SBox generation</param>
    /// <returns>A 256-byte SBox array</returns>
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