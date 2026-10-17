using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Archive.Models;

namespace UotanStudio.Firmware.Archive.Formats.Cpio;

internal static class CpioFieldParser
{
    private const int FileTypeMask = 0xF000;

    public static string ReadAsciiString(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data);

    public static uint ParseHex(ReadOnlySpan<byte> data)
    {
        var span = TrimAsciiWhiteSpace(data);
        if (span.IsEmpty)
        {
            throw new FormatException("The CPIO hexadecimal field is empty.");
        }

        uint value = 0;
        foreach (var current in span)
        {
            value = checked((value << 4) | ParseHexDigit(current));
        }

        return value;
    }

    public static ulong ParseHex64(ReadOnlySpan<byte> data)
    {
        var span = TrimAsciiWhiteSpace(data);
        if (span.IsEmpty)
        {
            throw new FormatException("The CPIO hexadecimal field is empty.");
        }

        ulong value = 0;
        foreach (var current in span)
        {
            value = checked((value << 4) | ParseHexDigit(current));
        }

        return value;
    }

    public static uint ParseOctal(ReadOnlySpan<byte> data)
    {
        var end = data.Length;
        while (end > 0 && data[end - 1] is 0 or (byte)' ')
        {
            end--;
        }

        uint value = 0;
        for (var i = 0; i < end; i++)
        {
            var digit = data[i];
            if (digit is < (byte)'0' or > (byte)'7')
            {
                throw new FormatException("The CPIO octal field contains a non-octal digit.");
            }

            value = checked((value * 8) + (uint)(digit - '0'));
        }

        return value;
    }

    public static long ParseOctal64(ReadOnlySpan<byte> data)
    {
        var end = data.Length;
        while (end > 0 && data[end - 1] is 0 or (byte)' ')
        {
            end--;
        }

        long value = 0;
        for (var i = 0; i < end; i++)
        {
            var digit = data[i];
            if (digit is < (byte)'0' or > (byte)'7')
            {
                throw new FormatException("The CPIO octal field contains a non-octal digit.");
            }

            value = checked((value * 8) + (digit - '0'));
        }

        return value;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> data)
    {
        var start = 0;
        var end = data.Length;
        while (start < end && IsAsciiWhiteSpace(data[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWhiteSpace(data[end - 1]))
        {
            end--;
        }

        return data[start..end];
    }

    private static bool IsAsciiWhiteSpace(byte value) => value is (byte)' ' or (>= 0x09 and <= 0x0D);

    private static uint ParseHexDigit(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            return (uint)(value - '0');
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            return (uint)(value - 'a' + 10);
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            return (uint)(value - 'A' + 10);
        }

        throw new FormatException("The CPIO hexadecimal field contains a non-hexadecimal digit.");
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> data, bool byteSwap)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(data);
        return byteSwap ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    public static uint ComposeUInt32(ushort high, ushort low) => ((uint)high << 16) | low;

    public static CpioFileType ToFileType(uint mode)
    {
        return (mode & FileTypeMask) switch
        {
            0x8000 => CpioFileType.Regular,
            0x4000 => CpioFileType.Directory,
            0xA000 => CpioFileType.SymbolicLink,
            0x2000 => CpioFileType.CharacterDevice,
            0x6000 => CpioFileType.BlockDevice,
            0x1000 => CpioFileType.Fifo,
            0xC000 => CpioFileType.Socket,
            _ => CpioFileType.Unknown,
        };
    }

    public static int ToPermissions(uint mode) => (int)(mode & 0x0FFF);

    public static uint ToMode(CpioFileType fileType, int permissions)
    {
        var typeBits = fileType switch
        {
            CpioFileType.Regular => 0x8000U,
            CpioFileType.Directory => 0x4000U,
            CpioFileType.SymbolicLink => 0xA000U,
            CpioFileType.CharacterDevice => 0x2000U,
            CpioFileType.BlockDevice => 0x6000U,
            CpioFileType.Fifo => 0x1000U,
            CpioFileType.Socket => 0xC000U,


            CpioFileType.HardLink => 0x8000U,
            CpioFileType.Unknown => throw new ArgumentException("Unknown CPIO file type.", nameof(fileType)),
            _ => 0x8000U,
        };

        return typeBits | ((uint)permissions & 0x0FFFU);
    }
}