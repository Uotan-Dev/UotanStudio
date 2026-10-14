using UotanStudio.Hash.Crc16CcittReversed;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors of the reflected CRC-16 (low-bit-first polynomial 0x8408, init 0xFFFF, final XOR 0xFFFF) cross-checked
/// against a bit-by-bit loop written in this class, so the 256-entry table build is exercised rather than a single
/// hard-coded digest.
/// <para>反射式 CRC-16（低位优先多项式 0x8408，初值 0xFFFF，最终异或 0xFFFF）的黄金向量，与本类内书写的逐位循环互相校验，
/// 从而验证 256 项查表构建，而不只是单个硬编码摘要。</para>
/// </summary>
public class Crc16CcittReversedTests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 17 + 5))];

    [Fact]
    public void Compute_UpstreamGoldenVectors_MatchReferenceCheckValues()
    {
        Assert.Equal((ushort)0x906E, Crc16CcittReversed.Compute("123456789"u8));
        Assert.Equal((ushort)0x4EAB, Crc16CcittReversed.Compute([0x0B]));
        Assert.Equal((ushort)0x0000, Crc16CcittReversed.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(4096)]
    public void Compute_TableWalk_MatchesBitByBitReferenceOverEveryPrefixLength(int length)
    {
        var data = LongPayload.AsSpan(0, length);

        Assert.Equal(ReferenceCrc16(data), Crc16CcittReversed.Compute(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(2048)]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Compute_ChunksRejoinedFromEveryOffset_EqualsSingleShotResult(int split)
    {
        byte[] rejoined = [.. LongPayload[..split], .. LongPayload[split..]];

        Assert.Equal(Crc16CcittReversed.Compute(LongPayload), Crc16CcittReversed.Compute(rejoined));
        Assert.Equal(ReferenceCrc16(LongPayload[..split]), Crc16CcittReversed.Compute(LongPayload.AsSpan(0, split)));
        Assert.Equal(ReferenceCrc16(LongPayload[split..]), Crc16CcittReversed.Compute(rejoined.AsSpan(split)));
    }

    private static ushort ReferenceCrc16(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            }
        }

        return (ushort)(crc ^ 0xFFFF);
    }
}