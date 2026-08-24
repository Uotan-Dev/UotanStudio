using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>加密工具类，提供 MD5 哈希、十六进制编解码等共享方法。</para>
/// Crypto utility class providing MD5 hashing, hex encoding/decoding, and other shared methods.
/// </summary>
internal static class CryptoHelper
{
    /// <summary>
    /// <para>计算数据的 MD5 哈希，将结果转换为小写十六进制字符串，取前 16 个字符并编码为 UTF-8 字节数组。</para>
    /// Computes the MD5 hash of the data, converts the result to a lowercase hex string,
    /// takes the first 16 characters, and encodes them as UTF-8 bytes.
    /// </summary>
    /// <param name="data"><para>要计算哈希的数据。</para>The data to hash.</param>
    /// <returns><para>前 16 个十六进制字符的 UTF-8 编码字节数组。</para>The UTF-8 encoded byte array of the first 16 hex characters.</returns>
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
    /// <para>将十六进制字符串解码为字节数组。</para>
    /// Decodes a hex string to a byte array.
    /// </summary>
    /// <param name="hex"><para>十六进制字符串。</para>The hex string.</param>
    /// <returns><para>解码后的字节数组。</para>The decoded byte array.</returns>
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
    /// <para>将单个十六进制字符转换为其整数值。</para>
    /// Converts a single hex character to its integer value.
    /// </summary>
    /// <param name="c"><para>十六进制字符。</para>The hex character.</param>
    /// <returns><para>对应的整数值。</para>The corresponding integer value.</returns>
    internal static int HexCharToInt(char c)
    {
        return c is >= '0' and <= '9'
            ? c - '0'
            : c is >= 'a' and <= 'f'
                ? c - 'a' + 10
                : c is >= 'A' and <= 'F' ? c - 'A' + 10 : throw new FormatException($"Invalid hex character: '{c}'");
    }
}