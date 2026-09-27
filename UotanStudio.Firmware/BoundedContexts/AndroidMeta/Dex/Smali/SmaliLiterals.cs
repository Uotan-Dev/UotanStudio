using System.Globalization;
using System.Numerics;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

internal static class SmaliLiterals
{
    internal static EncodedValue Scalar(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("A scalar literal cannot be empty.");
        }
        if (text.StartsWith('\''))
        {
            return EncodedValue.Char(SmaliSyntax.CharacterLiteral(text));
        }
        if (text is "true" or "false")
        {
            return EncodedValue.Boolean(text == "true");
        }
        var magnitude = text.TrimStart('-', '+');
        var hex = magnitude.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var suffix = char.ToLowerInvariant(text[^1]);
        var floating = hex ? magnitude.Contains('p', StringComparison.OrdinalIgnoreCase) || magnitude.Contains('.')
            : suffix is 'f' or 'd' || magnitude.Contains('.') || magnitude.Contains('e', StringComparison.OrdinalIgnoreCase)
              || magnitude.StartsWith("NaN", StringComparison.OrdinalIgnoreCase)
              || magnitude.StartsWith("Infinity", StringComparison.OrdinalIgnoreCase);
        if (floating)
        {
            var single = suffix == 'f';
            var body = suffix is 'f' or 'd' ? text[..^1] : text;
            if (hex)
            {
                var bits = HexFloat(body, single);
                return single ? EncodedValue.Float(BitConverter.Int32BitsToSingle(unchecked((int)bits)))
                    : EncodedValue.Double(BitConverter.Int64BitsToDouble(unchecked((long)bits)));
            }
            if (body.Equals("NaN", StringComparison.OrdinalIgnoreCase))
            {
                return single ? EncodedValue.Float(BitConverter.Int32BitsToSingle(0x7fc00000))
                    : EncodedValue.Double(BitConverter.Int64BitsToDouble(0x7ff8000000000000));
            }
            if (single && float.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            {
                return EncodedValue.Float(f);
            }
            if (!single && double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return EncodedValue.Double(d);
            }
            throw new FormatException("Invalid floating-point literal.");
        }
        var width = suffix switch { 't' => 8, 's' => 16, 'l' => 64, _ => 32 };
        var integral = suffix is 't' or 's' or 'l' ? text[..^1] : text;
        var value = Integer(integral, width);
        return width switch
        {
            8 => EncodedValue.Byte(unchecked((byte)value)),
            16 => EncodedValue.Short(unchecked((short)value)),
            64 => EncodedValue.Long(value),
            _ => EncodedValue.Int(unchecked((int)value))
        };
    }

    internal static long Integral(string text)
    {
        var value = Scalar(text);
        if (value.Type is not (EncodedValueType.Byte or EncodedValueType.Short or EncodedValueType.Int or EncodedValueType.Long))
        {
            throw new FormatException("An integral literal is required.");
        }
        return Bits(value);
    }

    internal static long Fixed(string text, int width)
    {
        var value = Scalar(text);
        if (width == 16 && value.Type is not (EncodedValueType.Byte or EncodedValueType.Short
            or EncodedValueType.Char or EncodedValueType.Int or EncodedValueType.Long)
            || width == 32 && value.Type == EncodedValueType.Double)
        {
            throw new FormatException("The literal kind is not supported by this instruction.");
        }
        var bits = Bits(value);
        if (width < 64 && (bits < -(1L << (width - 1)) || bits >= 1L << width))
        {
            throw new FormatException($"The literal does not fit {width} bits.");
        }
        return width switch { 16 => unchecked((short)bits), 32 => unchecked((int)bits), _ => bits };
    }

    internal static long Bits(EncodedValue value) => value.Type switch
    {
        EncodedValueType.Byte => unchecked((sbyte)(byte)value.Value!),
        EncodedValueType.Short => (short)value.Value!,
        EncodedValueType.Char => (char)value.Value!,
        EncodedValueType.Int => (int)value.Value!,
        EncodedValueType.Long => (long)value.Value!,
        EncodedValueType.Float => BitConverter.SingleToInt32Bits((float)value.Value!),
        EncodedValueType.Double => BitConverter.DoubleToInt64Bits((double)value.Value!),
        EncodedValueType.Boolean => (bool)value.Value! ? 1 : 0,
        _ => throw new FormatException("A numeric scalar literal is required.")
    };

