using System.Text;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

namespace UotanStudio.Firmware.Oppo.Crypto.Algorithms;

/// <summary>
/// <para>MTK（MediaTek）密钥派生算法，实现 MtkShuffle/MtkShuffle2 半字节交换操作及三元素/二元素密钥派生。</para>
/// MTK (MediaTek) key derivation algorithm implementing MtkShuffle/MtkShuffle2 nibble swap operations and 3-element/2-element key derivation.
/// </summary>
internal static class OppMtkKeyDerivation
{
    /// <summary>
    /// <para>使用三元素密钥配置（Mc + EncAesKey + EncAesIv）派生 AES 密钥和 IV。</para>
    /// Derives AES key and IV using the 3-element key configuration (Mc + EncAesKey + EncAesIv).
    /// </summary>
    /// <param name="entry"><para>MTK 密钥条目，必须为三元素配置。</para>The MTK key entry, must be a 3-element configuration.</param>
    /// <returns><para>包含 AES 密钥和 IV 的元组，均为 16 字节的 UTF-8 编码十六进制字符串前缀。</para>A tuple containing the AES key and IV, both 16-byte UTF-8 encoded hex string prefixes.</returns>
    internal static (byte[] AesKey, byte[] AesIv) DeriveKey3(MtkKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (!entry.Is3Element)
        {
            throw new InvalidOperationException(
                $"MTK key entry '{entry.KeyId}' is not a 3-element configuration.");
        }

        // Derive AesKey: HexDecode(Mc) as key, HexDecode(EncAesKey) as data → MtkShuffle2 → MD5 hex first 16 → UTF8
        var mcBytes = CryptoHelper.HexDecode(entry.Mc!);
        var encAesKeyBytes = CryptoHelper.HexDecode(entry.EncAesKey!);
        MtkShuffle2(mcBytes, encAesKeyBytes);
        var aesKey = CryptoHelper.Md5HexFirst16(encAesKeyBytes);

        // Derive AesIv: HexDecode(Mc) as key, HexDecode(EncAesIv) as data → MtkShuffle2 → MD5 hex first 16 → UTF8
        var mcBytes2 = CryptoHelper.HexDecode(entry.Mc!);
        var encAesIvBytes = CryptoHelper.HexDecode(entry.EncAesIv!);
        MtkShuffle2(mcBytes2, encAesIvBytes);
        var aesIv = CryptoHelper.Md5HexFirst16(encAesIvBytes);

        return (aesKey, aesIv);
    }

    /// <summary>
    /// <para>使用二元素密钥配置（DirectAesKey + DirectAesIv）派生 AES 密钥和 IV。</para>
    /// Derives AES key and IV using the 2-element key configuration (DirectAesKey + DirectAesIv).
    /// </summary>
    /// <param name="entry"><para>MTK 密钥条目，必须为二元素配置。</para>The MTK key entry, must be a 2-element configuration.</param>
    /// <returns><para>包含 AES 密钥和 IV 的元组。</para>A tuple containing the AES key and IV.</returns>
    internal static (byte[] AesKey, byte[] AesIv) DeriveKey2(MtkKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (!entry.Is2Element)
        {
            throw new InvalidOperationException(
                $"MTK key entry '{entry.KeyId}' is not a 2-element configuration.");
        }

        // DirectAesKey and DirectAesIv are hex strings whose UTF-8 encoding is used as the AES key/IV.
        var aesKey = Encoding.UTF8.GetBytes(entry.DirectAesKey!);
        var aesIv = Encoding.UTF8.GetBytes(entry.DirectAesIv!);

        return (aesKey, aesIv);
    }

    /// <summary>
    /// <para>对数据执行 MTK Shuffle 操作：先对每个字节进行半字节交换（高低 4 位互换），再与密钥异或。</para>
    /// Performs MTK Shuffle on data: nibble swap (swap high and low 4 bits) on each byte, then XOR with key.
    /// </summary>
    /// <param name="key"><para>混淆密钥，当数据长度超过密钥长度时密钥将循环使用。</para>The obfuscation key; cycles when data length exceeds key length.</param>
    /// <param name="data"><para>要处理的数据，操作结果直接写回此缓冲区。</para>The data to process; results are written directly back to this buffer.</param>
    internal static void MtkShuffle(ReadOnlyMemory<byte> key, Span<byte> data)
    {
        var keySpan = key.Span;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            var nibbleSwapped = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            data[i] = (byte)(nibbleSwapped ^ keySpan[i % keySpan.Length]);
        }
    }

    /// <summary>
    /// <para>对数据执行 MTK Shuffle2 操作：先将每个字节与密钥异或，再进行半字节交换（高低 4 位互换）。</para>
    /// Performs MTK Shuffle2 on data: XOR each byte with key, then nibble swap (swap high and low 4 bits).
    /// </summary>
    /// <param name="key"><para>混淆密钥，当数据长度超过密钥长度时密钥将循环使用。</para>The obfuscation key; cycles when data length exceeds key length.</param>
    /// <param name="data"><para>要处理的数据，操作结果直接写回此缓冲区。</para>The data to process; results are written directly back to this buffer.</param>
    internal static void MtkShuffle2(ReadOnlyMemory<byte> key, Span<byte> data)
    {
        var keySpan = key.Span;
        for (var i = 0; i < data.Length; i++)
        {
            var x = (byte)(data[i] ^ keySpan[i % keySpan.Length]);
            data[i] = (byte)(((x & 0xF0) >> 4) | ((x & 0x0F) << 4));
        }
    }
}