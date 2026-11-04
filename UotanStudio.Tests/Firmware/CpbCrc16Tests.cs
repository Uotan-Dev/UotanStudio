using UotanStudio.Hash.CpbCrc16;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors of the CPB container CRC-16 variant (32-bit untruncated accumulator over the CCITT 0x1021 table),
/// cross-checked against a table and fold loop written independently in this class, so the library's table build is
/// exercised rather than a single hard-coded digest.
/// <para>CPB 容器 CRC-16 变体（CCITT 0x1021 表上的 32 位不截断累加器）的黄金向量，与本类内独立书写的表与折叠循环
/// 互相校验，从而验证库内的查表构建，而不只是单个硬编码摘要。</para>
/// </summary>
public class CpbCrc16Tests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 23 + 9))];

    [Fact]
    public void Compute_GoldenVectors_MatchContainerChecksumSemantics()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];

        Assert.Equal(0x0000_0000u, CpbCrc16.Compute(ReadOnlySpan<byte>.Empty, 0));
        Assert.Equal(0x8690_31C3u, CpbCrc16.Compute("123456789"u8, 0));
        Assert.Equal(0x9B51_65E5u, CpbCrc16.Compute(payload, 0));
        Assert.Equal(0x0000_0000u, CpbCrc16.Compute(new byte[64], 0));
        Assert.Equal(0x9755_4619u, CpbCrc16.Compute("UotanStudio"u8, 0));
    }

    [Fact]
    public void Compute_ZeroSeed_FoldsZeroBytesIntoAnUnchangedAccumulator()
    {
        Assert.Equal(0u, CpbCrc16.Compute(new byte[100], 0));
    }

    [Fact]
    public void Compute_LowHalf_MatchesABitwiseCcittRegisterWalk()
    {
        var data = LongPayload.AsSpan(0, 257);

        var accumulation = CpbCrc16.Compute(data, 0);

        Assert.Equal(BitwiseCcittRegister(data), (ushort)accumulation);
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
    public void Compute_TableWalk_MatchesReferenceAccumulatorOverEveryPrefixLength(int length)
    {
        var data = LongPayload.AsSpan(0, length);

        Assert.Equal(ReferenceAccumulator(data, 0), CpbCrc16.Compute(data, 0));
        Assert.Equal(ReferenceAccumulator(data, 0x1234_5678u), CpbCrc16.Compute(data, 0x1234_5678u));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(2048)]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Compute_ChunksContinuedFromEveryOffset_EqualsSingleShotResult(int split)
    {
        var continuation = CpbCrc16.Compute(LongPayload.AsSpan(split), CpbCrc16.Compute(LongPayload.AsSpan(0, split), 0));

        Assert.Equal(CpbCrc16.Compute(LongPayload, 0), continuation);
        Assert.Equal(CpbCrc16.Compute(LongPayload.AsSpan(0, split), 0x0F0F_0F0Fu), CpbCrc16.Compute(LongPayload.AsSpan(0, split), 0x0F0F_0F0Fu));
    }

    /// <summary>
    /// Bit-at-a-time 16-bit CCITT register (poly 0x1021, MSB-first, no final XOR) the accumulator's low half must
    /// coincide with when started from zero.
    /// <para>逐位的 16 位 CCITT 寄存器（多项式 0x1021，高位优先，无最终异或），从零起步时累加器的低半部必须与其一致。</para>
    /// </summary>
    private static ushort BitwiseCcittRegister(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
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

    /// <summary>
    /// Reference accumulation over a table rebuilt through the bit-at-a-time route and folded with the same
    /// untruncated 32-bit step the container uses, written independently of the library's loop.
    /// <para>经逐位路线重建的表、并以容器使用的同一步进（32 位不截断折叠）做参考累加，独立于库内循环书写。</para>
    /// </summary>
    private static uint ReferenceAccumulator(ReadOnlySpan<byte> data, uint seed)
    {
        Span<ushort> table = stackalloc ushort[256];
        for (var index = 0; index < 256; index++)
        {
            ushort entry = (ushort)(index << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 0x8000) != 0 ? (ushort)((entry << 1) ^ 0x1021) : (ushort)(entry << 1);
            }

            table[index] = entry;
        }

        unchecked
        {
            var crc = seed;
            foreach (var value in data)
            {
                crc = (crc << 8) ^ table[(byte)(crc >> 8) ^ value];
            }

            return crc;
        }
    }
}