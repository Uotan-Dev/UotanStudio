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
        RockchipConfigFile.CreateHeader().CopyTo(image, 0);
        image[RockchipConfigFile.HeaderRowCountOffset] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6), 2026);
        WriteRecord(image.AsSpan(RockchipConfigFile.HeaderLength, record), "Loader",
            @"C:\Users\rock\Desktop\rk3588_spl_loader_v1.08.111.bin", leadWord: 0, storageWord: 0, address: 0,
            selected: 1);
        WriteRecord(image.AsSpan(RockchipConfigFile.HeaderLength + record, record), "Image",
            @"C:\Users\rock\Desktop\rock-5b-spi-image-g49da44e116d.img", leadWord: 0x53000000,
            storageWord: 1u << RockchipConfigFile.StorageIdSpiNor, address: 0, selected: 1);
        return image;
    }

    private static void WriteRecord(Span<byte> record, string name, string path, uint leadWord,
        uint storageWord, uint address, uint selected)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record, RockchipConfigFile.RecordMarker);
        Encoding.Unicode.GetBytes(name, record.Slice(2, RockchipConfigFile.NameFieldLength));
        Encoding.Unicode.GetBytes(path,
            record.Slice(2 + RockchipConfigFile.NameFieldLength, RockchipConfigFile.PathFieldLength));
        var trailing = record.Slice(2 + RockchipConfigFile.NameFieldLength + RockchipConfigFile.PathFieldLength,
            RockchipConfigFile.TrailingLength);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing, leadWord);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing[RockchipConfigFile.StorageOffset..], storageWord);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing[RockchipConfigFile.AddressOffset..], address);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing[RockchipConfigFile.SelectedOffset..], selected);
    }

    private static byte[] BuildRecordWithTrailing(string name, string path, ReadOnlySpan<byte> trailing)
    {
        var image = new byte[RockchipConfigFile.HeaderLength + RockchipConfigFile.RecordLength];
        RockchipConfigFile.CreateHeader().CopyTo(image, 0);
        image[RockchipConfigFile.HeaderRowCountOffset] = 1;
        var record = image.AsSpan(RockchipConfigFile.HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record, RockchipConfigFile.RecordMarker);
        Encoding.Unicode.GetBytes(name, record.Slice(2, RockchipConfigFile.NameFieldLength));
        Encoding.Unicode.GetBytes(path,
            record.Slice(2 + RockchipConfigFile.NameFieldLength, RockchipConfigFile.PathFieldLength));
        trailing.CopyTo(record.Slice(
            2 + RockchipConfigFile.NameFieldLength + RockchipConfigFile.PathFieldLength,
            RockchipConfigFile.TrailingLength));
        return image;
    }

    [Fact]
    public void Parse_VendorLayout_RestoresBothRows()
    {
        var parsed = RockchipConfigFile.Parse(BuildSample());

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("Loader", parsed.Entries[0].Name);
        Assert.Equal(@"C:\Users\rock\Desktop\rk3588_spl_loader_v1.08.111.bin", parsed.Entries[0].Path);
        Assert.Equal("Image", parsed.Entries[1].Name);
        Assert.Equal(0u, parsed.Entries[1].Address);
        Assert.Equal(0u, parsed.Entries[0].StorageMask);
        Assert.Equal(1u << RockchipConfigFile.StorageIdSpiNor, parsed.Entries[1].StorageMask);
        Assert.Equal(string.Empty, RockchipConfigFile.DescribeStorage(parsed.Entries[0].StorageMask));
        Assert.Equal("SPINOR", RockchipConfigFile.DescribeStorage(parsed.Entries[1].StorageMask));
        Assert.True(parsed.Entries[0].Selected);
        Assert.True(parsed.Entries[1].Selected);
        Assert.Equal(2026, BinaryPrimitives.ReadUInt16LittleEndian(parsed.Header.AsSpan(6)));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }, 0u)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x53, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }, 1u << RockchipConfigFile.StorageIdEmmc)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }, 1u << RockchipConfigFile.StorageIdSpiNor)]
    public void Parse_VendorTrailingBlock_KeepsTheStorageBit(byte[] trailing, uint expected)
    {
        var parsed = RockchipConfigFile.Parse(BuildRecordWithTrailing("Image", @"C:\img\system.img", trailing));

        Assert.Equal(expected, parsed.Entries[0].StorageMask);
    }

    [Fact]
    public void Parse_MultiBitStorageWord_IsNotReadAsTheLowestBit()
    {
        var parsed = RockchipConfigFile.Parse(BuildRecordWithTrailing("Image", "linux_4_19.img",
            [0x00, 0x00, 0x40, 0xE9, 0xDA, 0x00, 0x87, 0x77, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]));

        Assert.Equal(0x778700DAu, parsed.Entries[0].StorageWord);
        Assert.Equal(0u, parsed.Entries[0].StorageMask);
        Assert.Equal(string.Empty, RockchipConfigFile.DescribeStorage(parsed.Entries[0].StorageMask));
    }

    [Fact]
    public void StorageIdConstants_MatchTheProtocolHelpers()
    {
        Assert.Equal(1, RockchipConfigFile.StorageIdEmmc);
        Assert.Equal(2, RockchipConfigFile.StorageIdSd);
        Assert.Equal(9, RockchipConfigFile.StorageIdSpiNor);
    }

    [Fact]
    public void Parse_UninitializedStorageWord_ReportsNoStorage()
    {
        var parsed = RockchipConfigFile.Parse(BuildRecordWithTrailing("Uboot", @"D:\rk3568\images\uboot.img",
            [0x00, 0x00, 0x80, 0xEF, 0xAF, 0x00, 0x87, 0x77, 0x00, 0x20, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]));

        var entry = parsed.Entries[0];
        Assert.Equal(0u, entry.StorageMask);
        Assert.Equal(string.Empty, RockchipConfigFile.DescribeStorage(entry.StorageMask));
        Assert.Equal(0x2000u, entry.Address);
        Assert.True(entry.Selected);
    }

    [Fact]
    public void Serialize_UneadableStorageWord_RoundTripsUnchanged()
    {
        var sample = BuildRecordWithTrailing("Uboot", @"D:\rk3568\images\uboot.img",
            [0x00, 0x00, 0x80, 0xEF, 0xAF, 0x00, 0x87, 0x77, 0x00, 0x20, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);
        var (header, entries) = RockchipConfigFile.Parse(sample);

        var rebuilt = RockchipConfigFile.Serialize(header, entries);

        Assert.Equal(0x778700AFu, entries[0].StorageWord);
        Assert.Equal(0u, entries[0].StorageMask);
        Assert.Equal(sample, rebuilt);
    }

    [Fact]
    public void Serialize_ChangedStorage_ReplacesTheUnreadableWord()
    {
        var source = new RockchipConfigEntry
        {
            Name = "Uboot",
            Path = "uboot.img",
            StorageWord = 0x778700AFu,
            StorageMask = 1u << RockchipConfigFile.StorageIdEmmc
        };

        var image = RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(), [source]);
        var (_, entries) = RockchipConfigFile.Parse(image);

        Assert.Equal(1u << RockchipConfigFile.StorageIdEmmc, entries[0].StorageWord);
        Assert.Equal(1u << RockchipConfigFile.StorageIdEmmc, entries[0].StorageMask);
    }

    [Fact]
    public void Parse_StorageWordWithoutMarker_ReportsNoStorage()
    {
        var parsed = RockchipConfigFile.Parse(BuildRecordWithTrailing("Image", "image.img",
            [0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]));

        Assert.Equal(2u, parsed.Entries[0].StorageWord);
        Assert.Equal(0u, parsed.Entries[0].StorageMask);
    }

    [Fact]
    public void Serialize_StorageRow_WritesTheMarkerAndTheWord()
    {
        var image = RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(),
        [
            new RockchipConfigEntry
            {
                Name = "Image",
                Path = "image.img",
                StorageMask = 1u << RockchipConfigFile.StorageIdEmmc,
                Selected = true
            }
        ]);

        var record = image.AsSpan(RockchipConfigFile.HeaderLength);
        var tail = record[^RockchipConfigFile.TrailingLength..];
        Assert.Equal(RockchipConfigFile.StorageMarker, tail[RockchipConfigFile.StorageMarkerOffset]);
        Assert.Equal(1u << RockchipConfigFile.StorageIdEmmc,
            BinaryPrimitives.ReadUInt32LittleEndian(tail[RockchipConfigFile.StorageOffset..]));

        var reparsed = RockchipConfigFile.Parse(image);
        Assert.Equal(1u << RockchipConfigFile.StorageIdEmmc, reparsed.Entries[0].StorageMask);
        Assert.Equal("EMMC", RockchipConfigFile.DescribeStorage(reparsed.Entries[0].StorageMask));
    }

    [Fact]
    public void Serialize_ClearedStorage_RemovesTheMarkerAndTheWord()
    {
        var source = RockchipConfigFile.Parse(BuildRecordWithTrailing("Image", "image.img",
            [0x00, 0x00, 0x00, 0x53, 0x02, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]));
        Assert.Equal(1u << RockchipConfigFile.StorageIdEmmc, source.Entries[0].StorageMask);

        var rebuilt = RockchipConfigFile.Serialize(source.Header,
            [source.Entries[0] with { StorageMask = 0 }]);
        var tail = rebuilt.AsSpan(RockchipConfigFile.HeaderLength)[^RockchipConfigFile.TrailingLength..];

        Assert.Equal(0, tail[RockchipConfigFile.StorageMarkerOffset]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(tail[RockchipConfigFile.StorageOffset..]));
    }

    [Fact]
    public void Parse_MarkerWithSeveralBits_ReportsNoStorage()
    {
        var parsed = RockchipConfigFile.Parse(BuildRecordWithTrailing("Image", "image.img",
            [0x00, 0x00, 0x00, 0x53, 0xAF, 0x00, 0x87, 0x77, 0x00, 0x20, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]));

        Assert.Equal(0x778700AFu, parsed.Entries[0].StorageWord);
        Assert.Equal(0u, parsed.Entries[0].StorageMask);
    }

    [Fact]
    public void Parse_RowWithoutImage_ReportsUnselected()
    {
        var parsed = RockchipConfigFile.Parse(BuildRecordWithTrailing("misc", string.Empty,
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));

        Assert.Equal(0x4000u, parsed.Entries[0].Address);
        Assert.False(parsed.Entries[0].Selected);
        Assert.Equal(0u, parsed.Entries[0].StorageMask);
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
            StorageMask = 1u << RockchipConfigFile.StorageIdEmmc,
            Selected = true,
            Trailing = [0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14]
        };

        var (_, entries) = RockchipConfigFile.Parse(
            RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(), [source]));
        var rebuilt = entries[0];

        Assert.Equal(0x40u, rebuilt.Address);
        Assert.Equal(1u << RockchipConfigFile.StorageIdEmmc, rebuilt.StorageMask);
        Assert.True(rebuilt.Selected);
        Assert.Equal(source.Trailing[..RockchipConfigFile.StorageMarkerOffset],
            rebuilt.Trailing[..RockchipConfigFile.StorageMarkerOffset]);
        Assert.Equal(RockchipConfigFile.StorageMarker,
            rebuilt.Trailing[RockchipConfigFile.StorageMarkerOffset]);
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

    [Theory]
    [InlineData("EMMC", 2u)]
    [InlineData("emmc", 2u)]
    [InlineData("SD", 4u)]
    [InlineData("SPINOR", 512u)]
    [InlineData(" spinor ", 512u)]
    [InlineData("UFS", 0u)]
    [InlineData("", 0u)]
    public void StorageMaskFromName_VendorMediumNames_ReturnsTheProtocolBit(string name, uint expected)
        => Assert.Equal(expected, RockchipConfigFile.StorageMaskFromName(name));

    [Theory]
    [InlineData(2u, "EMMC")]
    [InlineData(4u, "SD")]
    [InlineData(512u, "SPINOR")]
    [InlineData(0u, "")]
    [InlineData(514u, "")]
    [InlineData(0x778700AFu, "")]
    [InlineData(0xEF800000u, "")]
    public void DescribeStorage_Mask_NamesKnownBitsAndLeavesTheRestBlank(uint mask, string expected)
        => Assert.Equal(expected, RockchipConfigFile.DescribeStorage(mask));

    [Theory]
    [InlineData("Loader", RockchipConfigOperation.Loader)]
    [InlineData("loader", RockchipConfigOperation.Loader)]
    [InlineData(" Loader ", RockchipConfigOperation.Loader)]
    [InlineData("Parameter", RockchipConfigOperation.Parameter)]
    [InlineData(" parameter ", RockchipConfigOperation.Parameter)]
    [InlineData("Image", RockchipConfigOperation.Flash)]
    [InlineData("loader2", RockchipConfigOperation.Flash)]
    [InlineData("", RockchipConfigOperation.Flash)]
    [InlineData(null, RockchipConfigOperation.Flash)]
    public void ClassifyOperation_RowName_RecognizesLoaderAndParameter(string? name,
        RockchipConfigOperation expected)
        => Assert.Equal(expected, RockchipConfigFile.ClassifyOperation(name));

    [Fact]
    public void Serialize_FiveRows_WritesRowCountRepeatedLengthAndHeaderMarker()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(index => new RockchipConfigEntry { Name = $"p{index}", Path = $@"C:imagesp{index}.img" })
            .ToArray();

        var image = RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(), entries);

        Assert.Equal(5, image[RockchipConfigFile.HeaderRowCountOffset]);
        Assert.Equal((uint)RockchipConfigFile.HeaderLength,
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(RockchipConfigFile.HeaderLengthWordOffset)));
        Assert.Equal(RockchipConfigFile.RecordMarker,
            BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(RockchipConfigFile.HeaderRecordMarkerOffset)));
        Assert.Equal(RockchipConfigFile.HeaderLength + (5 * RockchipConfigFile.RecordLength), image.Length);
    }

    [Fact]
    public void CreateHeader_NoRows_CarriesLengthFieldsAndMarker()
    {
        var header = RockchipConfigFile.CreateHeader();

        Assert.Equal(RockchipConfigFile.HeaderLength, header.Length);
        Assert.Equal((ushort)RockchipConfigFile.HeaderLength, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)));
        Assert.Equal((uint)RockchipConfigFile.HeaderLength,
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(RockchipConfigFile.HeaderLengthWordOffset)));
        Assert.Equal(RockchipConfigFile.RecordMarker,
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(RockchipConfigFile.HeaderRecordMarkerOffset)));
        Assert.Equal(0, header[RockchipConfigFile.HeaderRowCountOffset]);
    }

    [Fact]
    public void Serialize_MoreRowsThanTheCountByteHolds_Throws()
    {
        var entries = Enumerable.Range(0, 256)
            .Select(index => new RockchipConfigEntry { Name = $"p{index}" })
            .ToArray();

        Assert.Throws<ArgumentException>(() => RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(), entries));
    }

    [Fact]
    public void Serialize_PathLongerThanItsField_Throws()
    {
        var entries = new[] { new RockchipConfigEntry { Name = "boot", Path = new string('p', 256) } };

        Assert.Throws<ArgumentException>(() => RockchipConfigFile.Serialize(RockchipConfigFile.CreateHeader(), entries));
    }
}