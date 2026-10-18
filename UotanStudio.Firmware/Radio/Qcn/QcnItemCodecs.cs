using System.Text;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Display and structured-edit codecs for the well-known NV item data formats other than the IMEI:
/// SPC (six ASCII digits), MEID and ESN (byte-reversed hex), and Bluetooth/WLAN MAC addresses.
/// <para>除 IMEI 之外的已知 NV 条目数据格式的显示与结构化编解码器：SPC（6 位 ASCII 数字）、MEID
/// 与 ESN（字节反序十六进制），以及蓝牙/WLAN MAC 地址。</para>
/// </summary>
public static class QcnItemCodecs
{
    /// <summary>
    /// Decodes the SPC (security code, NV item 85) stored as six ASCII digits.
    /// <para>解码以 6 位 ASCII 数字存储的 SPC（安全码，NV 条目 85）。</para>
    /// </summary>
    /// <param name="data">
    /// The NV data region; the first 6 bytes must be ASCII digits.
    /// <para>NV 数据区，前 6 字节须为 ASCII 数字。</para>
    /// </param>
    /// <returns>
    /// The SPC string; <c>null</c> when the data is too short or contains non-digit bytes.
    /// <para>SPC 字符串；数据不足或含非数字字节时为 <c>null</c>。</para>
    /// </returns>
    public static string? DecodeSpc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return null;
        }

        foreach (var b in data[..6])
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(data[..6]);
    }

    /// <summary>Encodes a six-digit SPC into the destination buffer as ASCII digits.
    /// <para>将 6 位 SPC 以 ASCII 数字编码写入目标缓冲区。</para></summary>
    /// <param name="spc">The 6-digit SPC string.<para>6 位数字的 SPC 字符串。</para></param>
    /// <param name="destination">The destination buffer, at least 6 bytes.
    /// <para>至少 6 字节的目标缓冲区。</para></param>
    /// <returns><c>true</c> on success; <c>false</c> when the input is not six digits.
    /// <para>编码成功返回 <c>true</c>；输入不是 6 位数字时返回 <c>false</c>。</para></returns>
    public static bool TryEncodeSpc(ReadOnlySpan<char> spc, Span<byte> destination)
    {
        if (spc.Length != 6 || destination.Length < 6)
        {
            return false;
        }

        for (var i = 0; i < 6; i++)
        {
            var digit = spc[i] - '0';
            if (digit is < 0 or > 9)
            {
                return false;
            }

            destination[i] = (byte)('0' + digit);
        }

        return true;
    }

    /// <summary>Decodes a byte-reversed hex identifier (MEID items 1943/5598 with 7 bytes, ESN items 0/5597 with 4 bytes) into its conventional display order.
    /// <para>将字节反序的十六进制标识（MEID 条目 1943/5598 为 7 字节，ESN 条目 0/5597 为 4 字节） 解码为常规显示顺序。</para></summary>
    /// <param name="data">The NV data region; the first <paramref name="length"/> bytes are the reversed raw bytes.
    /// <para>NV 数据区，前 <paramref name="length"/> 字节为反序原始字节。</para></param>
    /// <param name="length">The identifier byte length (7 for MEID, 4 for ESN).
    /// <para>标识字节长度（MEID 为 7，ESN 为 4）。</para></param>
    /// <returns>The uppercase hex display string; <c>null</c> when the data is too short.
    /// <para>大写十六进制显示串；数据不足时为 <c>null</c>。</para></returns>
    public static string? DecodeReversedHex(ReadOnlySpan<byte> data, int length)
    {
        if (data.Length < length || length > 8)
        {
            return null;
        }

        Span<char> chars = stackalloc char[16];
        for (var i = 0; i < length; i++)
        {
            data[length - 1 - i].TryFormat(chars[(2 * i)..], out _, "X2");
        }

        return new string(chars[..(2 * length)]);
    }

    /// <summary>Encodes a reversed-hex identifier (MEID/ESN) from its conventional display order.
    /// <para>按常规显示顺序编码反序十六进制标识（MEID/ESN）。</para></summary>
    /// <param name="hex">The even-length hex string (14 digits for MEID or 8 for ESN), any case.
    /// <para>偶数长度的大写或小写十六进制串（14 位 MEID 或 8 位 ESN）。</para></param>
    /// <param name="destination">The destination buffer, half the hex digit count in length.
    /// <para>目标缓冲区，长度为十六进制位数的一半。</para></param>
    /// <returns><c>true</c> on success; <c>false</c> when the input is invalid.
    /// <para>编码成功返回 <c>true</c>；输入无效时返回 <c>false</c>。</para></returns>
    public static bool TryEncodeReversedHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Length > 16 || destination.Length < hex.Length / 2)
        {
            return false;
        }

        var length = hex.Length / 2;
        for (var i = 0; i < length; i++)
        {
            if (!Uri.IsHexDigit(hex[2 * i]) || !Uri.IsHexDigit(hex[(2 * i) + 1]))
            {
                return false;
            }

            destination[length - 1 - i] = (byte)((Uri.FromHex(hex[2 * i]) << 4) | Uri.FromHex(hex[(2 * i) + 1]));
        }

        return true;
    }

    /// <summary>
    /// Decodes a six-byte MAC address into its colon-separated display form.
    /// <para>将 6 字节 MAC 地址解码为冒号分隔的显示形式。</para>
    /// </summary>
    /// <param name="data">
    /// The NV data region; the first 6 bytes are the MAC bytes.
    /// <para>NV 数据区，前 6 字节为 MAC 字节。</para>
    /// </param>
    /// <returns>
    /// A display string such as <c>AA:BB:CC:DD:EE:FF</c>; <c>null</c> when the data is too short.
    /// <para>形如 <c>AA:BB:CC:DD:EE:FF</c> 的显示串；数据不足时为 <c>null</c>。</para>
    /// </returns>
    public static string? DecodeMac(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return null;
        }

        Span<char> chars = stackalloc char[17];
        for (var i = 0; i < 6; i++)
        {
            if (i > 0)
            {
                chars[(3 * i) - 1] = ':';
            }

            data[i].TryFormat(chars[(3 * i)..], out _, "X2");
        }

        return new string(chars);
    }

    /// <summary>Encodes a colon-separated or continuous hex MAC address into six bytes.
    /// <para>将冒号分隔或连续十六进制的 MAC 地址编码为 6 字节。</para></summary>
    /// <param name="text">The MAC address text (colon-separated or 12 continuous hex digits).
    /// <para>MAC 地址文本（冒号分隔或 12 位连续十六进制）。</para></param>
    /// <param name="destination">The destination buffer, at least 6 bytes.
    /// <para>至少 6 字节的目标缓冲区。</para></param>
    /// <returns><c>true</c> on success; <c>false</c> when the input is invalid.
    /// <para>编码成功返回 <c>true</c>；输入无效时返回 <c>false</c>。</para></returns>
    public static bool TryEncodeMac(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (destination.Length < 6)
        {
            return false;
        }

        Span<char> hex = stackalloc char[12];
        var count = 0;
        foreach (var c in text)
        {
            if (c == ':')
            {
                continue;
            }

            if (count >= 12 || !Uri.IsHexDigit(c))
            {
                return false;
            }

            hex[count++] = char.ToUpperInvariant(c);
        }

        if (count != 12)
        {
            return false;
        }

        for (var i = 0; i < 6; i++)
        {
            destination[i] = (byte)((Uri.FromHex(hex[2 * i]) << 4) | Uri.FromHex(hex[(2 * i) + 1]));
        }

        return true;
    }
}