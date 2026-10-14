using System.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

internal static class CryptoHelper
{
    /// <summary>
    /// Computes the MD5 hash of the data, converts the result to a lowercase hex string, takes the first 16 characters, and encodes them as UTF-8 bytes.
    /// <para>计算数据的 MD5 哈希，将结果转换为小写十六进制字符串，取前 16 个字符并编码为 UTF-8 字节数组。</para>
    /// </summary>
    internal static byte[] Md5HexFirst16(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        var hex = Md5.ComputeHexLower(data);
        return Encoding.UTF8.GetBytes(hex[..16]);
    }

    /// <summary>
    /// Decodes a hex string to a byte array.
    /// <para>将十六进制字符串解码为字节数组。</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="hex"/> is <see langword="null"/>.
    /// <para><paramref name="hex"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FormatException"><paramref name="hex"/> has an odd length or contains a character outside
    /// the hexadecimal alphabet.
    /// <para><paramref name="hex"/> 长度为奇数，或包含十六进制字符集之外的字符。</para></exception>
    internal static byte[] HexDecode(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return Convert.FromHexString(hex);
    }
}