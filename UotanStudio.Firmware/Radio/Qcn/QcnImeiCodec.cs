namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Codec for the 9-byte IMEI encoding stored in NV item 550 and its platform-dependent companion items, packing the 15 IMEI digits plus the Luhn check digit into nibble pairs.
/// <para>NV 项 550（及其平台相关伴随项）中 9 字节 IMEI 编码的编解码器，把 15 位 IMEI 数字与 Luhn 校验位以半字节对方式打包。</para>
/// </summary>
public static class QcnImeiCodec
{
    /// <summary>
    /// Number of decimal digits of a full IMEI including the Luhn check digit.
    /// <para>含 Luhn 校验位的完整 IMEI 十进制位数。</para>
    /// </summary>
    public const int DigitCount = 15;

    /// <summary>
    /// Encoded IMEI byte length.
    /// <para>IMEI 编码后的字节长度。</para>
    /// </summary>
    public const int EncodedLength = 9;

    /// <summary>Decodes a 9-byte encoded IMEI into its 15 decimal digits.
    /// <para>将 9 字节编码的 IMEI 解码为 15 位十进制数字串。</para></summary>
    /// <param name="data">The encoded data, at least <see cref="EncodedLength"/> bytes.
    /// <para>编码数据，至少 <see cref="EncodedLength"/> 字节。</para></param>
    /// <param name="imei">The decoded 15-digit string; <c>null</c> when decoding fails.
    /// <para>解码出的 15 位数字串；解码失败时为 <c>null</c>。</para></param>
    /// <param name="checkDigitValid">Whether the decoded check digit matches the Luhn check digit computed from the first 14 digits.
    /// <para>解码出的校验位是否与按前 14 位计算的 Luhn 校验位一致。</para></param>
    /// <returns><c>true</c> on success; <c>false</c> when the data is not a valid IMEI encoding.
    /// <para>解码成功返回 <c>true</c>；数据不是有效的 IMEI 编码时返回 <c>false</c>。</para></returns>
    public static bool TryDecode(ReadOnlySpan<byte> data, out string? imei, out bool checkDigitValid)
    {
        imei = null;
        checkDigitValid = false;
        if (data.Length < EncodedLength || data[0] != 0x08)
        {
            return false;
        }

        Span<char> digits = stackalloc char[DigitCount];
        digits[0] = (char)('0' + (data[1] >> 4));
        for (var k = 2; k <= 7; k++)
        {
            var high = data[k] >> 4;
            var low = data[k] & 0x0F;
            if (high > 9 || low > 9)
            {
                return false;
            }

            digits[2 * k - 3] = (char)('0' + low);
            digits[2 * k - 2] = (char)('0' + high);
        }

        var fourteenth = data[8] & 0x0F;
        var check = data[8] >> 4;
        if (fourteenth > 9 || check > 9)
        {
            return false;
        }

        digits[13] = (char)('0' + fourteenth);
        digits[14] = (char)('0' + check);
        imei = new string(digits);
        checkDigitValid = ComputeCheckDigit(imei.AsSpan(0, 14)) == imei[14];
        return true;
    }

    /// <summary>Encodes a 15-digit IMEI (including its check digit) into the 9-byte NV representation.
    /// <para>将 15 位 IMEI（含校验位）编码为 9 字节的 NV 表示。</para></summary>
    /// <param name="imei">The 15-digit IMEI string.<para>15 位数字的 IMEI 字符串。</para></param>
    /// <param name="destination">The destination buffer, at least <see cref="EncodedLength"/> bytes.
    /// <para>至少 <see cref="EncodedLength"/> 字节的目标缓冲区。</para></param>
    /// <returns><c>true</c> on success; <c>false</c> when the input is not 15 digits.
    /// <para>编码成功返回 <c>true</c>；输入不是 15 位数字时返回 <c>false</c>。</para></returns>
    public static bool TryEncode(ReadOnlySpan<char> imei, Span<byte> destination)
    {
        if (imei.Length != DigitCount || destination.Length < EncodedLength)
        {
            return false;
        }

        Span<byte> digits = stackalloc byte[DigitCount];
        for (var i = 0; i < DigitCount; i++)
        {
            var digit = imei[i] - '0';
            if (digit is < 0 or > 9)
            {
                return false;
            }

            digits[i] = (byte)digit;
        }

        destination[0] = 0x08;
        destination[1] = (byte)((digits[0] << 4) | 0x0A);
        for (var k = 2; k <= 7; k++)
        {
            destination[k] = (byte)((digits[2 * k - 2] << 4) | digits[2 * k - 3]);
        }

        destination[8] = (byte)((digits[14] << 4) | digits[13]);
        return true;
    }

    /// <summary>Computes the Luhn check digit over the first 14 digits of an IMEI.
    /// <para>按 IMEI 前 14 位计算 Luhn 校验位。</para></summary>
    /// <param name="digits">The 14-digit IMEI prefix.<para>14 位数字的 IMEI 前缀。</para></param>
    /// <returns>The check digit character.<para>校验位数字的字符。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The input is not 14 digits or contains non-digit characters.
    /// <para>输入长度不是 14 或包含非数字字符。</para></exception>
    public static char ComputeCheckDigit(ReadOnlySpan<char> digits)
    {
        if (digits.Length != 14)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), "The IMEI check digit is computed over exactly 14 digits.");
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[i] - '0';
            if (digit is < 0 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(digits), "The IMEI prefix contains a non-digit character.");
            }

            if ((digits.Length - i) % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}