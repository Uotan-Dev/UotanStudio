using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Formats.Cpio;
using UotanStudio.Firmware.Archive.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// The CPIO <c>namesc</c> field is dialect-specific in width: two big-endian bytes in the binary dialects,
/// six octal digits in the old ASCII dialects and eight hexadecimal digits in newc and crc. It counts the
/// NUL terminator, so a name that does not fit must fail before a single record byte is emitted instead of
/// aliasing the field, because a header whose declared record size differs from the written bytes
/// desynchronises every following record. The measurement is in UTF-8 bytes, not in UTF-16 characters, so a
/// CJK name costs three bytes per character and still passes unchanged while it fits, and the widest field
/// keeps accepting names the 16-bit dialects must reject.
/// <para>CPIO 的 <c>namesc</c> 字段宽度随方言而定：binary 方言为两个大端字节，old ASCII 方言为六位八进制，
/// newc 与 crc 为八位十六进制。该字段会计入 NUL 终止符，因此装不下的名称必须在写出任何记录字节之前失败，
/// 而不是让字段回绕，因为头部宣告的记录长度与实际写出的字节不一致时，之后的全部记录都会错位。
/// 计量单位为 UTF-8 字节而非 UTF-16 字符，所以中文名称每字符占三字节，只要仍在上限内就原样通过；
/// 最宽的字段也继续接受 16 位方言必须拒绝的名称。</para>
/// </summary>
public sealed class CpioNameSizeTests
{
    private const int BinaryHeaderSize = 26;
    private const int OldAsciiHeaderSize = 76;
    private const int NewAsciiHeaderSize = 110;
    private const int BinaryNameLimitBytes = 65_534;
    private const int OldAsciiNameLimitBytes = 262_142;

