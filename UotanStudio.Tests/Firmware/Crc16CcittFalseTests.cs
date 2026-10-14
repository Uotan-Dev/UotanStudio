using UotanStudio.Hash.Crc16CcittFalse;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors of the non-reflected CRC-16 (high-bit-first polynomial 0x1021, init 0xFFFF, no final XOR) cross-checked
/// against a bit-by-bit loop written in this class, so the 256-entry table build is exercised rather than a single
/// hard-coded digest.
/// <para>非反射式 CRC-16（高位优先多项式 0x1021，初值 0xFFFF，无最终异或）的黄金向量，与本类内书写的逐位循环互相校验，
/// 从而验证 256 项查表构建，而不只是单个硬编码摘要。</para>
/// </summary>
public class Crc16CcittFalseTests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 23 + 9))];

    [Fact]
    public void Compute_UpstreamGoldenVectors_MatchReferenceCheckValues()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];

        Assert.Equal((ushort)0x29B1, Crc16CcittFalse.Compute("123456789"u8));
        Assert.Equal((ushort)0x0FEF, Crc16CcittFalse.Compute(payload));
        Assert.Equal((ushort)0xFFFF, Crc16CcittFalse.Compute(ReadOnlySpan<byte>.Empty));
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

        Assert.Equal(ReferenceCrc16(data), Crc16CcittFalse.Compute(data));
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

        Assert.Equal(Crc16CcittFalse.Compute(LongPayload), Crc16CcittFalse.Compute(rejoined));
        Assert.Equal(ReferenceCrc16(LongPayload[..split]), Crc16CcittFalse.Compute(LongPayload.AsSpan(0, split)));
        Assert.Equal(ReferenceCrc16(LongPayload[split..]), Crc16CcittFalse.Compute(rejoined.AsSpan(split)));
    }

    private static ushort ReferenceCrc16(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}