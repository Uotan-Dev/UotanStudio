using UotanStudio.Compress;
using UotanStudio.Compress.Compressors;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the two LZMA encoder profiles against each other: the array overload relies on the LZMA-SDK default coder
/// properties while the stream overload sets its own, so a pooled encoder leaking one profile's properties into the
/// other would silently change the encoded bytes of every later call.
/// <para>固定 LZMA 的两个编码器 profile 之间的隔离：数组重载依赖 LZMA-SDK 默认编码属性，流重载自带属性，因此池化实例若把
/// 一个 profile 的属性泄漏给另一个，之后每次调用的编码字节都会静默改变。</para>
/// </summary>
/// <remarks>
/// The decisive assertions are the before/after comparisons: the same overload must produce identical bytes whether or
/// not the other overload ran in between. A property leak changes exactly that.
/// <para>关键断言是前后对照：同一重载无论中间是否运行过另一重载，都必须产出相同字节。属性泄漏恰好会破坏这一点。</para>
/// </remarks>
public class LzmaContextReuseTests
{
    private static byte[] Pattern(int length, int salt)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index % 137) + salt);
        }

        return data;
    }

    [Fact]
    public void Compress_ArrayPathAroundStreamPath_ProducesIdenticalBytes()
    {
        var data = Pattern(64 * 1024, 5);
        var first = LzmaCompressor.Compress(data);

        using (var input = new MemoryStream(data))
        using (var output = new MemoryStream())
        {
            LzmaCompressor.Compress(input, output);
            Assert.NotEmpty(output.ToArray());
        }

        var second = LzmaCompressor.Compress(data);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compress_StreamPathAroundArrayPath_ProducesIdenticalBytes()
    {
        var data = Pattern(32 * 1024, 9);

        byte[] StreamEncode()
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            LzmaCompressor.Compress(input, output);
            return output.ToArray();
        }

        var first = StreamEncode();
        _ = LzmaCompressor.Compress(data);
        var second = StreamEncode();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compress_RepeatedCalls_RoundTrip()
    {
        var data = Pattern(100_000, 3);
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var encoded = LzmaCompressor.Compress(data);
            Assert.Equal(data, LzmaCompressor.Decompress(encoded));
        }
    }

    [Fact]
    public void Compress_ConcurrentCallers_RoundTripIndependently()
    {
        var payloads = Enumerable.Range(0, 6).Select(index => Pattern(48 * 1024, index)).ToArray();
        Parallel.For(0, payloads.Length, index =>
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var encoded = LzmaCompressor.Compress(payloads[index]);
                Assert.Equal(payloads[index], LzmaCompressor.Decompress(encoded));
            }
        });
    }

    [Fact]
    public void Decompress_TruncatedPayload_ThrowsThenPooledDecoderStillWorks()
    {
        var data = Pattern(8192, 11);
        var encoded = LzmaCompressor.Compress(data);
        var truncated = encoded.AsSpan(0, encoded.Length / 2).ToArray();

        Assert.Throws<CompressionException>(() => LzmaCompressor.Decompress(truncated));
        Assert.Equal(data, LzmaCompressor.Decompress(encoded));
    }

    [Fact]
    public void Compress_ViaCompressionService_RoundTrips()
    {
        var data = Pattern(256 * 1024, 17);
        var encoded = CompressionService.Compress(data, CompressionFormat.Lzma);
        Assert.Equal(data, CompressionService.Decompress(encoded, CompressionFormat.Lzma));
    }
}