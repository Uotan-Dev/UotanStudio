using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using UotanStudio.Firmware.BoundedContexts.Compression.Detection;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// LZOP 压缩端测试：压缩 → 自解压回环、压缩服务门面回环与格式检测集成。
/// <para>LZOP compressor tests: compress → self-decompress round trips, compression service
/// facade round trips and format detection integration.</para>
/// </summary>
public class LzopCompressionTests
{
    [Fact]
    public void RoundTrip_EmptyInput_ProducesValidLzopFile()
    {
        var compressed = LzopCompressor.Compress([]);

        Assert.True(LzopCompressor.IsLzopFormat(compressed));
        Assert.Empty(LzopCompressor.Decompress(compressed));
    }

    [Fact]
    public void RoundTrip_TinyInputs_MatchOriginal()
    {
        // 1..64 字节随机小输入，主要覆盖存储块路径与头/块校验和。
        // Random tiny inputs of 1..64 bytes, exercising the stored-block path and checksums.
        for (var size = 1; size <= 64; size++)
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            AssertRoundTrip(data);
        }
    }

    [Fact]
    public void RoundTrip_Exactly256KiB_MatchOriginal()
    {
        AssertRoundTrip(BuildRandom(262144, seed: 1));
    }

    [Fact]
    public void RoundTrip_256KiBPlusOne_MatchOriginal()
    {
        AssertRoundTrip(BuildRandom(262145, seed: 2));
    }

    [Fact]
    public void RoundTrip_MultipleBlocks_MatchOriginal()
    {
        AssertRoundTrip(BuildRandom(262144 * 3 + 1234, seed: 3));
    }

    [Fact]
    public void RoundTrip_RandomIncompressibleData_MatchOriginal()
    {
        // 随机数据不可压缩，压缩端应走存储块路径（compressed_size == uncompressed_size）；
        // 解压结果仍须逐字节一致。
        // Random data is incompressible and exercises the stored-block path
        // (compressed_size == uncompressed_size); the decoded bytes must still match exactly.
        AssertRoundTrip(BuildRandom(100_000, seed: 4));
    }

    [Fact]
    public void RoundTrip_RepetitiveCompressibleData_MatchOriginal()
    {
        var data = new byte[200_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        AssertRoundTrip(data);
    }

    [Fact]
    public void CompressionService_RoundTripsLzop()
    {
        var payload = BuildRandom(64 * 1024, seed: 5);

        var compressed = CompressionService.Compress(payload, CompressionFormat.Lzop);
        var restored = CompressionService.Decompress(compressed, CompressionFormat.Lzop);

        Assert.Equal(payload, restored);
    }

    [Fact]
    public void CompressionFormats_DetectsCompressedLzop()
    {
        var compressed = CompressionService.Compress("lzop round trip"u8.ToArray(), CompressionFormat.Lzop);

        Assert.Equal(CompressionFormat.Lzop, CompressionDetector.Detect(compressed));
    }

    [Fact]
    public void IsLzopFormat_ReturnsTrueForCompressedData()
    {
        var compressed = CompressionService.Compress("lzop magic"u8.ToArray(), CompressionFormat.Lzop);

        Assert.True(LzopCompressor.IsLzopFormat(compressed));
    }

    [Fact]
    public void StreamingCompression_RoundTrips()
    {
        var payload = BuildRandom(262144 * 2 + 17, seed: 6);
        using var input = new MemoryStream(payload);
        using var output = new MemoryStream();

        LzopCompressor.Compress(input, output);

        var compressed = output.ToArray();
        Assert.True(LzopCompressor.IsLzopFormat(compressed));
        Assert.Equal(payload, LzopCompressor.Decompress(compressed));
    }

    [Fact]
    public void CompressedSize_IsSmallerForCompressibleInput()
    {
        var data = new byte[100_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 7);
        }

        var compressed = LzopCompressor.Compress(data);
        Assert.True(compressed.Length < data.Length / 2, $"compressed length {compressed.Length}");
    }

    private static byte[] BuildRandom(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void AssertRoundTrip(byte[] data)
    {
        var compressed = LzopCompressor.Compress(data);
        Assert.True(LzopCompressor.IsLzopFormat(compressed));
        var decompressed = LzopCompressor.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }
}