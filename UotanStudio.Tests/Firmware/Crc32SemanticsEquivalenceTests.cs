using Xunit;

using BclCrc32 = System.IO.Hashing.Crc32;
using RegisterCrc32 = UotanStudio.Hash.Crc32Register.Crc32Register;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the exact relationship between the standard CRC-32 register form (initial value <c>0xFFFFFFFF</c> with a
/// final complement, IEEE 802.3 / ZIP / GPT / sparse semantics) and <c>UotanStudio.Hash</c>'s no-final-XOR register
/// form, so a future consolidation can forward one onto the other without silently flipping an on-disk field.
/// <para>固定“标准 CRC-32 寄存器形式（初值 <c>0xFFFFFFFF</c> 并做最终取反，即 IEEE 802.3 / ZIP / GPT / sparse 语义）”
/// 与 <c>UotanStudio.Hash</c> 的“无末异或寄存器形式”之间的精确关系，使后续统一收拢时可以在两者间转发，
/// 而不会静默改写落盘字段。</para>
/// </summary>
/// <remarks>
/// The complement identity is <c>BCL(d) == ~Hash.Compute(d, 0xFFFFFFFF)</c>. Chunk sizes here mirror the production
/// streaming sites: 4096 filesystem blocks, 64 KiB sparse and ADB buffers, 128 KiB AMLogic reads and 1 MiB payload
/// operations.
/// <para>取反恒等式为 <c>BCL(d) == ~Hash.Compute(d, 0xFFFFFFFF)</c>。这里的分块尺寸对应生产环境的流式站点：
/// 4096 字节文件系统块、64 KiB sparse 与 ADB 缓冲、128 KiB AMLogic 读取、1 MiB Payload 操作。</para>
/// </remarks>
public class Crc32SemanticsEquivalenceTests
{
    private const uint RegisterInitialValue = 0xFFFF_FFFF;

    private static readonly byte[] Payload = [.. Enumerable.Range(0, 300_000).Select(index => (byte)(index * 37 + 11))];

    [Fact]
    public void HashToUInt32_ReferenceCheckVector_MatchesIeeeGoldenValue()
    {
        Assert.Equal(0xCBF43926u, BclCrc32.HashToUInt32("123456789"u8));
    }

    [Fact]
    public void Compute_ReferenceCheckVector_ProducesComplementOfIeeeGoldenValue()
    {
        Assert.Equal(~0xCBF43926u, RegisterCrc32.Compute("123456789"u8, RegisterInitialValue));
    }

    [Fact]
    public void HashToUInt32_EmptyInput_ReturnsZeroBecauseFinalComplementCollapsesSeed()
    {
        Assert.Equal(0u, BclCrc32.HashToUInt32(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compute_EmptyInput_ReturnsSeedUnchangedSoSentinelsDifferFromStandardForm()
    {
        Assert.Equal(RegisterInitialValue, RegisterCrc32.Compute(ReadOnlySpan<byte>.Empty, RegisterInitialValue));
        Assert.Equal(0u, RegisterCrc32.Compute(ReadOnlySpan<byte>.Empty, 0u));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(65_536)]
    [InlineData(131_072)]
    [InlineData(262_144)]
    [InlineData(299_999)]
    [InlineData(300_000)]
    public void HashToUInt32_AnyLength_IsBitwiseComplementOfRegisterForm(int length)
    {
        var data = Payload.AsSpan(0, length);

        Assert.Equal(~RegisterCrc32.Compute(data, RegisterInitialValue), BclCrc32.HashToUInt32(data));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65_536)]
    [InlineData(131_072)]
    [InlineData(1_048_576)]
    public void HashToUInt32_RegistrationChunkSizes_KeepComplementIdentityAcrossSplits(int chunkLength)
    {
        var register = RegisterInitialValue;
        for (var offset = 0; offset < Payload.Length; offset += chunkLength)
        {
            register = RegisterCrc32.Compute(Payload.AsSpan(offset, Math.Min(chunkLength, Payload.Length - offset)), register);
        }

        Assert.Equal(~register, BclCrc32.HashToUInt32(Payload));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65_536)]
    [InlineData(131_072)]
    [InlineData(1_048_576)]
    public void GetCurrentHashAsUInt32_AppendedChunks_EqualsOneShotHashToUInt32(int chunkLength)
    {
        var accumulator = new BclCrc32();
        for (var offset = 0; offset < Payload.Length; offset += chunkLength)
        {
            accumulator.Append(Payload.AsSpan(offset, Math.Min(chunkLength, Payload.Length - offset)));
        }

        Assert.Equal(BclCrc32.HashToUInt32(Payload), accumulator.GetCurrentHashAsUInt32());
    }

    [Fact]
    public void GetCurrentHashAsUInt32_FreshAccumulatorWithoutAppends_MatchesEmptyInputStandardForm()
    {
        Assert.Equal(BclCrc32.HashToUInt32(ReadOnlySpan<byte>.Empty), new BclCrc32().GetCurrentHashAsUInt32());
    }

    [Fact]
    public void Reset_AccumulatorReusedForSecondPayload_MatchesFreshAccumulator()
    {
        var accumulator = new BclCrc32();
        accumulator.Append(Payload.AsSpan(0, 50_000));
        _ = accumulator.GetCurrentHashAsUInt32();

        accumulator.Reset();
        accumulator.Append(Payload.AsSpan(120_000, 50_000));

        Assert.Equal(BclCrc32.HashToUInt32(Payload.AsSpan(120_000, 50_000)), accumulator.GetCurrentHashAsUInt32());
    }

    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4096)]
    [InlineData(131_072)]
    [InlineData(299_999)]
    public void Compute_AllOnesSeedAtFastPathBoundary_MatchesIeeeComplement(int length)
    {
        var data = Payload.AsSpan(0, length);

        Assert.Equal(~RegisterCrc32.Compute(data, RegisterInitialValue), BclCrc32.HashToUInt32(data));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(131_072)]
    public void Compute_AllOnesSeededStreamingChunks_FastPathComposesWithSliceFallback(int chunkLength)
    {
        var register = RegisterInitialValue;
        for (var offset = 0; offset < Payload.Length; offset += chunkLength)
        {
            register = RegisterCrc32.Compute(Payload.AsSpan(offset, Math.Min(chunkLength, Payload.Length - offset)), register);
        }

        Assert.Equal(~register, BclCrc32.HashToUInt32(Payload));
    }

    [Fact]
    public void Compute_FormatSpecificSeed_IsNotExpressibleByStandardFormSoBothFamiliesStay()
    {
        const uint f2fsSuperblockMagicSeed = 0xF2F5_2010;
        var data = Payload.AsSpan(0, 4096);

        var seeded = RegisterCrc32.Compute(data, f2fsSuperblockMagicSeed);

        Assert.NotEqual(BclCrc32.HashToUInt32(data), ~seeded);
        Assert.NotEqual(seeded, RegisterCrc32.Compute(data, RegisterInitialValue));
    }
}