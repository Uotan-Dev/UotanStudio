using System.Security.Cryptography;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
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
        var data = new byte[65536];
        new Random(42).NextBytes(data);
        var result = F2fsCompression.CompressCluster(data, CompressionAlgorithm.Lz4);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("ramp", CompressionAlgorithm.Lz4, 0, 546, "3dfbec526d6b5488711b00785613db7e7e6f12fac2070394a0573c239a1a876c")]
    [InlineData("ramp", CompressionAlgorithm.Lz4, 3, 546, "3dfbec526d6b5488711b00785613db7e7e6f12fac2070394a0573c239a1a876c")]
    [InlineData("ramp", CompressionAlgorithm.Lz4, 9, 546, "3dfbec526d6b5488711b00785613db7e7e6f12fac2070394a0573c239a1a876c")]
    [InlineData("ramp", CompressionAlgorithm.Zstd, 0, 301, "87755dc634cffc273ffaf8edf6eb68d5de99f200ae2b9b2ca0b692cbd93c6ef9")]
    [InlineData("ramp", CompressionAlgorithm.Zstd, 3, 301, "87755dc634cffc273ffaf8edf6eb68d5de99f200ae2b9b2ca0b692cbd93c6ef9")]
    [InlineData("ramp", CompressionAlgorithm.Zstd, 9, 301, "87755dc634cffc273ffaf8edf6eb68d5de99f200ae2b9b2ca0b692cbd93c6ef9")]
    [InlineData("mixed", CompressionAlgorithm.Lz4, 0, 30815, "046856359c9989a475f872566191f219fc96dc77e7fbd248ecb41849de33f189")]
    [InlineData("mixed", CompressionAlgorithm.Lz4, 3, 19495, "ba1b89b06e1cd8dbfc431ad953a2b94cb76274573a2ce01e88e26d2201439d81")]
    [InlineData("mixed", CompressionAlgorithm.Lz4, 9, 19459, "ac8b26b8701ddef03a4408f4c4db36e6392057d28bd19c0c5b90246fae425689")]
    [InlineData("mixed", CompressionAlgorithm.Zstd, 0, 9829, "63f8a5fe4ab3376a64bc73794fac2fd073c6e3d34b71106dba217c37a8a49e93")]
    [InlineData("mixed", CompressionAlgorithm.Zstd, 3, 9829, "63f8a5fe4ab3376a64bc73794fac2fd073c6e3d34b71106dba217c37a8a49e93")]
    [InlineData("mixed", CompressionAlgorithm.Zstd, 9, 10723, "4027677f490afaebc973bab392770651beeeacd9755dfb91b9fca0bf901b67f1")]
    public void CompressCluster_KnownPayloadAndLevel_ProducesGoldenClusterBytes(
        string payloadKind,
        CompressionAlgorithm algorithm,
        int level,
        int expectedLength,
        string expectedSha256)
    {
        var payload = BuildPayload(payloadKind);

        var cluster = F2fsCompression.CompressCluster(payload, algorithm, level);

        Assert.NotNull(cluster);
        Assert.Equal(expectedLength, cluster.Length);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(cluster)));
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

    private static byte[] BuildPayload(string payloadKind)
    {
        var payload = new byte[65536];
        if (payloadKind == "mixed")
        {
            var state = 0x12345678u;
            for (var i = 0; i < payload.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                payload[i] = (byte)((i % 7 == 0) ? (int)(state >> 24) : (97 + (i % 3)));
            }

            return payload;
        }

        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 17 + 3);
        }

        return payload;
    }
}