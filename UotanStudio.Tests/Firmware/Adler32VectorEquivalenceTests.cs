using UotanStudio.Hash.Adler32;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the equality of <c>UotanStudio.Hash</c>'s three Adler-32 paths: the public entry point, the portable byte-at-a-time
/// reference, and the SSSE3 block form. A divergence between them would silently change a DEX or LZOP checksum field
/// depending on which processor the build happens to run on.
/// <para>固定 <c>UotanStudio.Hash</c> 的三种 Adler-32 路径之间的相等性：公开入口、可移植逐字节参考实现、SSSE3 分块形态。
/// 三者一旦分歧，同一个 DEX 或 LZOP 校验和字段会随构建机器处理器不同而静默改变。</para>
/// </summary>
/// <remarks>
/// The lengths cover the 16/32-byte vector width, its neighbours, the <c>5552</c>-byte reduction block and two
/// neighbouring block boundaries, so an off-by-one in the tail loop or in the block boundary shows up as a value
/// difference rather than staying latent.
/// <para>长度覆盖 16/32 字节向量宽度及其相邻值、<c>5552</c> 字节归约块以及两个相邻块边界，因此尾部循环或块边界处的
/// 差一错误会直接表现为数值不同，而不是潜伏下来。</para>
/// </remarks>
public class Adler32VectorEquivalenceTests
{
    private static readonly int[] BoundaryLengths =
    [
        0, 1, 2, 3, 15, 16, 17, 31, 32, 33, 47, 48, 63, 64, 65, 95, 96, 127, 128, 129,
        4096, 4097, 5551, 5552, 5553, 11103, 11104, 11105, 65535, 65536, 300_000
    ];

    [Fact]
    public void Compute_ReferenceCheckVector_MatchesZlibGoldenValue()
    {
        Assert.Equal(0x091E01DEu, Adler32.Compute("123456789"u8));
        Assert.Equal(0x091E01DEu, Adler32.ComputeScalar("123456789"u8));
        Assert.Equal(0x091E01DEu, Adler32.ComputeVectorized("123456789"u8));
    }

    [Fact]
    public void Compute_EmptyInput_ReturnsInitialValue()
    {
        Assert.Equal(1u, Adler32.Compute([]));
        Assert.Equal(1u, Adler32.ComputeScalar([]));
        Assert.Equal(1u, Adler32.ComputeVectorized([]));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void Compute_EveryPath_AgreeWithIndependentOracleAndWithEachOther(int length)
    {
        var data = Pattern(length);

        var oracle = Oracle(data);
        var publicPath = Adler32.Compute(data);
        var scalarPath = Adler32.ComputeScalar(data);
        var vectorPath = Adler32.ComputeVectorized(data);

        Assert.Equal(oracle, publicPath);
        Assert.Equal(oracle, scalarPath);
        Assert.Equal(oracle, vectorPath);
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void ComputeWithSeed_SplitChunks_MatchesSinglePass(int length)
    {
        var data = Pattern(length);
        var split = length / 3;
        var first = Adler32.Compute(data[..split]);
        var stitched = Adler32.Compute(data[split..], first);
        Assert.Equal(Adler32.Compute(data), stitched);
    }

    [Fact]
    public void ComputeArraySlice_OffsetAndLength_MatchesSpanOverload()
    {
        var buffer = new byte[4096 + 777];
        Pattern(buffer.Length).CopyTo(buffer, 0);
        var slice = buffer.AsSpan(4096, 777);
        Assert.Equal(Adler32.Compute(slice), Adler32.Compute(buffer, 4096, 777));
        Assert.Equal(Adler32.ComputeScalar(slice), Adler32.Compute(buffer, 4096, 777));
    }

    [Fact]
    public void ComputeWithSeed_EmptyContinuation_ReturnsPreviousResult()
    {
        var seed = Adler32.Compute(Pattern(1234));
        Assert.Equal(seed, Adler32.Compute([], seed));
    }

    public static TheoryData<int> LengthCases()
    {
        var cases = new TheoryData<int>();
        foreach (var length in BoundaryLengths)
        {
            cases.Add(length);
        }

        return cases;
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 37 + 11) & 0xFF);
        }

        return data;
    }

    private static uint Oracle(ReadOnlySpan<byte> data)
    {
        ulong a = 1;
        ulong b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (uint)((b << 16) | a);
    }
}