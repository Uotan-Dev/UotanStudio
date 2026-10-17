using UotanStudio.Hash.Crc16Arc;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors and chunked-continuation semantics of the PAC CRC-16 (low-bit-first polynomial 0xA001, caller-seeded,
/// no final XOR), cross-checked against a bit-by-bit loop written in this class so the derived slice tables are
/// exercised for arbitrary seeds and partial tails.
/// <para>PAC CRC-16（低位优先多项式 0xA001，调用方给 seed，无最终异或）的黄金向量与分块续算语义，
/// 与本类内书写的逐位循环互相校验，从而在任意 seed 与非整块尾部上检验派生出的 slice 表。</para>
/// </summary>
public class Crc16ArcTests
{
    [Fact]
    public void Compute_UpstreamGoldenVector_MatchesReferenceCheckValue()
    {
        Assert.Equal((ushort)0xBB3D, Crc16Arc.Compute("123456789"u8, 0));
    }

    [Fact]
    public void Compute_EmptySpan_ReturnsSeedUnchanged()
    {
        Assert.Equal((ushort)0xBEEF, Crc16Arc.Compute(ReadOnlySpan<byte>.Empty, 0xBEEF));
    }

    [Fact]
    public void Compute_RandomLengthsAndSeeds_MatchesBitwiseReference()
    {
        var random = new Random(0x5EEDA001);
        int[] lengths =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 23, 24, 31, 32, 33,
            63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 512, 513, 1023, 4096,
        ];
        ushort[] seeds = [0, 1, 0x8005, 0xA001, 0xBEEF, 0xFFFF];

        foreach (var length in lengths)
        {
            var data = new byte[length];
            random.NextBytes(data);

            foreach (var seed in seeds)
            {
                Assert.Equal(BitwiseReference(data, seed), Crc16Arc.Compute(data, seed));
            }
        }
    }

    [Fact]
    public void Compute_RandomSplitPoints_ContinuationMatchesSinglePass()
    {
        var random = new Random(unchecked((int)0xC0FFEEA0u));
        var data = new byte[1031];
        random.NextBytes(data);

        foreach (var seed in new ushort[] { 0, 0xBEEF, 0xFFFF })
        {
            foreach (var split in new[] { 0, 1, 3, 7, 8, 9, 15, 16, 17, 255, 256, 257, 1023, 1030, 1031 })
            {
                var single = Crc16Arc.Compute(data, seed);
                var continued = Crc16Arc.Compute(data.AsSpan(split), Crc16Arc.Compute(data.AsSpan(0, split), seed));

                Assert.Equal(single, continued);
            }
        }
    }

    private static ushort BitwiseReference(ReadOnlySpan<byte> data, ushort seed)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }
}