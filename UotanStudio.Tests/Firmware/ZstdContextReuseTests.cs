using UotanStudio.Compress;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the behaviour of the pooled zstd contexts: repeated, parallel and concurrent compressions must all produce a
/// frame that decodes back to the original bytes, and the decompression context must not leak state between callers.
/// A pooled native context that is handed out twice or returned while still in use would corrupt frames silently.
/// <para>固定池化 zstd 上下文的行为：重复、并行与并发压缩都必须产出可还原原文的帧，解压上下文也不得在调用方之间泄漏状态。
/// 一个被同时借出两次、或仍在使用时就被归还的池化原生上下文会静默损坏帧。</para>
/// </summary>
public class ZstdContextReuseTests
{
    private static byte[] Pattern(int length, int salt)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index % 251) + salt + (index / 4096));
        }

        return data;
    }

    [Fact]
    public void Compress_RepeatedCalls_ProduceIdenticalFramesAndRoundTrip()
    {
        var data = Pattern(64 * 1024, 3);
        var first = CompressionService.Compress(data, CompressionFormat.Zstd);
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var again = CompressionService.Compress(data, CompressionFormat.Zstd);
            Assert.Equal(first, again);
        }

        Assert.Equal(data, CompressionService.Decompress(first, CompressionFormat.Zstd));
    }

    [Fact]
    public void Compress_AlternatingLevels_RoundTripAtEachLevel()
    {
        var data = Pattern(32 * 1024, 11);
        foreach (var level in new[] { 1, 3, 9, 3, 1 })
        {
            var encoded = CompressionService.Compress(data, CompressionFormat.Zstd, new CompressionOptions { Level = level });
            Assert.Equal(data, CompressionService.Decompress(encoded, CompressionFormat.Zstd));
        }
    }

    [Fact]
    public void Compress_ConcurrentCallers_RoundTripIndependently()
    {
        var payloads = Enumerable.Range(0, 8).Select(index => Pattern(48 * 1024, index)).ToArray();
        Parallel.For(0, payloads.Length, index =>
        {
            for (var repeat = 0; repeat < 6; repeat++)
            {
                var encoded = CompressionService.Compress(payloads[index], CompressionFormat.Zstd);
                var decoded = CompressionService.Decompress(encoded, CompressionFormat.Zstd);
                Assert.Equal(payloads[index], decoded);
            }
        });
    }

    [Fact]
    public void Compress_ParallelChunkedInput_RoundTrips()
    {
        var data = Pattern(4 * 1024 * 1024, 5);
        var encoded = CompressionService.Compress(data, CompressionFormat.Zstd, new CompressionOptions { MaxDegreeOfParallelism = 4 });
        Assert.Equal(data, CompressionService.Decompress(encoded, CompressionFormat.Zstd));
    }

    [Fact]
    public void Decompress_InvalidFrame_ThrowsWithoutPoisoningThePooledContext()
    {
        var valid = CompressionService.Compress(Pattern(4096, 2), CompressionFormat.Zstd);
        var corrupt = (byte[])valid.Clone();
        corrupt[^1] ^= 0xFF;

        Assert.ThrowsAny<Exception>(() => CompressionService.Decompress(corrupt, CompressionFormat.Zstd));
        Assert.Equal(Pattern(4096, 2), CompressionService.Decompress(valid, CompressionFormat.Zstd));
    }
}