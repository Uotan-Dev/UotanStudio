using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

namespace UotanStudio.Firmware.Oppo.Crypto.Algorithms;

/// <summary>
/// <para>QC（Qualcomm）密钥派生算法，实现 HexDecode → Deobfuscate（XOR + 循环左移 4 位（半字节交换））→ MD5 → hex → UTF8 的密钥派生流水线。</para>
/// QC (Qualcomm) key derivation algorithm implementing the HexDecode → Deobfuscate (XOR + rotate left 4 / nibble swap) → MD5 → hex → UTF8 key derivation pipeline.
/// </summary>
internal static class OppQcKeyDerivation
{
    /// <summary>
    /// <para>从 QC 密钥条目派生 AES 密钥和 IV。</para>
    /// Derives AES key and IV from a QC key entry.
    /// </summary>
    /// <param name="entry"><para>QC 密钥条目，包含 Mc、UserKey 和 Ivec 字段。</para>The QC key entry containing Mc, UserKey, and Ivec fields.</param>
    /// <returns><para>包含 AES 密钥和 IV 的元组，均为 16 字节的 UTF-8 编码十六进制字符串前缀。</para>A tuple containing the AES key and IV, both 16-byte UTF-8 encoded hex string prefixes.</returns>
    internal static (byte[] AesKey, byte[] AesIv) DeriveKey(QcKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        // Decode Mc once and reuse for both AesKey and AesIv derivation
        var mcBytes = CryptoHelper.HexDecode(entry.Mc);

        // Derive AesKey: copy of mcBytes → Deobfuscate with mask from UserKey → MD5 → hex first 16 → UTF8
        var mcBytesForKey = (byte[])mcBytes.Clone();
        var userKeyBytes = CryptoHelper.HexDecode(entry.UserKey);
        Deobfuscate(mcBytesForKey, userKeyBytes);
        var aesKey = CryptoHelper.Md5HexFirst16(mcBytesForKey);

        // Derive AesIv: copy of mcBytes → Deobfuscate with mask from Ivec → MD5 → hex first 16 → UTF8
        var mcBytesForIv = (byte[])mcBytes.Clone();
        var ivecBytes = CryptoHelper.HexDecode(entry.Ivec);
        Deobfuscate(mcBytesForIv, ivecBytes);
        var aesIv = CryptoHelper.Md5HexFirst16(mcBytesForIv);

        return (aesKey, aesIv);
    }

    /// <summary>
    /// <para>对数据进行去混淆操作：每个字节先与掩码异或，再循环左移 4 位（半字节交换）。</para>
    /// Deobfuscates data: each byte is XORed with the mask, then rotated left by 4 bits (nibble swap).
    /// </summary>
    /// <param name="data"><para>要处理的数据，操作结果直接写回此缓冲区。</para>The data to process; results are written directly back to this buffer.</param>
    /// <param name="mask"><para>异或掩码，长度应与 data 相同。</para>The XOR mask; should be the same length as data.</param>
    internal static void Deobfuscate(Span<byte> data, ReadOnlySpan<byte> mask)
    {
        if (data.Length != mask.Length)
        {
            throw new ArgumentException("Data and mask must have the same length.");
        }

        for (var i = 0; i < data.Length; i++)
        {
            var x = (byte)(data[i] ^ mask[i]);
            data[i] = (byte)((x << 4) | (x >> 4));
        }
    }
}