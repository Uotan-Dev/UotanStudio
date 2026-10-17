using UotanStudio.Hash.RockchipCrc32;
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

    [Fact]
    public void Compute_RandomLengthsAndSeeds_MatchesBitwiseReference()
    {
        var random = new Random(0x5EED04C1);
        int[] lengths =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 23, 24, 31, 32, 33,
            63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 512, 513, 1023, 4096,
        ];
        uint[] seeds = [0u, 1u, 0x04C10DB7u, 0xDEADBEEFu, 0xFFFFFFFFu];

        foreach (var length in lengths)
        {
            var data = new byte[length];
            random.NextBytes(data);

            foreach (var seed in seeds)
            {
                Assert.Equal(BitwiseReference(data, seed), RockchipCrc32.Compute(data, seed));
            }
        }
    }

    [Fact]
    public void Compute_RandomSplitPoints_ContinuationMatchesSinglePass()
    {
        var random = new Random(unchecked((int)0xC0FFEE04u));
        var data = new byte[1031];
        random.NextBytes(data);

        foreach (var seed in new uint[] { 0u, 0xDEADBEEFu, 0xFFFFFFFFu })
        {
            foreach (var split in new[] { 0, 1, 3, 7, 8, 9, 15, 16, 17, 255, 256, 257, 1023, 1030, 1031 })
            {
                var single = RockchipCrc32.Compute(data, seed);
                var continued = RockchipCrc32.Compute(data.AsSpan(split), RockchipCrc32.Compute(data.AsSpan(0, split), seed));

                Assert.Equal(single, continued);
            }
        }
    }

    private static uint BitwiseReference(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C10DB7u : crc << 1;
            }
        }

        return crc;
    }
}