using UotanStudio.Firmware.Core.Checksums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors and chunked-continuation semantics of the Rockchip CRC-32 (poly 0x04C10DB7,
/// MSB-first, init 0, no final XOR).
/// <para>Rockchip CRC-32（多项式 0x04C10DB7，高位优先，初值 0，无最终异或）的黄金向量与分块续算语义。</para>
/// </summary>
public class RockchipCrc32Tests
{
    [Fact]
    public void Compute_UpstreamGoldenVectors_MatchReferenceTableImplementation()
    {
        Assert.Equal(0x889A9615u, RockchipCrc32.Compute("123456789"u8));
        Assert.Equal(0xA862DB20u, RockchipCrc32.Compute("a"u8));
        Assert.Equal(0x3A06543Eu, RockchipCrc32.Compute("abc"u8));
        Assert.Equal(0x00000000u, RockchipCrc32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x00000000u, RockchipCrc32.Compute(new byte[1024]));
    }

    [Fact]
    public void Compute_ChunkedSeedContinuation_EqualsSingleShotResult()
    {
        var data = "123456789"u8.ToArray();

        var first = RockchipCrc32.Compute(data.AsSpan(0, 4));
        var combined = RockchipCrc32.Compute(data.AsSpan(4), first);

        Assert.Equal(RockchipCrc32.Compute(data), combined);
    }

    [Fact]
    public void Compute_EmptySpan_ReturnsSeedUnchanged()
    {
        Assert.Equal(0xDEADBEEFu, RockchipCrc32.Compute(ReadOnlySpan<byte>.Empty, 0xDEADBEEFu));
    }
}