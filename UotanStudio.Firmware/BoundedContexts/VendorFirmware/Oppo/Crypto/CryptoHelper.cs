using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

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

        var hash = MD5.HashData(data);

        var hex = new StringBuilder(32);
        for (var i = 0; i < hash.Length; i++)
        {
            hex.Append(hash[i].ToString("x2"));
        }

        var hexStr = hex.ToString();
        return Encoding.UTF8.GetBytes(hexStr[..16]);
    }

    /// <summary>
    /// Decodes a hex string to a byte array.
    /// <para>将十六进制字符串解码为字节数组。</para>
    /// </summary>
    internal static byte[] HexDecode(string hex)
    {
        if (hex is null)
        {
            throw new ArgumentNullException(nameof(hex));
        }

        if (hex.Length % 2 != 0)
        {
            throw new FormatException("Hex string must have an even number of characters.");
        }

        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexCharToInt(hex[i * 2]) << 4) | HexCharToInt(hex[(i * 2) + 1]));
        }

        return result;
    }

    /// <summary>
    /// Converts a single hex character to its integer value.
    /// <para>将单个十六进制字符转换为其整数值。</para>
    /// </summary>
    internal static int HexCharToInt(char c)
    {
        return c is >= '0' and <= '9'
            ? c - '0'
            : c is >= 'a' and <= 'f'
                ? c - 'a' + 10
                : c is >= 'A' and <= 'F' ? c - 'A' + 10 : throw new FormatException($"Invalid hex character: '{c}'");
    }
}
