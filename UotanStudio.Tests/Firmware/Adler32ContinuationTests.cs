using UotanStudio.Hash.Adler32;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors and chunked continuation of Adler-32: folding a chunk into the packed running value must reproduce
/// the one-shot checksum of the concatenated bytes, including splits that straddle the internal 5552-byte blocking.
/// <para>Adler-32 的黄金向量与分块续算：把数据块并入打包的累进值，必须复现拼接字节的一次性校验和，包括跨越内部
/// 5552 字节分块边界的切分。</para>
/// </summary>
public class Adler32ContinuationTests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 20000).Select(index => (byte)(index * 37 + 11))];

    [Fact]
    public void Compute_UpstreamGoldenVectors_MatchReferenceCheckValues()
    {
        Assert.Equal(0x091E01DEu, Adler32.Compute("123456789"u8));
        Assert.Equal(0x00620062u, Adler32.Compute("a"u8));
        Assert.Equal(0x024D0127u, Adler32.Compute("abc"u8));
        Assert.Equal(0x00000001u, Adler32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5551)]
    [InlineData(5552)]
    [InlineData(5553)]
    [InlineData(10000)]
    [InlineData(11104)]
    [InlineData(19999)]
    [InlineData(20000)]
    public void Compute_ChunkedContinuationAtOffset_EqualsSingleShotResult(int offset)
    {
        var running = Adler32.Compute(LongPayload.AsSpan(0, offset));
        var folded = Adler32.Compute(LongPayload.AsSpan(offset), running);

        Assert.Equal(Adler32.Compute(LongPayload), folded);
    }

    [Fact]
    public void Compute_ThreeWayContinuation_EqualsSingleShotResult()
    {
        var first = Adler32.Compute(LongPayload.AsSpan(0, 1));
        var second = Adler32.Compute(LongPayload.AsSpan(1, 5551), first);
        var third = Adler32.Compute(LongPayload.AsSpan(5552), second);

        Assert.Equal(Adler32.Compute(LongPayload), third);
    }

    [Fact]
    public void Compute_InitialPackedValue_ReproducesTheSingleShotChecksum()
    {
        Assert.Equal(Adler32.Compute("123456789"u8), Adler32.Compute("123456789"u8, 0x00000001u));
        Assert.Equal(Adler32.Compute(LongPayload), Adler32.Compute(LongPayload, 0x00000001u));
    }

    [Fact]
    public void Compute_EmptyChunk_ReturnsPreviousResultUnchanged()
    {
        Assert.Equal(0xDEADBEEFu, Adler32.Compute(ReadOnlySpan<byte>.Empty, 0xDEADBEEFu));
        Assert.Equal(0x091E01DEu, Adler32.Compute(ReadOnlySpan<byte>.Empty, Adler32.Compute("123456789"u8)));
    }
}