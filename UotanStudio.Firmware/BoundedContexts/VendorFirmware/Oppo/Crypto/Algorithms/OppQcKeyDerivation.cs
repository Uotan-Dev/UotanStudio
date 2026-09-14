using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.KeyDatabase;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.Algorithms;

internal static class OppQcKeyDerivation
{
    /// <summary>
    /// Derives AES key and IV from a QC key entry.
    /// <para>从 QC 密钥条目派生 AES 密钥和 IV。</para>
    /// </summary>
    internal static (byte[] AesKey, byte[] AesIv) DeriveKey(QcKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }


        var mcBytes = CryptoHelper.HexDecode(entry.Mc);


        var mcBytesForKey = (byte[])mcBytes.Clone();
        var userKeyBytes = CryptoHelper.HexDecode(entry.UserKey);
        Deobfuscate(mcBytesForKey, userKeyBytes);
        var aesKey = CryptoHelper.Md5HexFirst16(mcBytesForKey);


        var mcBytesForIv = (byte[])mcBytes.Clone();
        var ivecBytes = CryptoHelper.HexDecode(entry.Ivec);
        Deobfuscate(mcBytesForIv, ivecBytes);
        var aesIv = CryptoHelper.Md5HexFirst16(mcBytesForIv);

        return (aesKey, aesIv);
    }

    /// <summary>
    /// Deobfuscates data: each byte is XORed with the mask, then rotated left by 4 bits (nibble swap).
    /// <para>对数据进行去混淆操作：每个字节先与掩码异或，再循环左移 4 位（半字节交换）。</para>
    /// </summary>
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