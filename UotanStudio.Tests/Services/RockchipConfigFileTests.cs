using System.Buffers.Binary;
using System.Text;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RockchipConfigFileTests
{
    private static byte[] BuildSample()
    {
        const int record = RockchipConfigFile.RecordLength;
        var image = new byte[RockchipConfigFile.HeaderLength + (2 * record)];
        "CFG"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), RockchipConfigFile.HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6), 2026);
        WriteRecord(image.AsSpan(RockchipConfigFile.HeaderLength, record), "Loader",
            @"C:\Users\rock\Desktop\rk3588_spl_loader_v1.08.111.bin", 0, lastWord: 1);
        WriteRecord(image.AsSpan(RockchipConfigFile.HeaderLength + record, record), "Image",
            @"C:\Users\rock\Desktop\rock-5b-spi-image-g49da44e116d.img", 0x20, lastWord: 1);
        return image;
    }

    private static void WriteRecord(Span<byte> record, string name, string path, ulong address, uint lastWord)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record, RockchipConfigFile.RecordMarker);
        Encoding.Unicode.GetBytes(name, record.Slice(2, RockchipConfigFile.NameFieldLength));
        Encoding.Unicode.GetBytes(path,
            record.Slice(2 + RockchipConfigFile.NameFieldLength, RockchipConfigFile.PathFieldLength));
        var trailing = record.Slice(2 + RockchipConfigFile.NameFieldLength + RockchipConfigFile.PathFieldLength,
            RockchipConfigFile.TrailingLength);
        BinaryPrimitives.WriteUInt64LittleEndian(trailing, address);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing[12..], lastWord);
    }

    [Fact]
    public void Parse_VendorLayout_RestoresBothRows()
    {
        var parsed = RockchipConfigFile.Parse(BuildSample());

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("Loader", parsed.Entries[0].Name);
        Assert.Equal(@"C:\Users\rock\Desktop\rk3588_spl_loader_v1.08.111.bin", parsed.Entries[0].Path);
        Assert.Equal("Image", parsed.Entries[1].Name);
        Assert.Equal(0x20UL, parsed.Entries[1].Address);
        Assert.Equal(2026, BinaryPrimitives.ReadUInt16LittleEndian(parsed.Header.AsSpan(6)));
    }

    [Fact]
    public void Serialize_ParsedSample_ReProducesOriginalBytes()
    {
        var sample = BuildSample();
        var (header, entries) = RockchipConfigFile.Parse(sample);

        var rebuilt = RockchipConfigFile.Serialize(header, entries);

        Assert.Equal(sample, rebuilt);
    }

    [Fact]
    public void Serialize_NewEntry_KeepsTrailingBlockFromSource()
    {
        var source = new RockchipConfigEntry
        {
            Name = "Loader",
            Path = "loader.bin",
            Address = 0x40,
            Trailing = [0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14]
        };

        var (_, entries) = RockchipConfigFile.Parse(
            RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(), [source]));
        var rebuilt = entries[0];

        Assert.Equal(0x40UL, rebuilt.Address);
        Assert.Equal(source.Trailing[8..], rebuilt.Trailing[8..]);
    }

    [Fact]
    public void IsConfigHeader_VendorSample_True()
    {
        Assert.True(RockchipConfigFile.IsConfigHeader(BuildSample()));
    }

    [Fact]
    public void IsConfigHeader_PlainTextConfig_False()
    {
        var text = Encoding.UTF8.GetBytes("[System]\nMSC_TIMEOUT=30\n");

        Assert.False(RockchipConfigFile.IsConfigHeader(text));
        Assert.False(RockchipConfigFile.IsConfigHeader([]));
    }

    [Fact]
    public void Parse_WithoutMagic_Throws()
    {
        var image = BuildSample();
        "BAD"u8.CopyTo(image);

        Assert.Throws<InvalidDataException>(() => RockchipConfigFile.Parse(image));
    }

    [Fact]
    public void Parse_TruncatedPayload_Throws()
    {
        var image = BuildSample()[..^1];

        Assert.Throws<InvalidDataException>(() => RockchipConfigFile.Parse(image));
    }

    [Fact]
    public void LooksLike_ConfigFileOnDisk_True()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cfg");
        try
        {
            File.WriteAllBytes(path, BuildSample());

            Assert.True(RockchipConfigFile.LooksLike(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LooksLike_PlainTextFile_False()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cfg");
        try
        {
            File.WriteAllText(path, "[System]\nMSC_TIMEOUT=30\n");

            Assert.False(RockchipConfigFile.LooksLike(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LooksLike_TruncatedHeader_False()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cfg");
        try
        {
            File.WriteAllBytes(path, BuildSample()[..12]);

            Assert.False(RockchipConfigFile.LooksLike(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LooksLike_MissingFile_False()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cfg");

        Assert.False(RockchipConfigFile.LooksLike(path));
    }
}