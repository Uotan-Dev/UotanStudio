using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the compacted 2-byte index form (<c>Z_EROFS_ADVISE_COMPACTED_2B</c>), where a compacted index
/// area is written as a 4-byte head, a 2-byte middle section of 16 entries per 32-byte unit, and a 4-byte
/// tail. The middle section is only reachable when the area is long enough, and bit 0 of <c>h_advise</c>
/// means <c>COMPACTED_2B</c> here but <c>EXTENTS</c> on a full-index inode, so the data layout is what
/// disambiguates it.
/// <para>守护紧凑 2 字节索引形态（<c>Z_EROFS_ADVISE_COMPACTED_2B</c>）：紧凑索引区被写成 4 字节头部、
/// 每 32 字节单元容纳 16 条的 2 字节中段，以及 4 字节尾部。只有索引区足够长时中段才会出现，而
/// <c>h_advise</c> 的 bit 0 在完整索引 inode 上表示 <c>EXTENTS</c>，语义由数据布局区分。</para>
/// </summary>
public sealed class ErofsCompacted2BTests
{
    private const int BlockSize = 1024;

    private const int Clusters = 1500;

    [Fact]
    public void Build_Compacted2B_ShrinksIndexAreaAndRoundTrips()
    {
        var payload = CompressiblePayload(Clusters * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);

        var fourByte = Build(source.Path, useCompacted2B: false);
        var twoByte = Build(source.Path, useCompacted2B: true);

        Assert.True(twoByte.Length < fourByte.Length,
            $"expected the 2-byte form to shrink the image; fourByte={fourByte.Length} twoByte={twoByte.Length}");

        using var parser = new ErofsParser(twoByte);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_Compacted2B_SetsSharedBitZeroOnCompactedLayout()
    {
        var payload = CompressiblePayload(Clusters * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);

        var image = Build(source.Path, useCompacted2B: true);
        using var parser = new ErofsParser(image);
        var header = ZErofsMapHeader.FromBytes(
            image.AsSpan((int)parser.CompressedMapHeaderOffset("data.bin"), ZErofsMapHeader.Size));

        Assert.True(header.HasCompacted2B);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void AdviseBitZero_IsSharedBetweenCompacted2BAndExtents()
    {
        Assert.Equal((ushort)0x0001, ZErofsMapHeader.AdviseCompacted2B);
        Assert.Equal(ZErofsMapHeader.AdviseExtents, ZErofsMapHeader.AdviseCompacted2B);
    }

    [Fact]
    public void Build_FullIndexes_LeavesSharedBitZeroClear()
    {
        var payload = CompressiblePayload(Clusters * BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);

        var image = Build(source.Path, useCompacted2B: false, useCompactedIndexes: false);
        using var parser = new ErofsParser(image);
        var header = ZErofsMapHeader.FromBytes(
            image.AsSpan((int)parser.CompressedMapHeaderOffset("data.bin"), ZErofsMapHeader.Size));

        Assert.False(header.HasCompacted2B);
        Assert.False(header.HasExtents);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_Compacted2BAndFullIndexes_IsRejected()
    {
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            UseCompactedIndexes = false,
            UseCompacted2BIndexes = true,
        };

        Assert.Throws<NotSupportedException>(() => options.Validate());
    }

    [Fact]
    public void Build_Compacted2BWithOversizedLogicalCluster_IsRejected()
    {
        var options = new ErofsOptions
        {
            BlockSize = 8192,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            UseCompactedIndexes = true,
            UseCompacted2BIndexes = true,
        };

        Assert.Throws<NotSupportedException>(() => options.Validate());
    }

    private static byte[] Build(string sourcePath, bool useCompacted2B, bool useCompactedIndexes = true)
        => new ErofsBuilder().Build(sourcePath, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            UseCompactedIndexes = useCompactedIndexes,
            UseCompacted2BIndexes = useCompacted2B,
        });

    private static byte[] CompressiblePayload(int length)
    {
        var payload = new byte[length];
        var block = new byte[BlockSize];
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = (byte)((i * 31 + i / 7) & 0x3F);
        }
        for (var written = 0; written < payload.Length; written += block.Length)
        {
            var count = Math.Min(block.Length, payload.Length - written);
            block.AsSpan(0, count).CopyTo(payload.AsSpan(written));
            block[0] = (byte)(written / block.Length);
        }
        return payload;
    }
}