    [Theory]
    [InlineData(ArchiveFormat.Binary, 65_535)]
    [InlineData(ArchiveFormat.Binary, 65_536)]
    [InlineData(ArchiveFormat.HpBinary, 65_535)]
    public void WriteAll_BinaryNameBeyondSixteenBitNamesc_ThrowsBeforeWritingAnyBytes(ArchiveFormat format, int nameByteLength)
    {
        var name = new string('a', nameByteLength);
        using var output = new MemoryStream();

        var error = Assert.Throws<ArchiveUnsupportedFeatureException>(
            () => new CpioArchiveWriter().WriteAll(output, [RegularEntry(name, [])], format));

        Assert.Contains($"CPIO {format} entry name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains($"{nameByteLength} UTF-8 bytes exceed the {BinaryNameLimitBytes}-byte name limit of the namesc field",
            error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(ArchiveFormat.Binary)]
    [InlineData(ArchiveFormat.HpBinary)]
    public void WriteAll_BinaryNameAtSixteenBitNamescLimit_WritesUnclippedRecord(ArchiveFormat format)
    {
        var name = new string('a', BinaryNameLimitBytes);

        var packed = Write(format, [RegularEntry(name, [])]);

        Assert.Equal(BinaryNameLimitBytes + 1, ReadNamescField(packed, format));
        Assert.Equal(Encoding.UTF8.GetBytes(name), packed[BinaryHeaderSize..(BinaryHeaderSize + BinaryNameLimitBytes)]);
        Assert.Equal((byte)0, packed[BinaryHeaderSize + BinaryNameLimitBytes]);

        var unpacked = Read(packed, format);
        Assert.Equal(name, unpacked.Single(entry => entry.Name == name).Name);
        Assert.Equal(packed, Write(format, unpacked));
    }

    [Theory]
    [InlineData(ArchiveFormat.OldAscii)]
    [InlineData(ArchiveFormat.HpOldAscii)]
    public void WriteAll_OldAsciiNameBeyondSixOctalDigitNamesc_ThrowsBeforeWritingAnyBytes(ArchiveFormat format)
    {
        var name = new string('文', 87_381);
        var nameByteLength = Encoding.UTF8.GetByteCount(name);
        Assert.Equal(262_143, nameByteLength);
        Assert.Equal(87_381, name.Length);
        Assert.NotEqual(nameByteLength, name.Length);
        using var output = new MemoryStream();

        var error = Assert.Throws<ArchiveUnsupportedFeatureException>(
            () => new CpioArchiveWriter().WriteAll(output, [RegularEntry(name, [])], format));

        Assert.Contains($"CPIO {format} entry name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains($"{nameByteLength} UTF-8 bytes exceed the {OldAsciiNameLimitBytes}-byte name limit of the namesc field",
            error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(ArchiveFormat.NewAscii)]
    [InlineData(ArchiveFormat.CrcAscii)]
    [InlineData(ArchiveFormat.OldAscii)]
    [InlineData(ArchiveFormat.HpOldAscii)]
    [InlineData(ArchiveFormat.Binary)]
    [InlineData(ArchiveFormat.HpBinary)]
    public void WriteAll_CjkNameWithinNamescField_WritesEveryUtf8ByteAndMatchingField(ArchiveFormat format)
    {
        var name = "系统/lib 配置(中文).rc";
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var headerSize = HeaderSize(format);

        var packed = Write(format, [RegularEntry(name, payload)]);

        Assert.True(nameBytes.Length > name.Length);
        Assert.Equal(nameBytes, packed[headerSize..(headerSize + nameBytes.Length)]);
        Assert.Equal((byte)0, packed[headerSize + nameBytes.Length]);
        Assert.Equal(nameBytes.Length + 1, ReadNamescField(packed, format));

        var unpacked = Read(packed, format);
        var roundTripped = unpacked.Single(entry => entry.Name == name);
        Assert.Equal(name, roundTripped.Name);
        Assert.Equal(payload, roundTripped.Data);
        Assert.Equal(packed, Write(format, unpacked));
    }

    [Fact]
    public void WriteAll_NewAsciiNameBeyondSixteenBits_WritesFullThirtyTwoBitNamescField()
    {
        var name = new string('a', 65_532) + "文";
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Assert.Equal(65_535, nameBytes.Length);
        Assert.Equal(65_533, name.Length);

        var packed = Write(ArchiveFormat.NewAscii, [RegularEntry(name, [])]);

        Assert.Equal("00010000", Encoding.ASCII.GetString(packed, 94, 8));
        Assert.Equal(65_536, ReadNamescField(packed, ArchiveFormat.NewAscii));
        Assert.Equal(nameBytes, packed[NewAsciiHeaderSize..(NewAsciiHeaderSize + nameBytes.Length)]);
        var unpacked = Read(packed, ArchiveFormat.NewAscii);
        Assert.Equal(name, unpacked.Single(entry => entry.Name == name).Name);
        Assert.Equal(packed, Write(ArchiveFormat.NewAscii, unpacked));

        var binaryError = Assert.Throws<ArchiveUnsupportedFeatureException>(
            () => Write(ArchiveFormat.Binary, [RegularEntry(name, [])]));
        Assert.Contains("CPIO Binary entry name is not representable", binaryError.Message, StringComparison.Ordinal);
        Assert.Contains("65535 UTF-8 bytes exceed the 65534-byte name limit of the namesc field",
            binaryError.Message, StringComparison.Ordinal);
    }

    private static ArchiveEntry RegularEntry(string name, byte[] data) => new()
    {
        Name = name,
        Data = data,
        Metadata = new ArchiveEntryMetadata
        {
            FileType = CpioFileType.Regular,
            Mode = 0x81A4,
            UserId = 1000,
            GroupId = 1000,
            Inode = 17,
            LinkCount = 1,
            ModificationTimeUnixSeconds = 1_700_000_000,
            DeviceMajor = 3,
        },
    };

    private static byte[] Write(ArchiveFormat format, IEnumerable<ArchiveEntry> entries)
    {
        using var output = new MemoryStream();
        new CpioArchiveWriter().WriteAll(output, entries, format);
        return output.ToArray();
    }

    private static IReadOnlyList<ArchiveEntry> Read(byte[] packed, ArchiveFormat format)
    {
        using var input = new MemoryStream(packed, writable: false);
        return new CpioArchive().ReadAll(input, format);
    }

    private static int HeaderSize(ArchiveFormat format) => format switch
    {
        ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii => NewAsciiHeaderSize,
        ArchiveFormat.OldAscii or ArchiveFormat.HpOldAscii => OldAsciiHeaderSize,
        _ => BinaryHeaderSize,
    };

    private static int ReadNamescField(byte[] packed, ArchiveFormat format) => format switch
    {
        ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii => (int)Convert.ToUInt32(Encoding.ASCII.GetString(packed, 94, 8), 16),
        ArchiveFormat.OldAscii or ArchiveFormat.HpOldAscii => (int)Convert.ToUInt32(Encoding.ASCII.GetString(packed, 59, 6), 8),
        _ => BinaryPrimitives.ReadUInt16BigEndian(packed[20..22]),
    };
}