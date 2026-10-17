using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Known values and modular wrap behavior of the byte additive checksums used by ADB, CPIO, TAR, HDC and MediaTek
/// frames, compared against scalar accumulators over lengths that straddle vector lanes and partial tails.
/// <para>ADB、CPIO、TAR、HDC 与 MediaTek 帧所用逐字节累加校验和的已知值与按模回卷行为，在跨越向量通道与
/// 非整块尾部的长度上与标量累加器对照。</para>
/// </summary>
public class ByteSumTests
{
    [Fact]
    public void Compute_KnownShortVectors_ProduceScalarWrappedSums()
    {
        byte[] bytes = [0xFF, 0x02, 0x03];

        Assert.Equal((byte)0x04, ByteSum8.Compute(bytes));
        Assert.Equal((ushort)0x0104, ByteSum16.Compute(bytes));
        Assert.Equal(0x0000_0104u, ByteSum32.Compute(bytes));
        Assert.Equal((byte)0x00, ByteSum8.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0x0000, ByteSum16.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0u, ByteSum32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compute_LargeAllOnesPayload_WrapsEachWidthAtItsOwnModulus()
    {
        var data = new byte[70_001];
        Array.Fill(data, (byte)0xFF);

        Assert.Equal((byte)143, ByteSum8.Compute(data));
        Assert.Equal((ushort)0x5F8F, ByteSum16.Compute(data));
        Assert.Equal(0x0110_5F8Fu, ByteSum32.Compute(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(100_003)]
    public void Compute_RandomPayloads_MatchScalarAccumulators(int length)
    {
        var random = new Random(0x5EED5A17 + length);
        var data = new byte[length];
        random.NextBytes(data);

        Assert.Equal(Scalar8(data), ByteSum8.Compute(data));
        Assert.Equal(Scalar16(data), ByteSum16.Compute(data));
        Assert.Equal(Scalar32(data), ByteSum32.Compute(data));
    }

    private static byte Scalar8(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return sum;
    }

    private static ushort Scalar16(ReadOnlySpan<byte> data)
    {
        ushort sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return sum;
    }

    private static uint Scalar32(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return sum;
    }
}