using UotanStudio.Hash.Crc16Xmodem;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors of the non-reflected CRC-16 with a zero seed (high-bit-first polynomial 0x1021, init 0x0000, no
/// final XOR) cross-checked against a bit-by-bit loop written in this class, so the 256-entry table build is
/// exercised rather than a single hard-coded digest.
/// <para>零初值非反射式 CRC-16（高位优先多项式 0x1021，初值 0x0000，无最终异或）的黄金向量，与本类内书写的逐位循环
/// 互相校验，从而验证 256 项查表构建，而不只是单个硬编码摘要。</para>
/// </summary>
public class Crc16XmodemTests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 29 + 5))];

    [Fact]
    public void Compute_BlackBoxCheckValue_MatchesXmodemParameters()
    {
        Assert.Equal((ushort)0x31C3, Crc16Xmodem.Compute("123456789"u8));
        Assert.Equal((ushort)0x0000, Crc16Xmodem.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compute_DiffersFromCcittFalseSeed_OnlyByInitialRegister()
    {
        var data = "123456789"u8;

        Assert.Equal(CrcUnseeded(0x0000, data), Crc16Xmodem.Compute(data));
        Assert.Equal((ushort)0x29B1, CrcUnseeded(0xFFFF, data));
        Assert.NotEqual(CrcUnseeded(0xFFFF, data), Crc16Xmodem.Compute(data));
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

        Assert.Equal(ReferenceCrc16(data), Crc16Xmodem.Compute(data));
    }

    private static ushort CrcUnseeded(ushort seed, ReadOnlySpan<byte> data)
    {
        var crc = seed;
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

    private static ushort ReferenceCrc16(ReadOnlySpan<byte> data) => CrcUnseeded(0x0000, data);
}