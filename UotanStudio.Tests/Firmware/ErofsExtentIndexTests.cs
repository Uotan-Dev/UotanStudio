using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the compressed extent index form (<c>Z_EROFS_ADVISE_EXTENTS</c>), where a compressed-full inode
/// stores its index as a variable-size <c>z_erofs_extent</c> array whose records are 4, 8, 16 or 32 bytes
/// wide. The area starts at the map header end rounded up to one record size, which is not the constant
/// sixteen the legacy full-index form uses, and bit 0 of the advise word means <c>EXTENTS</c> only under the
/// compressed-full layout because the compact-index layout reads the same bit as <c>COMPACTED_2B</c>.
/// <para>守护压缩 extent 索引形态（<c>Z_EROFS_ADVISE_EXTENTS</c>）：compressed-full inode 把索引存成可变长度的
/// <c>z_erofs_extent</c> 数组，记录宽 4、8、16 或 32 字节。该区起点为映射头末端向上取整到一个记录尺寸，并不是
/// 传统完整索引形态所用的常量十六；而 advise 字的第 0 位只在 compressed-full 布局下表示 <c>EXTENTS</c>，因为
/// 紧凑索引布局把同一位读作 <c>COMPACTED_2B</c>。</para>
/// </summary>
public sealed class ErofsExtentIndexTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Theory]
    [InlineData(4, 0L, 32, 0)]
    [InlineData(4, 1408L, 32, 8)]
    [InlineData(8, 1024L, 64, 0)]
    [InlineData(16, 1280L, 32, 0)]
    [InlineData(16, 1408L, 32, 8)]
    [InlineData(32, 1312L, 64, 16)]
    public void ExtentAreaBase_RecordSizes_MatchesUpstreamFormula(int recordSize, long inodeOffset, int inodeSize,
        int xattrSize)
    {
        var mapHeaderStart = AlignUp(inodeOffset + inodeSize + xattrSize, 8);
        var expected = AlignUp(mapHeaderStart + 8, recordSize);

        Assert.Equal(expected, ZErofsMapHeader.ExtentAreaBase(mapHeaderStart, recordSize));
    }

    [Fact]
    public void ExtentAreaBase_FirstHandImageData_MatchesMeasuredOffset()
    {
        Assert.Equal(1328L, ZErofsMapHeader.ExtentAreaBase(1312L, 16));
        Assert.Equal(1456L, ZErofsMapHeader.ExtentAreaBase(1440L, 16));
        Assert.Equal(1288L, ZErofsMapHeader.ExtentAreaBase(1280L, 4));
    }

    [Fact]
    public void ExtentAreaOffset_InlineXattrShiftsIndexBase_IsNotAlwaysSixteen()
    {
        Assert.Equal(16, ZErofsMapHeader.ExtentAreaOffset(32, 16));
        Assert.Equal(8, ZErofsMapHeader.ExtentAreaOffset(40, 16));
        Assert.Equal(8, ZErofsMapHeader.ExtentAreaOffset(32, 4));
        Assert.Equal(32, ZErofsMapHeader.ExtentAreaOffset(32, 32));
    }

    [Theory]
    [InlineData(4, 0x0001)]
    [InlineData(8, 0x0003)]
    [InlineData(16, 0x0005)]
    [InlineData(32, 0x0007)]
    public void ExtentAdvise_RecordSizes_EncodesExtRecSizeField(int recordSize, int expectedAdvise)
    {
        Assert.Equal((ushort)expectedAdvise, ZErofsMapHeader.ExtentAdvise(recordSize));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void ExtentRecordSize_AdviseRoundTrip_MatchesEveryForm(int recordSize)
    {
        Assert.Equal(recordSize, ZErofsMapHeader.ExtentRecordSize(ZErofsMapHeader.ExtentAdvise(recordSize)));
    }

    [Fact]
    public void ExtentMetadataSize_RecordSizes_MatchesUpstreamFormula()
    {
        Assert.Equal(80L, ZErofsMapHeader.ExtentMetadataSize(32, 16, 2));
        Assert.Equal(56L, ZErofsMapHeader.ExtentMetadataSize(32, 4, 2));
        Assert.Equal(48L, ZErofsMapHeader.ExtentMetadataSize(32, 8, 1));
        Assert.Equal(160L, ZErofsMapHeader.ExtentMetadataSize(32, 32, 3));
        Assert.Equal(40L, ZErofsMapHeader.ExtentMetadataSize(32, 8, 0));
    }

    [Fact]
    public void ExtentPrefixBytes_IsTheEightByteInitialPhysicalAddress()
    {
        Assert.Equal(8, ZErofsMapHeader.ExtentPrefixBytes);
    }

    [Fact]
    public void Build_SinglePcluster_SelectsRecSzFourAndRoundTrips()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();

        var image = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        var (layout, header) = ReadMapHeader(image, "data.bin");
        Assert.Equal(1, layout);
        Assert.True(header.HasExtents);
        Assert.Equal(4, ZErofsMapHeader.ExtentRecordSize(header.Advise));
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Read_HandConstructedRecSzEightExtents_RoundTrips()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();
        var fourByte = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        var image = RewriteExtentForm(fourByte, 8);

        var (layout, header) = ReadMapHeader(image, "data.bin");
        Assert.Equal(1, layout);
        Assert.True(header.HasExtents);
        Assert.Equal(8, ZErofsMapHeader.ExtentRecordSize(header.Advise));
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Read_HandConstructedRecSzThirtyTwoExtents_RoundTrips()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();
        var fourByte = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        var image = RewriteExtentForm(fourByte, 32);

        var (layout, header) = ReadMapHeader(image, "data.bin");
        Assert.Equal(1, layout);
        Assert.True(header.HasExtents);
        Assert.Equal(32, ZErofsMapHeader.ExtentRecordSize(header.Advise));
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_UnequalPclusterSpans_SelectsRecSzSixteenAndRoundTrips()
    {
        var payload = MixedDensityPayload();

        var image = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        var (layout, header) = ReadMapHeader(image, "data.bin");
        Assert.True(header.HasExtents, $"advise=0x{header.Advise:X4} layout={layout}");
        Assert.Equal(1, layout);
        Assert.Equal(16, ZErofsMapHeader.ExtentRecordSize(header.Advise));
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Read_ExtentRecords_CoverTheFileWithoutGapsOrOverlaps()
    {
        var payload = MixedDensityPayload();
        var image = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        using var parser = new ErofsParser(image);
        var mapHeaderOffset = checked((int)parser.CompressedMapHeaderOffset("data.bin"));
        var header = ZErofsMapHeader.FromBytes(image.AsSpan(mapHeaderOffset, ZErofsMapHeader.Size));
        var recordSize = ZErofsMapHeader.ExtentRecordSize(header.Advise);
        Assert.Equal(16, recordSize);
        var count = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(mapHeaderOffset))
            | ((ulong)header.ExtentsHi << 32)));
        Assert.True(count > 1, $"expected several extents, found {count}");
        var area = checked((int)ZErofsMapHeader.ExtentAreaBase(mapHeaderOffset, recordSize));
        var logicalStarts = new List<long>(count);
        for (var i = 0; i < count; i++)
        {
            var record = image.AsSpan(checked(area + i * recordSize), recordSize);
            logicalStarts.Add(BinaryPrimitives.ReadUInt32LittleEndian(record[12..]));
        }

        Assert.Equal(0L, logicalStarts[0]);
        Assert.Equal(logicalStarts.Count, logicalStarts.Distinct().Count());
        for (var i = 1; i < logicalStarts.Count; i++)
        {
            Assert.True(logicalStarts[i] > logicalStarts[i - 1],
                $"extent {i} logical start {logicalStarts[i]} does not follow {logicalStarts[i - 1]}");
        }

        Assert.True(logicalStarts[^1] < payload.Length,
            $"the last extent starts at {logicalStarts[^1]} of {payload.Length} bytes");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_ExtentMetadataLargerThanFullIndexes_FallsBackToFullIndexes()
    {
        var payload = FallbackPayload();

        var image = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        var (layout, header) = ReadMapHeader(image, "data.bin");
        Assert.Equal(3, layout);
        Assert.False(layout == 1 && header.HasExtents);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_ExtentIndexes_RecordsStartAtTheRoundedAreaBase()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();

        var image = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        using var parser = new ErofsParser(image);
        var mapHeaderOffset = parser.CompressedMapHeaderOffset("data.bin");
        var header = ZErofsMapHeader.FromBytes(image.AsSpan(checked((int)mapHeaderOffset), ZErofsMapHeader.Size));
        var recordSize = ZErofsMapHeader.ExtentRecordSize(header.Advise);
        var areaBase = ZErofsMapHeader.ExtentAreaBase(mapHeaderOffset, recordSize);
        var recordOffset = areaBase + (recordSize == 4 ? ZErofsMapHeader.ExtentPrefixBytes : 0);
        var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(checked((int)recordOffset)));
        var physical = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(checked((int)areaBase)));

        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(checked((int)mapHeaderOffset + 4))));
        Assert.Equal(1u, encodedLength >> ZErofsMapHeader.ExtentPlenFmtBit);
        Assert.True((encodedLength & ZErofsMapHeader.ExtentPlenMask) > 0);
        Assert.Equal(0u, physical & (ulong)(BlockSize - 1));
        Assert.NotEqual(0UL, physical);
    }

    [Fact]
    public void Build_ExtentIndexes_DeclaresFortyEightBit()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();

        var image = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Enabled);

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.NotEqual(0u, superblock.FeatureIncompat & ErofsSuperblock.Incompat48Bit);
        Assert.True(superblock.Has48Bit);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_ExtentIndexesWithoutDeclaredFortyEightBit_Throws()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = PinnedOptions();
        options.UseExtentIndexes = ErofsTriStateFeature.Enabled;
        options.Features = ErofsFeatureSet.Auto.With(ErofsFeature.Lz4ZeroPadding);

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("data.bin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("48bit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ExtentIndexesWithInlineTail_IsRejected()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = PinnedOptions();
        options.UseExtentIndexes = ErofsTriStateFeature.Enabled;
        options.CompressedTailPacking = true;
        options.Features = ErofsFeatureSet.Auto
            .With(ErofsFeature.Lz4ZeroPadding)
            .With(ErofsFeature.Ztailpacking);

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("inline", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPreservedStorageStrategies_ExtentSourceWithoutExtentTarget_Throws()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = PinnedOptions();
        options.UseExtentIndexes = ErofsTriStateFeature.Disabled;
        var record = ExtentSourceRecord("data.bin");
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("data.bin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("extent index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdviseBitZero_UnderCompactDatalayout_IsNotReadAsExtents()
    {
        var inodeBytes = new byte[ErofsInode.CompactSize];
        BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes, ErofsInode.DataLayoutCompactIndexes);
        BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(4), ErofsInode.ModeRegularFile | 0x1A4);
        var inode = ErofsInode.FromBytes(inodeBytes, 7, 12);
        var headerBytes = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(4), ZErofsMapHeader.AdviseCompacted2B);

        inode.AttachMapHeader(headerBytes, 12);

        Assert.Equal(ErofsDataLayout.CompactIndexes, inode.DataLayout);
        var compressed = Assert.IsType<ErofsCompressedIdata>(inode.Idata);
        Assert.Equal(ZErofsMapHeader.AdviseExtents, ZErofsMapHeader.AdviseCompacted2B);
        Assert.True((inode.ZAdvise & ZErofsMapHeader.AdviseExtents) != 0);
        Assert.True(compressed.Compiled2B);
        Assert.False(compressed.HasExtents);
    }

    [Fact]
    public void Build_DefaultAndDisabledExtentOptions_AreByteIdentical()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();

        var implicitAuto = Build(payload, configure: null);
        var explicitDisabled = Build(payload, options => options.UseExtentIndexes = ErofsTriStateFeature.Disabled);
        var implicitAutoAgain = Build(payload, configure: null);

        Assert.Equal(implicitAuto, explicitDisabled);
        Assert.Equal(implicitAuto, implicitAutoAgain);
    }

    [Fact]
    public async Task PreservedRebuild_OfAnExtentSource_KeepsExtentIndexes()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray();
        using var temp = new TempDirectory();
        temp.WriteBytes("data.bin", payload);
        var sourceOptions = PinnedOptions();
        sourceOptions.MaxPclusterSize = BlockSize;
        sourceOptions.UseExtentIndexes = ErofsTriStateFeature.Enabled;
        sourceOptions.FsConfig = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal)
        {
            ["."] = new FsConfigEntry(0, 0, 0x41ED),
            ["data.bin"] = new FsConfigEntry(0, 0, 0x81A4),
        };

        var source = new ErofsBuilder().Build(temp.Path, sourceOptions);
        var (sourceLayout, sourceHeader) = ReadMapHeader(source, "data.bin");
        Assert.Equal(1, sourceLayout);
        Assert.True(sourceHeader.HasExtents);

        var imagePath = Path.Combine(temp.Path, "source.img");
        await File.WriteAllBytesAsync(imagePath, source, TestContext.Current.CancellationToken);
        var extracted = Path.Combine(temp.Path, "extracted");
        var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        var packed = await handler.PackAsync(extracted, rebuiltPath, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var rebuilt = await File.ReadAllBytesAsync(rebuiltPath, TestContext.Current.CancellationToken);
        var (rebuiltLayout, rebuiltHeader) = ReadMapHeader(rebuilt, "data.bin");
        Assert.Equal(1, rebuiltLayout);
        Assert.True(rebuiltHeader.HasExtents);
        Assert.Equal(ZErofsMapHeader.ExtentRecordSize(sourceHeader.Advise),
            ZErofsMapHeader.ExtentRecordSize(rebuiltHeader.Advise));
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.NotEqual(0u, rebuiltSuperblock.FeatureIncompat & ErofsSuperblock.Incompat48Bit);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    private static ErofsOptions PinnedOptions() => new()
    {
        BlockSize = BlockSize,
        Compress = true,
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        MaxPclusterSize = BlockSize,
        SuperblockChecksum = false,
        Uuid = new byte[16],
        BuildTime = PinnedBuildTime,
    };

    private static ErofsInode ExtentSourceRecord(string path) => new()
    {
        Layer = ErofsInodeLayer.Source,
        DataLayout = ErofsDataLayout.Compressed,
        Idata = new ErofsCompressedIdata
        {
            HasExtents = true,
            ExtentRecordSize = 4,
            ExtentClusterSize = BlockSize,
            LogicalClusterSize = BlockSize,
        },
        SourcePath = path,
        SourceCompressed = true,
        SourceAlgorithm = ErofsCompressionAlgorithm.Lz4,
    };

    private static byte[] Build(byte[] payload, Action<ErofsOptions>? configure)
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = PinnedOptions();
        configure?.Invoke(options);
        return new ErofsBuilder().Build(source.Path, options);
    }

    private static (int DataLayout, ZErofsMapHeader Header) ReadMapHeader(byte[] image, string path)
    {
        using var parser = new ErofsParser(image);
        var mapHeaderOffset = parser.CompressedMapHeaderOffset(path);
        var header = ZErofsMapHeader.FromBytes(image.AsSpan(checked((int)mapHeaderOffset), ZErofsMapHeader.Size));
        var entry = parser.EnumerateFiles().Single(file => file.Path == path);
        var inodeOffset = checked((int)((long)parser.Superblock.MetaBlkAddr * parser.BlockSize
            + (long)entry.Nid * ErofsInode.CompactSize));
        var format = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(inodeOffset));
        return ((format >> ErofsInode.DataLayoutShift) & 7, header);
    }

    private static byte[] RewriteExtentForm(byte[] source, int recordSize)
    {
        using var parser = new ErofsParser(source);
        var mapHeaderOffset = checked((int)parser.CompressedMapHeaderOffset("data.bin"));
        var sourceHeader = ZErofsMapHeader.FromBytes(source.AsSpan(mapHeaderOffset, ZErofsMapHeader.Size));
        var sourceArea = ZErofsMapHeader.ExtentAreaBase(mapHeaderOffset,
            ZErofsMapHeader.ExtentRecordSize(sourceHeader.Advise));
        var physical = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(checked((int)sourceArea)));
        var sourceRecord = checked((int)sourceArea + ZErofsMapHeader.ExtentPrefixBytes);
        var encoded = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(sourceRecord));
        var fileSize = parser.ReadFileBytes("data.bin").Length;

        var image = (byte[])source.Clone();
        image.AsSpan(mapHeaderOffset, ZErofsMapHeader.Size).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(mapHeaderOffset + 4),
            ZErofsMapHeader.ExtentAdvise(recordSize));
        var area = checked((int)ZErofsMapHeader.ExtentAreaBase(mapHeaderOffset, recordSize));
        if (recordSize > ZErofsMapHeader.ExtentPrefixBytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(mapHeaderOffset), 1u);
            var record = image.AsSpan(area, recordSize);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, encoded);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)physical);
        }
        else
        {
            image[mapHeaderOffset + 7] = checked((byte)(CeilLog2(fileSize) - 12));
            if (recordSize == 4)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(area), physical);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(area + 8), encoded);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(area), encoded);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(area + 4), (uint)physical);
            }
        }

        return image;
    }

    private static int CeilLog2(long length)
    {
        var bits = 0;
        var value = length - 1;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    private static byte[] FallbackPayload()
    {
        var payload = new byte[(12 + 40 + 12) * 1024];
        var random = new Random(20261014);
        Array.Fill(payload, (byte)0x5A, 0, 12 * 1024);
        random.NextBytes(payload.AsSpan(12 * 1024, 40 * 1024));
        Array.Fill(payload, (byte)0xA5, 52 * 1024, 12 * 1024);
        return payload;
    }

    private static byte[] MixedDensityPayload()
    {
        var payload = new byte[(128 + 4 + 128) * 1024];
        var random = new Random(20261012);
        Array.Fill(payload, (byte)0x5A, 0, 128 * 1024);
        random.NextBytes(payload.AsSpan(128 * 1024, 4 * 1024));
        Array.Fill(payload, (byte)0xA5, 132 * 1024, 128 * 1024);
        return payload;
    }

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}