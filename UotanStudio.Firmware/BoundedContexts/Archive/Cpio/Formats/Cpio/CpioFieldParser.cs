using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Cpio;

internal static class CpioFieldParser
{
    private const int FileTypeMask = 0xF000;

    public static string ReadAsciiString(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data);

    public static uint ParseHex(ReadOnlySpan<byte> data)
    {
        var text = ReadAsciiString(data);
        return uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static ulong ParseHex64(ReadOnlySpan<byte> data)
    {
        var text = ReadAsciiString(data);
        return ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static uint ParseOctal(ReadOnlySpan<byte> data)
    {
        var text = ReadAsciiString(data).TrimEnd('\0', ' ');
        return text.Length == 0 ? 0U : Convert.ToUInt32(text, 8);
    }

    public static long ParseOctal64(ReadOnlySpan<byte> data)
    {
        var text = ReadAsciiString(data).TrimEnd('\0', ' ');
        return text.Length == 0 ? 0L : Convert.ToInt64(text, 8);
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