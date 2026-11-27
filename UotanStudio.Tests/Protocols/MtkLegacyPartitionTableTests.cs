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
    public void PartitionCount_MbrSnapshot_ExcludesUnusedSlotsAndTheExtendedContainer()
    {
        var storage = CreateStorage();
        var mbr = CreateMbr();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, mbr);

        Assert.Equal(1 + 2, snapshot.PartitionCount);
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

    [Fact]
    public void EnumerateExtents_GptSnapshot_KeepsEntryBytesAndOrdersByAscendingOffset()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, CreateGpt(4), null);

        var extents = snapshot.EnumerateExtents();

        Assert.Equal(4, extents.Count);
        for (var index = 0; index < extents.Count; index++)
        {
            var expectedOffset = (34L * 512) + (index * 0x80000L);
            Assert.Equal("p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                extents[index].Name);
            Assert.Equal(expectedOffset, extents[index].ByteOffset);
            Assert.Equal(0x80000L, extents[index].ByteLength);
            Assert.Null(extents[index].MbrPartitionType);
            Assert.False(extents[index].IsLogical);
        }
    }

    [Fact]
    public void EnumerateExtents_GptSnapshotWithDisorderedEntries_OrdersByOffsetNotByTableSlot()
    {
        var storage = CreateStorage();
        var partitions = new List<GptPartition>
        {
            new(0, Guid.NewGuid(), Guid.NewGuid(), 0x900, 0x9FF, 0, "late", 0x900L * 512, 0x100L * 512),
            new(1, Guid.NewGuid(), Guid.NewGuid(), 0x100, 0x1FF, 0, "early", 0x100L * 512, 0x100L * 512)
        };
        var gpt = new GptTable(Guid.NewGuid(), 512, 0x800_0000, partitions, new string('0', 64),
            92, 34, 0x7FFF_FFDE, 128, 128, 2, 0x7FFF_FFFF);
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, gpt, null);

        var extents = snapshot.EnumerateExtents();

        Assert.Equal(["early", "late"], extents.Select(static extent => extent.Name));
        Assert.Equal(0x100L * 512, extents[0].ByteOffset);
        Assert.Equal(0x900L * 512, extents[1].ByteOffset);
    }

    [Fact]
    public void EnumerateExtents_MbrSnapshot_RecomputesBytesAgainstTheTableSector()
    {
        var storage = CreateStorage();
        var mbr = CreateMbr();
        const long tableOffset = 0xB80000;
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, tableOffset,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, mbr);

        var extents = snapshot.EnumerateExtents();

        Assert.Equal(3, extents.Count);
        Assert.Equal(tableOffset + (2048L * MtkMbrSnapshot.SectorSize), extents[0].ByteOffset);
        Assert.Equal(0x20000L * MtkMbrSnapshot.SectorSize, extents[0].ByteLength);
        Assert.Equal<byte?>(0x83, extents[0].MbrPartitionType);
        Assert.False(extents[0].IsLogical);
    }

    [Fact]
    public void EnumerateExtents_MbrSnapshotSkipsTheExtendedContainerAndMarksLogicalEntries()
    {
        var storage = CreateStorage();
        var mbr = CreateMbr();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, mbr);

        var extents = snapshot.EnumerateExtents();

        Assert.DoesNotContain(extents, static extent => extent.MbrPartitionType is 0x05 or 0x0F);
        Assert.Equal([0x22040u * 512, 0x23080u * 512],
            extents.Where(static extent => extent.IsLogical).Select(static extent => extent.ByteOffset));
        Assert.All(extents.Where(static extent => extent.IsLogical),
            static extent => Assert.Equal(0x1000L * MtkMbrSnapshot.SectorSize, extent.ByteLength));
    }

    [Fact]
    public void EnumerateExtents_MbrSnapshot_NamesEachExtentAfterItsTypeAndOrdinal()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, CreateMbr());

        var extents = snapshot.EnumerateExtents();

        Assert.Equal(["mbr-83-1", "mbr-83-2", "mbr-83-3"],
            extents.Select(static extent => extent.Name));
    }

    [Fact]
    public void EnumerateExtents_MbrSnapshotWithDistinctTypes_RestartsEachOrdinal()
    {
        var storage = CreateStorage();
        var primary = new List<MtkMbrPartition>
        {
            new(0, 0x00, 0x83, 2048, 0x1000, false),
            new(1, 0x00, 0x0C, 0x4000, 0x1000, false),
            new(2, 0x00, 0x83, 0x8000, 0x1000, false),
            new(3, 0x00, 0x00, 0, 0, false)
        };
        var mbr = new MtkMbrSnapshot(MtkLegacyEmmcRegion.User, 0x4000_0000 / 512, primary, [], []);
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, mbr);

        var extents = snapshot.EnumerateExtents();

        Assert.Equal(["mbr-83-1", "mbr-0C-1", "mbr-83-2"],
            extents.Select(static extent => extent.Name));
    }

    [Fact]
    public void EnumerateExtents_UnknownKindOrMissingTable_IsEmpty()
    {
        var storage = CreateStorage();
        var unknown = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Unknown, null, null);
        var gptWithoutTable = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, null, null);
        var mbrWithoutTable = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, null);

        Assert.Empty(unknown.EnumerateExtents());
        Assert.Empty(gptWithoutTable.EnumerateExtents());
        Assert.Empty(mbrWithoutTable.EnumerateExtents());
    }

    [Fact]
    public void PartitionCount_MatchesTheEnumeratedExtentCount()
    {
        var storage = CreateStorage();
        var gpt = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, CreateGpt(6), null);
        var mbr = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, CreateMbr());

        Assert.Equal(6, gpt.PartitionCount);
        Assert.Equal(gpt.EnumerateExtents().Count, gpt.PartitionCount);
        Assert.Equal(mbr.EnumerateExtents().Count, mbr.PartitionCount);
    }

    [Fact]
    public void FindExtent_MbrName_ReturnsTheExtentThatCarriesIt()
    {
        var storage = CreateStorage();
        const long tableOffset = 0xB80000;
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, tableOffset,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, CreateMbr());

        var extent = snapshot.FindExtent("mbr-83-2");

        Assert.Equal(tableOffset + (0x22040L * MtkMbrSnapshot.SectorSize), extent.ByteOffset);
        Assert.True(extent.IsLogical);
    }

    [Fact]
    public void FindExtent_GptName_ReturnsTheEntryDeclaredByTheTable()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, CreateGpt(3), null);

        var extent = snapshot.FindExtent("p2");

        Assert.Equal((34L * 512) + (2 * 0x80000L), extent.ByteOffset);
        Assert.Equal(0x80000L, extent.ByteLength);
    }

    [Fact]
    public void FindExtent_AbsentName_ThrowsInsteadOfGuessing()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, CreateMbr());

        Assert.Throws<KeyNotFoundException>(() => snapshot.FindExtent("mbr-83-9"));
    }

    [Fact]
    public void FindExtent_NameDiffersOnlyInCase_IsNotMatched()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, CreateMbr());

        Assert.Throws<KeyNotFoundException>(() => snapshot.FindExtent("MBR-83-1"));
    }

    [Fact]
    public void FindExtent_DuplicateNames_AreRejectedAsAmbiguous()
    {
        var storage = CreateStorage();
        var partitions = new List<GptPartition>
        {
            new(0, Guid.NewGuid(), Guid.NewGuid(), 0x100, 0x1FF, 0, "dup", 0x100L * 512, 0x100L * 512),
            new(1, Guid.NewGuid(), Guid.NewGuid(), 0x200, 0x2FF, 0, "dup", 0x200L * 512, 0x100L * 512)
        };
        var gpt = new GptTable(Guid.NewGuid(), 512, 0x800_0000, partitions, new string('0', 64),
            92, 34, 0x7FFF_FFDE, 128, 128, 2, 0x7FFF_FFFF);
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Gpt, gpt, null);

        Assert.Throws<InvalidDataException>(() => snapshot.FindExtent("dup"));
    }

    [Fact]
    public void FindExtent_BlankName_IsRejectedBeforeTheLookup()
    {
        var storage = CreateStorage();
        var snapshot = new MtkLegacyPartitionTableSnapshot(storage, MtkLegacyEmmcRegion.User, 0,
            MtkBromLayoutHeader.None, PartitionTableKind.Mbr, null, CreateMbr());

        Assert.Throws<ArgumentNullException>(() => snapshot.FindExtent(null!));
        Assert.Throws<ArgumentException>(() => snapshot.FindExtent("   "));
    }

    [Fact]
    public void HasBootSignature_MbrByteOrder_IsAccepted()
    {
        var sector = new byte[MtkMbrSnapshot.SectorSize];
        sector[MtkMbrSnapshot.SectorSize - 2] = 0x55;
        sector[MtkMbrSnapshot.SectorSize - 1] = 0xAA;

        Assert.True(MtkMbrSnapshot.HasBootSignature(sector));
    }

    [Fact]
    public void HasBootSignature_ReversedByteOrder_IsRejected()
    {
        var sector = new byte[MtkMbrSnapshot.SectorSize];
        sector[MtkMbrSnapshot.SectorSize - 2] = 0xAA;
        sector[MtkMbrSnapshot.SectorSize - 1] = 0x55;

        Assert.False(MtkMbrSnapshot.HasBootSignature(sector));
    }

    [Fact]
    public void HasBootSignature_ShortOrMissingSignature_IsRejected()
    {
        var shortSector = new byte[MtkMbrSnapshot.SectorSize - 1];
        shortSector[^1] = 0xAA;
        var noSignature = new byte[MtkMbrSnapshot.SectorSize];

        Assert.False(MtkMbrSnapshot.HasBootSignature(shortSector));
        Assert.False(MtkMbrSnapshot.HasBootSignature(noSignature));
    }

    [Fact]
    public void ResolveExtentSectorCount_MaxValueSentinel_ClampsToRegionRemainder()
    {
        const uint totalSectors = 15_246_336;
        const uint firstLba = 1024;

        var count = MtkMbrSnapshot.ResolveExtentSectorCount(firstLba, uint.MaxValue, firstLba, totalSectors);

        Assert.Equal(totalSectors - firstLba, count);
    }

    [Fact]
    public void ResolveExtentSectorCount_DeclaredSumAtLbaLimit_ClampsToRegionRemainder()
    {
        const uint totalSectors = 15_246_336;
        const uint declaredFirstLba = 2_253_824;
        const uint declaredCount = 0xFFDD_9BFF;
        const uint absoluteFirstLba = 2_254_848;

        Assert.Equal((uint)(declaredFirstLba + declaredCount), uint.MaxValue);

        var count = MtkMbrSnapshot.ResolveExtentSectorCount(
            declaredFirstLba, declaredCount, absoluteFirstLba, totalSectors);

        Assert.Equal(totalSectors - absoluteFirstLba, count);
    }

    [Fact]
    public void ResolveExtentSectorCount_NormalExtent_IsPreserved()
    {
        const uint count = 0x5000;

        var resolved = MtkMbrSnapshot.ResolveExtentSectorCount(18_432, count, 18_432, 15_246_336);

        Assert.Equal(count, resolved);
    }

    [Fact]
    public void ResolveExtentSectorCount_EmptyOrOutOfRange_IsRejected()
    {
        const uint totalSectors = 15_246_336;

        var empty = MtkMbrSnapshot.ResolveExtentSectorCount(0, 0, 0, totalSectors);
        var beyond = MtkMbrSnapshot.ResolveExtentSectorCount(
            totalSectors - 1, 4096, totalSectors - 1, totalSectors);

        Assert.Equal(0u, empty);
        Assert.Equal(0u, beyond);
    }

    private static MtkMbrSnapshot CreateMbr()
    {
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
        return new MtkMbrSnapshot(MtkLegacyEmmcRegion.User, 0x4000_0000 / 512, primary, logical, [primary[1]]);
    }
}