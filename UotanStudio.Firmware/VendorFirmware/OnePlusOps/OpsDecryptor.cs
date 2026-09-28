using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.OnePlusOps;

namespace UotanStudio.Firmware.VendorFirmware.OnePlusOps;

/// <summary>
/// Reads and decrypts entries of a OnePlus OPS package from a stream. Header and entry-table
/// parsing consume bytes sequentially from the current position, while <see cref="ReadEntryData"/>
/// seeks to absolute offsets. Decryption covers the SBox-based stream cipher and AES-ECB. The
/// stream stays open and owned by the caller for the lifetime of this instance.
/// <para>从流中读取并解密 OnePlus OPS 包的条目。头部与条目表解析从当前位置顺序消费字节，
/// <see cref="ReadEntryData"/> 则按绝对偏移定位。解密路径覆盖基于 SBox 的流密码与 AES-ECB。
/// 流在整个实例生命周期内保持打开，所有权归调用方。</para>
/// </summary>
/// <param name="stream">OPS package stream positioned where the header starts.
/// <para>定位到头部起始处的 OPS 数据流。</para></param>
/// <param name="logger">Optional logger; <see langword="null"/> uses a silent sink.
/// <para>可选日志器；为 <see langword="null"/> 时使用静默接收端。</para></param>
/// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
/// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
internal class OpsDecryptor(Stream stream, IOnePlusLogger? logger = null)
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
    /// <returns>The header validated against the OPS magic signature.
    /// <para>已通过 OPS 魔数签名校验的头部。</para></returns>
    /// <exception cref="EndOfStreamException">Fewer than the 52 header bytes remain to be read.
    /// <para>剩余可读字节不足头部的 52 字节。</para></exception>
    /// <exception cref="InvalidDataException">The header magic signature is invalid.
    /// <para>头部魔数签名无效。</para></exception>
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
    /// <param name="entryCount">Number of 32-byte entry headers to read, normally the validated
    /// <see cref="OpsHeader.EntryCount"/> value; it is consumed as given and not bounded here.
    /// <para>需要读取的 32 字节条目头数量，通常应取自已校验的 <see cref="OpsHeader.EntryCount"/>；
    /// 该值按原样使用，本方法不做上限约束。</para></param>
    /// <returns>The entry list in stream order. <para>按流内顺序排列的条目列表。</para></returns>
    /// <exception cref="EndOfStreamException">The stream ends before <paramref name="entryCount"/>
    /// headers are read. <para>流在读完 <paramref name="entryCount"/> 个条目头之前结束。</para></exception>
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
    /// <param name="offset">Absolute offset from the start of the stream where the entry data begins.
    /// <para>条目数据起始位置相对流开头的绝对偏移。</para></param>
    /// <param name="size">Exact number of bytes to read; 0 returns an empty array without seeking.
    /// <para>需要精确读取的字节数；为 0 时不移动流位置并直接返回空数组。</para></param>
    /// <returns>The raw, still encrypted entry bytes. <para>原始（仍加密）的条目字节。</para></returns>
    /// <exception cref="IOException">The stream does not support seeking or a read failed.
    /// <para>流不支持寻址或读取失败。</para></exception>
    /// <exception cref="EndOfStreamException">Fewer than <paramref name="size"/> bytes remain after seeking.
    /// <para>定位后剩余字节少于 <paramref name="size"/>。</para></exception>
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
    /// <param name="encryptedData">Raw entry bytes, e.g. from <see cref="ReadEntryData(uint, uint)"/>.
    /// <para>原始条目字节，例如来自 <see cref="ReadEntryData(uint, uint)"/>。</para></param>
    /// <param name="variant">Key-derivation variant; 0 uses the built-in 16-byte default key as-is,
    /// and each step above 0 prepends one derived byte before the key material is truncated to
    /// 32 bytes. A negative value behaves like 0.
    /// <para>密钥派生变体；0 表示直接使用内置 16 字节默认密钥，每增加 1 在密钥前追加一个派生字节，
    /// 最终截断至 32 字节。负值与 0 等效。</para></param>
    /// <returns>A new array holding the decrypted bytes, with the same length as the input.
    /// <para>包含解密结果的新数组，长度与输入相同。</para></returns>
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
    /// <param name="encryptedData">Ciphertext whose length is a positive multiple of 16 bytes.
    /// <para>长度为非零且为 16 字节整数倍的密文。</para></param>
    /// <param name="key">AES key of 16, 24, or 32 bytes. <para>16、24 或 32 字节的 AES 密钥。</para></param>
    /// <returns>The decrypted blocks; no padding is added or removed.
    /// <para>解密后的数据块；不添加也不移除填充。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="encryptedData"/> or
    /// <paramref name="key"/> is <see langword="null"/>.
    /// <para><paramref name="encryptedData"/> 或 <paramref name="key"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="encryptedData"/> is empty or not a
    /// multiple of 16 bytes, or <paramref name="key"/> is not 16, 24 or 32 bytes.
    /// <para><paramref name="encryptedData"/> 为空或长度不是 16 的整数倍，或 <paramref name="key"/>
    /// 不是 16、24、32 字节。</para></exception>
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
    /// <param name="encryptedData">The ciphertext to decrypt.
    /// <para>待解密的密文。</para></param>
    /// <param name="keyIndex">Index into <see cref="OnePlusKeyTable"/>; must be
    /// <see cref="OnePlusKeyTable.AesKeyIndex"/> (0) for the AES path.
    /// <para><see cref="OnePlusKeyTable"/> 中的索引；AES 路径必须使用
    /// <see cref="OnePlusKeyTable.AesKeyIndex"/>（0）。</para></param>
    /// <returns>The decrypted plaintext. <para>解密后的明文。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keyIndex"/> is
    /// outside the table bounds. <para><paramref name="keyIndex"/> 超出表的索引范围。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="keyIndex"/> refers to
    /// an MBox data entry that cannot be used with the AES-ECB interface.
    /// <para><paramref name="keyIndex"/> 指向无法用于 AES-ECB 接口的 MBox 数据条目。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="encryptedData"/> is
    /// <see langword="null"/>. <para><paramref name="encryptedData"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="encryptedData"/> length is not a
    /// positive multiple of 16 bytes.
    /// <para><paramref name="encryptedData"/> 长度不是非零的 16 字节整数倍。</para></exception>
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