    private static long Integer(string text, int width)
    {
        var negative = text.StartsWith('-');
        var digits = text.AsSpan();
        if (digits.Length > 0 && digits[0] is '-' or '+')
        {
            digits = digits[1..];
        }
        var radix = 10;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            radix = 16;
            digits = digits[2..];
        }
        else if (digits.Length > 1 && digits[0] == '0')
        {
            radix = 8;
        }
        if (digits.Length == 0)
        {
            throw new FormatException("An integral literal requires digits.");
        }
        var limit = negative ? 1ul << (width - 1) : width == 64 ? ulong.MaxValue : (1ul << width) - 1;
        var result = 0ul;
        foreach (var c in digits)
        {
            var digit = HexDigit(c);
            if (digit < 0 || digit >= radix || result > (limit - (uint)digit) / (uint)radix)
            {
                throw new FormatException("An integral literal contains invalid digits or exceeds its declared width.");
            }
            result = result * (uint)radix + (uint)digit;
        }
        return negative ? unchecked(-(long)result) : width switch
        {
            8 => unchecked((sbyte)result),
            16 => unchecked((short)result),
            32 => unchecked((int)result),
            _ => unchecked((long)result)
        };
    }

    /// <summary>
    /// Rounds a hexadecimal mantissa using a bounded 64-bit prefix and sticky discarded bits.
    /// <para>使用有界的 64 位尾数前缀及丢弃位标记，对十六进制尾数进行舍入。</para>
    /// </summary>
    private static ulong HexFloat(string text, bool single)
    {
        var negative = text.StartsWith('-');
        var body = text.AsSpan();
        if (body.Length > 0 && body[0] is '-' or '+')
        {
            body = body[1..];
        }
        var exponentAt = body.IndexOfAny('p', 'P');
        if (exponentAt < 3 || !body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("A hexadecimal floating-point literal requires a binary exponent.");
        }
        var exponent = Exponent(body[(exponentAt + 1)..]);
        var mantissa = 0ul;
        var kept = 0;
        var dropped = 0;
        var fractional = 0;
        var point = false;
        var digitCount = 0;
        var sticky = false;
        foreach (var c in body[2..exponentAt])
        {
            if (c == '.' && !point)
            {
                point = true;
                continue;
            }
            var digit = HexDigit(c);
            if (digit < 0)
            {
                throw new FormatException("Invalid hexadecimal floating-point mantissa.");
            }
            digitCount++;
            if (point)
            {
                fractional++;
            }
            if (mantissa == 0 && digit == 0)
            {
                continue;
            }
            if (kept < 16)
            {
                mantissa = (mantissa << 4) | (uint)digit;
                kept++;
            }
            else
            {
                dropped++;
                sticky |= digit != 0;
            }
        }
        if (digitCount == 0)
        {
            throw new FormatException("A hexadecimal mantissa requires digits.");
        }
        var sign = negative ? 1ul << (single ? 31 : 63) : 0;
        if (mantissa == 0)
        {
            return sign;
        }
        var fractionBits = single ? 23 : 52;
        var minimum = single ? -126 : -1022;
        var maximum = single ? 127 : 1023;
        var adjustment = exponent - 4L * fractional + 4L * dropped;
        var length = BitOperations.Log2(mantissa) + 1;
        var binaryExponent = adjustment + length - 1;
        if (binaryExponent < minimum)
        {
            return sign | Round(mantissa, minimum - fractionBits - adjustment, sticky);
        }
        var significand = Round(mantissa, length - fractionBits - 1, sticky);
        if (significand == 1ul << (fractionBits + 1))
        {
            significand >>= 1;
            binaryExponent++;
        }
        if (binaryExponent > maximum)
        {
            return sign | (single ? 0x7f800000ul : 0x7ff0000000000000ul);
        }
        return sign | ((ulong)(binaryExponent + maximum) << fractionBits) | (significand & ((1ul << fractionBits) - 1));
    }

    private static ulong Round(ulong value, long shift, bool sticky)
    {
        if (shift <= 0)
        {
            return value << (int)-shift;
        }
        if (shift > 64)
        {
            return 0;
        }
        var quotient = shift == 64 ? 0 : value >> (int)shift;
        var remainder = shift == 64 ? value : value & ((1ul << (int)shift) - 1);
        var half = 1ul << ((int)shift - 1);
        return quotient + (remainder > half || remainder == half && (sticky || (quotient & 1) != 0) ? 1ul : 0);
    }

    private static long Exponent(ReadOnlySpan<char> text)
    {
        var negative = text.Length > 0 && text[0] == '-';
        if (text.Length > 0 && text[0] is '-' or '+')
        {
            text = text[1..];
        }
        if (text.Length == 0)
        {
            throw new FormatException("A binary exponent requires digits.");
        }
        var value = 0L;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                throw new FormatException("Invalid binary exponent.");
            }
            value = Math.Min(1L << 40, value * 10 + c - '0');
        }
        return negative ? -value : value;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}