using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class SparseBoundedReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(131071)]
    public void FromStream_LogicalLengthExceedsCallerLimit_RejectsAtHeader(long limit)
    {
        using var stream = new MemoryStream(BuildFillImage(0x12345678));

        Assert.Throws<InvalidDataException>(() =>
            SparseReader.FromStream(stream, true, false, 65536, TestContext.Current.CancellationToken, limit));

        Assert.Equal(28, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void FromStream_LogicalLengthEqualsCallerLimit_ValidatesChecksum()
    {
        using var stream = new MemoryStream(BuildFillImage(0x12345678));
        using var image = SparseReader.FromStream(stream, true, false, 65536,
            TestContext.Current.CancellationToken, 131072);

        Assert.Single(image.Chunks);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void FromStream_NegativeLogicalLimit_RejectsBeforeReading()
    {
        using var stream = new MemoryStream(BuildFillImage(0x12345678));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SparseReader.FromStream(stream, true, false, 65536, TestContext.Current.CancellationToken, -1));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void FromStream_ExcessiveChunkCount_RejectsBeforeAllocatingChunks()
    {
        var bytes = BuildFillImage(0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 65537);
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() =>
            SparseReader.FromStream(stream, true, false, 65536, TestContext.Current.CancellationToken));

        Assert.Equal(28, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void FromStream_LogicalLengthOverflowsInt64_RejectsBeforeReadingChunks()
    {
        var bytes = BuildFillImage(0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0xFFFFFFFC);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), uint.MaxValue);
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() =>
            SparseReader.FromStream(stream, true, false, 65536, TestContext.Current.CancellationToken));

        Assert.Equal(28, stream.Position);
    }

    [Fact]
    public async Task FromStream_ConcurrentFillChecksums_KeepPatternsIsolated()
    {
        var tasks = Enumerable.Range(1, 16).Select(index => Task.Run(() =>
        {
            var bytes = BuildFillImage((uint)index * 0x01010101);
            for (var iteration = 0; iteration < 32; iteration++)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(bytes);
                using var image = SparseReader.FromStream(stream, true, false, 65536, TestContext.Current.CancellationToken);
                Assert.Single(image.Chunks);
            }
        }, TestContext.Current.CancellationToken));

        await Task.WhenAll(tasks);
    }

    private static byte[] BuildFillImage(uint value)
    {
        var logical = new byte[128 * 1024];
        for (var i = 0; i < logical.Length; i += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(i), value);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xED26FF3Au);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)28);
        writer.Write((ushort)12);
        writer.Write(512u);
        writer.Write((uint)(logical.Length / 512));
        writer.Write(1u);
        writer.Write(Crc32.HashToUInt32(logical));
        writer.Write((ushort)0xCAC2);
        writer.Write((ushort)0);
        writer.Write((uint)(logical.Length / 512));
        writer.Write(16u);
        writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }
}