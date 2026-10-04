namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Codec for the 9-byte IMEI encoding stored in NV item 550 (and platform-dependent companion
/// items). The layout is: byte 0 is the constant <c>0x08</c> marker; byte 1 carries the first
/// digit in the high nibble and an <c>0xA</c> filler in the low nibble; bytes 2..7 each carry an
/// odd-position digit in the high nibble and the preceding even-position digit in the low nibble;
/// byte 8 carries the Luhn check digit in the high nibble and the fourteenth digit in the low nibble.
/// <para>NV 条目 550（及随平台变化的伴随条目）中存储的 9 字节 IMEI 编解码器。布局为：字节 0 是常量
/// <c>0x08</c> 标记；字节 1 高半字节为第 1 位数字、低半字节为 <c>0xA</c> 填充；字节 2..7 每个字节
/// 高半字节为奇数位数字、低半字节为其前一个偶数位数字；字节 8 高半字节为 Luhn 校验位、低半字节为
/// 第 14 位数字。</para>
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

    /// <summary>
    /// Decodes a 9-byte encoded IMEI into its 15 decimal digits.
    /// <para>将 9 字节编码的 IMEI 解码为 15 位十进制数字串。</para>
    /// </summary>
    /// <param name="data">
    /// <para>编码数据，至少 <see cref="EncodedLength"/> 字节。</para>
    /// The encoded data, at least <see cref="EncodedLength"/> bytes.
    /// </param>
    /// <param name="imei">
    /// <para>解码出的 15 位数字串；解码失败时为 <c>null</c>。</para>
    /// The decoded 15-digit string; <c>null</c> when decoding fails.
    /// </param>
    /// <param name="checkDigitValid">
    /// <para>解码出的校验位是否与按前 14 位计算的 Luhn 校验位一致。</para>
    /// Whether the decoded check digit matches the Luhn check digit computed from the first 14 digits.
    /// </param>
    /// <returns>
    /// <para>解码成功返回 <c>true</c>；数据不是有效的 IMEI 编码时返回 <c>false</c>。</para>
    /// <c>true</c> on success; <c>false</c> when the data is not a valid IMEI encoding.
    /// </returns>
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

    /// <summary>
    /// Encodes a 15-digit IMEI (including its check digit) into the 9-byte NV representation.
    /// <para>将 15 位 IMEI（含校验位）编码为 9 字节的 NV 表示。</para>
    /// </summary>
    /// <param name="imei">
    /// <para>15 位数字的 IMEI 字符串。</para>
    /// The 15-digit IMEI string.
    /// </param>
    /// <param name="destination">
    /// <para>至少 <see cref="EncodedLength"/> 字节的目标缓冲区。</para>
    /// The destination buffer, at least <see cref="EncodedLength"/> bytes.
    /// </param>
    /// <returns>
    /// <para>编码成功返回 <c>true</c>；输入不是 15 位数字时返回 <c>false</c>。</para>
    /// <c>true</c> on success; <c>false</c> when the input is not 15 digits.
    /// </returns>
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

    /// <summary>
    /// Computes the Luhn check digit over the first 14 digits of an IMEI; starting from the
    /// rightmost digit of the body, every second digit (odd positions counting from the right) is
    /// doubled and digits above 9 are folded back.
    /// <para>按 IMEI 前 14 位计算 Luhn 校验位；从主体最右位开始，每隔一位（自右起奇数位）加倍，
    /// 大于 9 的结果做折返。</para>
    /// </summary>
    /// <param name="digits">
    /// <para>14 位数字的 IMEI 前缀。</para>
    /// The 14-digit IMEI prefix.
    /// </param>
    /// <returns>
    /// <para>校验位数字的字符。</para>
    /// The check digit character.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <para>输入长度不是 14 或包含非数字字符。</para>
    /// The input is not 14 digits or contains non-digit characters.
    /// </exception>
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