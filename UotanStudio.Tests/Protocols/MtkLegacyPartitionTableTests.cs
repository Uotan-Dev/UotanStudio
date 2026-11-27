using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.MediaTek;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the MediaTek <c>brom_layout_header</c> field offsets against the vendor <c>mtk_image.h</c> definition
/// using the block a real MT6582 device returned at the head of its BOOT1 region, so a shifted parse cannot
/// silently turn a valid layout into an unrecognised one.
/// </summary>
public sealed class MtkLegacyPartitionTableTests
{
    private const uint EmmcMediaType = 0x0001_0005;

    private static byte[] CreateLayoutHeader(uint headerSize = 0x800, uint totalSize = 0x20800,
        uint mediaType = EmmcMediaType, uint version = 1)
    {
        var buffer = new byte[512];
        buffer.AsSpan().Fill(0xFF);
        MtkBromLayoutHeader.Name.CopyTo(buffer.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), MtkBromLayoutHeader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), mediaType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36), 0);
        return buffer;
    }

    [Fact]
    public void TryParse_DeviceBootRegionHeader_ResolvesHeaderSizeAtOffset12()
    {
        var prefix = CreateLayoutHeader();

        Assert.True(MtkBromLayoutHeader.TryParse(prefix, out var header));

        Assert.True(header.IsRecognized);
        Assert.Equal(0x800u, header.HeaderSize);
        Assert.Equal(0x20800u, header.TotalSize);
        Assert.Equal(EmmcMediaType, header.MediaType);
        Assert.Equal("EMMC", header.DescribeMediaType());
    }

    [Fact]
    public void TryParse_VersionWordBeforeHeaderSize_IsNotMistakenForTheHeaderSize()
    {
        var prefix = CreateLayoutHeader(version: 7);

        Assert.True(MtkBromLayoutHeader.TryParse(prefix, out var header));

        Assert.Equal(0x800u, header.HeaderSize);
        Assert.NotEqual(7u, header.HeaderSize);
    }

    [Fact]
    public void TryParse_MediaTypeWords_MapToVendorSymbolicNames()
    {
        var cases = new (uint Word, string Name)[]
        {
            (0x0001_0002, "NAND"),
            (0x0001_0005, "EMMC"),
            (0x0001_0007, "NOR"),
            (0x0001_0008, "SDMMC"),
            (0x0001_0009, "SNAND")
        };

        foreach (var (word, name) in cases)
        {
            Assert.True(MtkBromLayoutHeader.TryParse(CreateLayoutHeader(mediaType: word), out var header));
            Assert.Equal(name, header.DescribeMediaType());
        }
    }

    [Fact]
    public void TryParse_PrefixShorterThanTheStructure_IsRejected()
    {
        var prefix = CreateLayoutHeader()[..(MtkBromLayoutHeader.MinimumLength - 1)];

        Assert.False(MtkBromLayoutHeader.TryParse(prefix, out var header));
        Assert.False(header.IsRecognized);
        Assert.Same(MtkBromLayoutHeader.None, header);
    }

    [Fact]
    public void TryParse_ForeignName_IsRejected()
    {
        var prefix = CreateLayoutHeader();
        prefix[0] = (byte)'X';

        Assert.False(MtkBromLayoutHeader.TryParse(prefix, out var header));
        Assert.False(header.IsRecognized);
    }

    [Fact]
    public void TryParse_ForeignMagic_IsRejected()
    {
        var prefix = CreateLayoutHeader();
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(20), 0x1234_5678u);

        Assert.False(MtkBromLayoutHeader.TryParse(prefix, out var header));
        Assert.False(header.IsRecognized);
    }

    [Fact]
    public void TryParse_DuplicatedLengthsThatDisagree_AreRejected()
    {
        var prefix = CreateLayoutHeader();
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(28), 0x1000u);

        Assert.False(MtkBromLayoutHeader.TryParse(prefix, out var header));
        Assert.False(header.IsRecognized);
    }

    [Fact]
    public void TryParse_ZeroOrOversizedHeaderSize_IsRejected()
    {
        Assert.False(MtkBromLayoutHeader.TryParse(CreateLayoutHeader(headerSize: 0, totalSize: 0x20800), out _));
        Assert.False(MtkBromLayoutHeader.TryParse(
            CreateLayoutHeader(headerSize: MtkBromLayoutHeader.MaximumHeaderSize + 4, totalSize: 0x40000), out _));
    }

    [Fact]
    public void TryParse_TotalSizeBelowHeaderSize_IsRejected()
    {
        var prefix = CreateLayoutHeader(headerSize: 0x800, totalSize: 0x400);

        Assert.False(MtkBromLayoutHeader.TryParse(prefix, out var header));
        Assert.False(header.IsRecognized);
    }

    [Theory]
    [InlineData((byte)1, "BOOT1")]
    [InlineData((byte)2, "BOOT2")]
    [InlineData((byte)3, "RPMB")]
    [InlineData((byte)4, "GP1")]
    [InlineData((byte)5, "GP2")]
    [InlineData((byte)6, "GP3")]
    [InlineData((byte)7, "GP4")]
    [InlineData((byte)8, "USER")]
    public void Describe_DocumentedRegionByte_ReturnsVendorSymbol(byte partitionId, string expected)
    {
        Assert.Equal(expected, MtkLegacyEmmcRegion.Describe(partitionId));
    }

    [Fact]
    public void Describe_UndocumentedRegionByte_FallsBackToHex()
    {
        Assert.Equal("0x11", MtkLegacyEmmcRegion.Describe(0x11));
        Assert.Equal("0x00", MtkLegacyEmmcRegion.Describe(0));
    }

    private static MtkLegacyStorageInfo CreateStorage(long userBytes = 0x4000_0000)
    {
        var inventory = new byte[92];
        for (var index = 0; index < 7; index++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(inventory.AsSpan(4 + index * 8), 0x200_0000UL);
        }

        BinaryPrimitives.WriteUInt64BigEndian(inventory.AsSpan(60), (ulong)userBytes);
        return MtkLegacyStorageInfo.Parse(inventory);
    }

    private static GptTable CreateGpt(int populatedEntries)
    {
        var partitions = new List<GptPartition>(populatedEntries);
        for (var index = 0; index < populatedEntries; index++)
        {
            partitions.Add(new GptPartition(index, Guid.NewGuid(), Guid.NewGuid(),
                (ulong)(34 + index * 0x400), (ulong)(34 + index * 0x400 + 0x3FF), 0,
                "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                34L * 512 + index * 0x80000, 0x80000));
        }

        return new GptTable(Guid.NewGuid(), 512, 0x800_0000, partitions, new string('0', 64),
            92, 34, 0x7FFF_FFDE, 128, 128, 2, 0x7FFF_FFFF);
    }

    [Fact]
    public void PartitionCount_GptSnapshot_CountsPopulatedEntries()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0x800,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, CreateGpt(23), null);

        Assert.Equal(23, snapshot.PartitionCount);
        Assert.Equal("USER", snapshot.RegionName);
        Assert.NotNull(snapshot.Gpt);
        Assert.Null(snapshot.Mbr);
    }

    [Fact]
    public void PartitionCount_GptWithoutParsedTable_ReportsZero()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0x800,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, null, null);

        Assert.Equal(0, snapshot.PartitionCount);
    }

    [Fact]
    public void PartitionCount_MbrSnapshot_ExcludesUnusedPrimarySlotsAndAddsLogicalEntries()
    {
        var storage = CreateStorage();
        var primary = new List<MtkMbrPartition>
        {
            new(0, 0x00, 0x83, 2048, 0x20000, false),
            new(1, 0x80, 0x05, 0x22000, 0x40000, true),
            new(2, 0x00, 0x00, 0, 0, false),
            new(3, 0x00, 0x00, 0, 0, false)
        };
        var logical = new List<MtkMbrLogicalPartition>
        {
            new(0, 0x00, 0x83, 0x22040, 0x1000, 0x22000),
            new(1, 0x00, 0x83, 0x23080, 0x1000, 0x23000)
        };
        var mbr = new MtkMbrSnapshot(MtkLegacyEmmcRegion.User, 0x4000_0000 / 512, primary, logical, [primary[1]]);
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, mbr);

        Assert.Equal(4, snapshot.PartitionCount);
        Assert.Null(snapshot.Gpt);
        Assert.NotNull(snapshot.Mbr);
    }

    [Fact]
    public void PartitionCount_UnknownKind_ReportsZero()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Unknown, null, null);

        Assert.Equal(0, snapshot.PartitionCount);
    }
}