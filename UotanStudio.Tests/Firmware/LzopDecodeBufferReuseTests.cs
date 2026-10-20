using UotanStudio.Compress;
using UotanStudio.Compress.Compressors;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the pooled LZOP decode buffer: blocks are decoded on worker threads and their buffers recycled between
/// batches, so a reuse mistake would either corrupt a later block or throw away a buffer that is still being written.
/// <para>固定池化的 LZOP 解码缓冲：块在工作线程上解码且缓冲在批次之间回收，因此复用出错要么损坏后续块，要么把仍在写入的
/// 缓冲提前归还。</para>
/// </summary>
public class LzopDecodeBufferReuseTests
{
    private static byte[] Pattern(int length, int salt)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index % 97) + salt);
        }

        return data;
    }

    [Theory]
    [InlineData(256 * 1024)]
    [InlineData(256 * 1024 + 1)]
    [InlineData(3 * 256 * 1024)]
    [InlineData(3 * 256 * 1024 + 12345)]
    public void RoundTrip_MultiBlockCompressibleInput_MatchesOriginal(int length)
    {
        var data = Pattern(length, 13);
        var encoded = LzopCompressor.Compress(data);
        Assert.Equal(data, LzopCompressor.Decompress(encoded));
    }

    [Fact]
    public void Decompress_TruncatedBlocks_ThrowsAndKeepsLaterDecodesIntact()
    {
        var data = Pattern(3 * 256 * 1024, 29);
        var encoded = LzopCompressor.Compress(data);
        var truncated = encoded.AsSpan(0, encoded.Length - 4096).ToArray();

        Assert.Throws<CompressionException>(() => LzopCompressor.Decompress(truncated));
        Assert.Equal(data, LzopCompressor.Decompress(encoded));
    }

    [Fact]
    public void Decompress_RepeatedCalls_ProduceIdenticalOutput()
    {
        var data = Pattern(2 * 256 * 1024, 41);
        var encoded = LzopCompressor.Compress(data);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            Assert.Equal(data, LzopCompressor.Decompress(encoded));
        }
    }

    [Fact]
    public void RoundTrip_LargeMultiBlockInputBeyondOutputSoftLimit_MatchesOriginal()
    {
        var data = Pattern(8 * 1024 * 1024, 19);
        var encoded = LzopCompressor.Compress(data);
        Assert.Equal(data, LzopCompressor.Decompress(encoded));
    }

    [Fact]
    public void Decompress_OutputLimitBelowDecodedTotal_Throws()
    {
        var data = Pattern(512 * 1024, 23);
        var encoded = LzopCompressor.Compress(data);
        Assert.Throws<CompressionException>(() => LzopCompressor.Decompress(encoded, 1024));
    }

    [Fact]
    public void Decompress_StoredAndCompressedBlocksMixed_RoundTrips()
    {
        var stored = new byte[256 * 1024];
        for (var index = 0; index < stored.Length; index++)
        {
            stored[index] = (byte)((index * 151 + index / 7) & 0xFF);
        }

        var compressible = Pattern(256 * 1024, 7);
        var mixed = new byte[stored.Length + compressible.Length];
        stored.CopyTo(mixed, 0);
        compressible.CopyTo(mixed, stored.Length);

        var encoded = LzopCompressor.Compress(mixed);
        Assert.Equal(mixed, LzopCompressor.Decompress(encoded));
    }
}