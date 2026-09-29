using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.OtaDelta.CareMap;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class CareMapTests : IDisposable
{
    private const int Block = CareMap.BlockSize;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uot-caremap-" + Guid.NewGuid().ToString("N"));

    public CareMapTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ToBytes_SinglePartitionWithRanges_MatchesProto3WireLayout()
    {
        var map = new CareMap();
        map.Partitions.Add(new CareMapPartitionInfo { Name = "system", Ranges = "2,0,3" });

        Assert.Equal([0x0A, 0x0F, 0x0A, 0x06, 0x73, 0x79, 0x73, 0x74, 0x65, 0x6D, 0x12, 0x05, 0x32, 0x2C, 0x30, 0x2C, 0x33],
            map.ToBytes());
    }

    [Fact]
    public void ToBytes_RecordWithoutFingerprint_OmitsIdentityFields()
    {
        var map = new CareMap();
        map.Partitions.Add(new CareMapPartitionInfo { Name = "odm", Ranges = "2,0,1" });
        var bytes = map.ToBytes();

        Assert.DoesNotContain((byte)0x1A, bytes);
        Assert.DoesNotContain((byte)0x22, bytes);
        Assert.Equal(14, bytes.Length);
    }

    [Fact]
    public void RoundTrip_AllFields_PreservesEveryRecord()
    {
        var map = new CareMap();
        map.Partitions.Add(new CareMapPartitionInfo
        {
            Name = "vendor",
            Ranges = "4,0,16,32,48",
            PropertyId = "ro.vendor.build.thumbprint",
            Fingerprint = "2026-09-29-r1"
        });
        map.Partitions.Add(new CareMapPartitionInfo { Name = "product", Ranges = "2,0,4" });

        var parsed = CareMap.FromBytes(map.ToBytes());

        Assert.True(map.Equals(parsed));
        Assert.Equal(2, parsed.Partitions.Count);
        Assert.Equal("vendor", parsed.Partitions[0].Name);
        Assert.Equal("4,0,16,32,48", parsed.Partitions[0].Ranges);
        Assert.Equal("ro.vendor.build.thumbprint", parsed.Partitions[0].PropertyId);
        Assert.Equal("2026-09-29-r1", parsed.Partitions[0].Fingerprint);
        Assert.Equal("", parsed.Partitions[1].PropertyId);
        Assert.Equal([new CareMapRange(0, 16), new CareMapRange(32, 48)], parsed.Partitions[0].ParseRanges());
    }

    [Fact]
    public void FromBytes_UnknownField_KeepsKnownValues()
    {
        var message = new byte[]
        {
            0x0A, 0x0E,
            0x0A, 0x03, 0x6F, 0x64, 0x6D,
            0x12, 0x05, 0x32, 0x2C, 0x30, 0x2C, 0x31,
            0x28, 0x07
        };

        var parsed = CareMap.FromBytes(message);

        Assert.Equal("odm", parsed.Partitions[0].Name);
        Assert.Equal("2,0,1", parsed.Partitions[0].Ranges);
    }

    [Fact]
    public void FromBytes_TruncatedMessage_Throws()
    {
        Assert.ThrowsAny<Exception>(() => CareMap.FromBytes(new byte[] { 0x0A, 0x40, 0x0A }));
    }

    [Fact]
    public void Format_OverlappingRanges_MergesAndRendersExclusiveEnds()
    {
        var text = CareMapRanges.Format([new CareMapRange(8, 10), new CareMapRange(0, 3), new CareMapRange(3, 5)]);

        Assert.Equal("4,0,5,8,10", text);
    }

    [Fact]
    public void Format_EmptyRangeSet_Throws()
    {
        Assert.Throws<ArgumentException>(() => CareMapRanges.Format([]));
    }

    [Theory]
    [InlineData("2,0,1")]
    [InlineData("6,0,3,8,10,12,13")]
    public void Parse_ProducedText_RoundTrips(string text)
    {
        var ranges = CareMapRanges.Parse(text);

        Assert.Equal(text, CareMapRanges.Format(ranges));
    }

    [Theory]
    [InlineData("1,0")]
    [InlineData("3,0,5,8")]
    [InlineData("4,0,3")]
    [InlineData("4,0,3,8,8")]
    [InlineData("4,0,3,8,x")]
    [InlineData("4,-1,3,8,9")]
    public void Parse_MalformedText_ThrowsFormat(string text)
    {
        Assert.Throws<FormatException>(() => CareMapRanges.Parse(text));
    }

    [Fact]
    public void Intersect_PartiallyCoveringOperands_KeepsOnlyOverlap()
    {
        var result = CareMapRanges.Intersect([new CareMapRange(0, 10)], [new CareMapRange(4, 6), new CareMapRange(8, 20)]);

        Assert.Equal([new CareMapRange(4, 6), new CareMapRange(8, 10)], result);
    }

    [Fact]
    public void SupportedPartitions_UpstreamVeritySet_MatchesNamesAndCount()
    {
        Assert.Equal(
            ["system", "vendor", "product", "system_ext", "odm", "vendor_dlkm", "odm_dlkm", "system_dlkm"],
            CareMapBuilder.SupportedPartitions);
        Assert.True(CareMapBuilder.IsSupportedPartition("system_ext"));
        Assert.False(CareMapBuilder.IsSupportedPartition("boot"));
        Assert.False(CareMapBuilder.IsSupportedPartition("SYSTEM"));
        Assert.False(CareMapBuilder.IsSupportedPartition(null));
    }

    [Fact]
    public void TryBuildFromImage_RawVerityImage_CoversVerifiedBlocksOnly()
    {
        var image = WriteRawImage("vendor.img", totalBlocks: 8, originalBytes: 5 * Block);

        var verified = AvbImageFootprint.TryRead(image, TestContext.Current.CancellationToken);
        Assert.Equal(5 * Block, verified);
        var record = CareMapBuilder.TryBuildFromImage("vendor", image, verified!.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal("vendor", record!.Name);
        Assert.Equal("2,0,5", record.Ranges);
    }

    [Fact]
    public void TryRead_ImageWithoutFooter_ReturnsNull()
    {
        var path = Path.Combine(_root, "unsigned.img");
        File.WriteAllBytes(path, new byte[4 * Block]);

        Assert.Null(AvbImageFootprint.TryRead(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryRead_ImageShorterThanFooter_ReturnsNull()
    {
        var path = Path.Combine(_root, "tiny.img");
        File.WriteAllBytes(path, new byte[32]);

        Assert.Null(AvbImageFootprint.TryRead(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryRead_SparseImage_ReadsFooterFromExpandedTail()
    {
        using var sparse = new SparseFile(Block, 4 * Block);
        sparse.AddRawChunk(new byte[3 * Block]);
        sparse.AddRawChunk(ChunkWithFooter(1, Footer(3 * Block)));
        var path = Path.Combine(_root, "sparse_footprint.img");
        using (var output = File.Create(path))
        {
            sparse.WriteToStream(output, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(3 * Block, AvbImageFootprint.TryRead(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryBuildFromImage_UnsupportedPartition_ReturnsNullWithoutReadingImage()
    {
        var record = CareMapBuilder.TryBuildFromImage("boot", Path.Combine(_root, "missing.img"), Block,
            TestContext.Current.CancellationToken);

        Assert.Null(record);
    }

    [Fact]
    public void TryBuildFromImage_SparseImageWithHoles_SkipsDontCareBlocks()
    {
        using var sparse = new SparseFile(Block, 9 * Block);
        sparse.AddRawChunk(new byte[3 * Block]);
        sparse.AddDontCareChunk(3 * Block);
        sparse.AddRawChunk(ChunkWithFooter(3, Footer(8 * Block)));
        var path = Path.Combine(_root, "system_sparse.img");
        using (var output = File.Create(path))
        {
            sparse.WriteToStream(output, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        var record = CareMapBuilder.TryBuildFromImage("system", path, 8 * Block, TestContext.Current.CancellationToken);

        Assert.Equal("4,0,3,6,8", record!.Ranges);
    }

    [Fact]
    public void TryBuildFromImage_FooterClaimingWholeImage_Throws()
    {
        var image = WriteRawImage("bad_footer.img", totalBlocks: 4, originalBytes: 4 * Block);

        Assert.Throws<InvalidDataException>(() => CareMapBuilder.TryBuildFromImage("odm", image, 4 * Block, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryBuildFromImage_ForeignSparseBlockSize_Throws()
    {
        var footer = Footer(originalImageSize: 2 * Block);
        using var sparse = new SparseFile(8192, 4 * Block);
        sparse.AddRawChunk(ChunkWithFooter(2, footer));
        var path = Path.Combine(_root, "foreign_block.img");
        using (var output = File.Create(path))
        {
            sparse.WriteToStream(output, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Throws<InvalidDataException>(() => CareMapBuilder.TryBuildFromImage("vendor", path, 2 * Block, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryBuildFromImage_CancelledToken_ThrowsBeforeReadingImage()
    {
        var image = WriteRawImage("cancelled.img", totalBlocks: 2, originalBytes: Block);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(
            () => CareMapBuilder.TryBuildFromImage("system", image, Block, cancellation.Token), cancellation.Token));
    }

    private string WriteRawImage(string name, int totalBlocks, long originalBytes)
    {
        var data = new byte[totalBlocks * Block];
        Footer(originalImageSize: originalBytes).CopyTo(data, data.Length - AvbFooterSize);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] ChunkWithFooter(int blocks, byte[] footer)
    {
        var data = new byte[blocks * Block];
        footer.CopyTo(data, data.Length - AvbFooterSize);
        return data;
    }

    private static byte[] Footer(long originalImageSize)
    {
        var footer = new byte[AvbFooterSize];
        "AVBf"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(12), checked((ulong)originalImageSize));
        return footer;
    }

    private const int AvbFooterSize = 64;

    public void Dispose() => Directory.Delete(_root, true);
}