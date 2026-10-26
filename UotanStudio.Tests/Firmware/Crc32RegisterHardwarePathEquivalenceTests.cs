using Xunit;

using RegisterCrc32 = UotanStudio.Hash.Crc32Register.Crc32Register;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the hardware continuation path of the register-form CRC-32 against the portable slice-by-8 walk: on
/// processors without the ARM64 CRC extension, spans of at least 128 KiB reach <c>System.IO.Hashing.Crc32</c>
/// through the linearity identity <c>C(seed, data) == ~Crc32(data) ^ C(~seed, zeros^len)</c>, whose zero-run term is
/// a GF(2) matrix power cached by length. Random seeds, repeated block lengths (the cache path) and split
/// continuations over the threshold all must agree with the table walk bit for bit.
/// <para>固定寄存器形式 CRC-32 的硬件续算路径与可移植 slice-by-8 查表结果一致：在没有 ARM64 CRC 扩展的处理器上，
/// 长度不少于 128 KiB 的区间经线性性恒等式 <c>C(seed, data) == ~Crc32(data) ^ C(~seed, zeros^len)</c> 到达
/// <c>System.IO.Hashing.Crc32</c>，其中零串项是按长度缓存的 GF(2) 矩阵幂。随机 seed、重复的块长度（缓存路径）
/// 与跨阈值的分段续算都必须与查表结果逐位一致。</para>
/// </summary>
public class Crc32RegisterHardwarePathEquivalenceTests
{
    private const int HardwareContinuationMinimum = 128 * 1024;

    private static readonly byte[] Payload = [.. Enumerable.Range(0, 600_000).Select(index => (byte)(index * 71 + 29))];

    public static TheoryData<uint> Seeds => new()
    {
        0xFFFF_FFFFu,
        0x0000_0000u,
        0xDEAD_BEEFu,
        0x0303_0303u,
        0xF2F2_F2F2u,
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Compute_AnySeedAboveThreshold_MatchesPortableTableWalk(uint seed)
    {
        var data = Payload.AsSpan(0, 200_000);

        Assert.Equal(RegisterCrc32.ComputeSoftware(data, seed), RegisterCrc32.Compute(data, seed));
    }

    [Theory]
    [InlineData(HardwareContinuationMinimum - 1)]
    [InlineData(HardwareContinuationMinimum)]
    [InlineData(HardwareContinuationMinimum + 1)]
    [InlineData(200_000)]
    [InlineData(599_999)]
    public void Compute_ThresholdBoundaryLengths_MatchPortableTableWalk(int length)
    {
        var data = Payload.AsSpan(0, length);

        Assert.Equal(RegisterCrc32.ComputeSoftware(data, 0xA5A5_5A5Au), RegisterCrc32.Compute(data, 0xA5A5_5A5Au));
    }

    [Fact]
    public void Compute_RepeatedBlockLength_ReusesCachedMatrixWithoutDrift()
    {
        uint carried = 0x1234_5678u;
        uint reference = 0x1234_5678u;
        for (var block = 0; block < 4; block++)
        {
            var data = Payload.AsSpan(block * 150_000, 150_000);
            carried = RegisterCrc32.Compute(data, carried);
            reference = RegisterCrc32.ComputeSoftware(data, reference);
        }

        Assert.Equal(reference, carried);
    }

    [Fact]
    public void Compute_SplitContinuationAcrossThreshold_EqualsSinglePortablePass()
    {
        var first = Payload.AsSpan(0, 300_000);
        var rest = Payload.AsSpan(300_000);

        var continued = RegisterCrc32.Compute(first, 0xC0C0_C0C0u);
        continued = RegisterCrc32.Compute(rest, continued);

        Assert.Equal(RegisterCrc32.ComputeSoftware(Payload, 0xC0C0_C0C0u), continued);
    }
}