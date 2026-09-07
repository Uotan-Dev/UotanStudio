using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// F2FS 压缩与 checksum 单元测试。
/// <para>F2FS compression and checksum unit tests.</para>
/// </summary>
public class F2fsCompressionTests
{
    [Fact]
    public void CompressAndDecompress_Lz4_RoundTrips()
    {
        var original = new byte[65536];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i * 31 + 7);
        }

        var compressed = F2fsCompression.CompressCluster(original, CompressionAlgorithm.Lz4);
        Assert.NotNull(compressed);
        Assert.True(compressed.Length < original.Length + F2fsCompression.ClusterHeaderSize);

        var header = F2fsCompression.ParseHeader(compressed);
        Assert.Equal(compressed.Length - 24, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(compressed));
        Assert.Equal(original.Length, header.UncompressedSize);

        var decompressed = F2fsCompression.DecompressCluster(
            compressed.AsSpan(F2fsCompression.ClusterHeaderSize),
            CompressionAlgorithm.Lz4,
            header.UncompressedSize,
            F2fsCompression.ClusterBytes);

        Assert.Equal(original.Length, decompressed.Length);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressAndDecompress_Zstd_RoundTrips()
    {
        var original = new byte[65536];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i * 17 + 3);
        }

        var compressed = F2fsCompression.CompressCluster(original, CompressionAlgorithm.Zstd, 3);
        Assert.NotNull(compressed);

        var header = F2fsCompression.ParseHeader(compressed);
        Assert.Equal(compressed.Length - 24, header.CompressedSize);

        var decompressed = F2fsCompression.DecompressCluster(
            compressed.AsSpan(F2fsCompression.ClusterHeaderSize),
            CompressionAlgorithm.Zstd,
            header.UncompressedSize,
            F2fsCompression.ClusterBytes);

        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressAndDecompress_Lzo_RoundTrips()
    {
        // LZO 由托管 LZO1X 实现支持（对齐内核 lzo1x_1_compress / lzo1x_decompress_safe）。
        // LZO is supported by the managed LZO1X implementation (kernel lzo1x_1_compress /
        // lzo1x_decompress_safe).
        var original = new byte[65536];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i % 17);
        }

        var compressed = F2fsCompression.CompressCluster(original, CompressionAlgorithm.Lzo);
        Assert.NotNull(compressed);
        Assert.True(compressed.Length < original.Length + F2fsCompression.ClusterHeaderSize);

        var header = F2fsCompression.ParseHeader(compressed);
        Assert.Equal(compressed.Length - 24, header.CompressedSize);
        Assert.Equal(original.Length, header.UncompressedSize);

        var decompressed = F2fsCompression.DecompressCluster(
            compressed.AsSpan(F2fsCompression.ClusterHeaderSize),
            CompressionAlgorithm.Lzo,
            header.UncompressedSize,
            F2fsCompression.ClusterBytes);

        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void Decompress_Lzo_GarbageData_ThrowsInvalidData()
    {
        Assert.ThrowsAny<InvalidDataException>(() =>
            F2fsCompression.DecompressCluster(
                new byte[100], CompressionAlgorithm.Lzo, 1024, F2fsCompression.ClusterBytes));
    }

    [Fact]
    public void Compress_UncompressibleData_ReturnsNull()
    {
        // 全随机数据通常不可压缩。
        var data = new byte[65536];
        new Random(42).NextBytes(data);
        var result = F2fsCompression.CompressCluster(data, CompressionAlgorithm.Lz4);
        // 如果压缩后不比原文小，应返回 null。
        Assert.Null(result);
    }

    [Fact]
    public void F2fsSummary_RoundTrips()
    {
        var summary = new F2fsSummary
        {
            Nid = 0x12345678,
            Version = 42,
            OfsInNode = 1000,
        };

        var buffer = new byte[F2fsSummary.Size];
        summary.WriteTo(buffer);

        var parsed = F2fsSummary.FromBytes(buffer);
        Assert.Equal(summary.Nid, parsed.Nid);
        Assert.Equal(summary.Version, parsed.Version);
        Assert.Equal(summary.OfsInNode, parsed.OfsInNode);
    }

    [Fact]
    public void F2fsCompressedCluster_RoundTrips()
    {
        var cluster = new F2fsCompressedCluster
        {
            Algorithm = CompressionAlgorithm.Lz4,
            UncompressedSize = 65536,
            CompressedSize = 32000,
        };

        var buffer = new byte[F2fsCompressedCluster.HeaderSize];
        cluster.WriteHeaderTo(buffer);

        var parsed = F2fsCompressedCluster.FromBytes(buffer);
        Assert.Equal(cluster.Algorithm, parsed.Algorithm);
        Assert.Equal(cluster.UncompressedSize, parsed.UncompressedSize);
        Assert.Equal(cluster.CompressedSize, parsed.CompressedSize);
    }
}