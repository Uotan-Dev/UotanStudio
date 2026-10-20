using UotanStudio.Firmware.BootSecurity.Avb;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the equality of the two GF(256) multiply-accumulate forms used by AVB FEC repair: the nibble-split vector form
/// and the byte-at-a-time reference. Both are reachable in production depending on whether the processor can permute a
/// 16-byte lane in one instruction, so a divergence would corrupt repaired partitions on exactly one class of machine.
/// <para>固定 AVB FEC 修复所使用的两种 GF(256) 乘加形态之间的相等性：nibble-split 向量形态与逐字节参考形态。生产环境按处理器
/// 能否用单条指令置换 16 字节通道分别走这两条路径，一旦分歧，修复出的分区会在其中一类机器上损坏。</para>
/// </summary>
/// <remarks>
/// The repair end-to-end cases sweep wider root counts than the existing repair tests so the residual, output and check
/// rows all run through long vector blocks, and every result is compared against the untouched source bytes.
/// <para>修复端到端用例扫描了比既有修复测试更宽的校验根数，使残差行、输出行与校验行都经过长向量块，并把每个结果与未受影响的
/// 源字节逐字节比对。</para>
/// </remarks>
public class AvbFecVectorMultiplicationTests
{
    private const int Block = 4096;

    private static readonly int[] RowLengths =
    [
        0, 1, 2, 15, 16, 17, 31, 32, 33, 47, 48, 63, 64, 65, 127, 128, 129, 255, 256, 1000, Block
    ];

    private static readonly byte[] Coefficients = [0, 1, 2, 3, 7, 16, 17, 128, 129, 200, 255];

    [Theory]
    [MemberData(nameof(CaseMatrix))]
    public void XorMultiplyConstant_EveryCoefficientAndRowLength_MatchesScalarReference(int length, byte coefficient)
    {
        var source = Pattern(length, 17);
        var seed = Pattern(length, 91);

        var vectorPath = (byte[])seed.Clone();
        var scalarPath = (byte[])seed.Clone();

        AvbFecCodec.XorMultiplyConstant(vectorPath, source, coefficient);
        AvbFecCodec.XorMultiplyConstantScalar(scalarPath, source, coefficient);

        Assert.Equal(scalarPath, vectorPath);
    }

    [Fact]
    public void XorMultiplyConstant_ZeroCoefficient_LeavesDestinationUntouched()
    {
        var source = Pattern(Block, 5);
        var destination = Pattern(Block, 77);
        var before = (byte[])destination.Clone();

        AvbFecCodec.XorMultiplyConstant(destination, source, 0);

        Assert.Equal(before, destination);
    }

    [Fact]
    public void XorMultiplyConstant_RepeatedAccumulation_MatchesSummedSinglePass()
    {
        var first = Pattern(Block, 3);
        var second = Pattern(Block, 4);
        var coefficient = (byte)131;

        var accumulated = new byte[Block];
        AvbFecCodec.XorMultiplyConstant(accumulated, first, coefficient);
        AvbFecCodec.XorMultiplyConstant(accumulated, second, coefficient);

        var reference = new byte[Block];
        for (var index = 0; index < Block; index++)
        {
            AvbFecCodec.XorMultiplyConstant(reference.AsSpan(index, 1), first.AsSpan(index, 1), coefficient);
            AvbFecCodec.XorMultiplyConstant(reference.AsSpan(index, 1), second.AsSpan(index, 1), coefficient);
        }

        Assert.Equal(reference, accumulated);
    }

    [Theory]
    [InlineData(16u, 1)]
    [InlineData(16u, 8)]
    [InlineData(16u, 16)]
    [InlineData(32u, 1)]
    [InlineData(32u, 16)]
    [InlineData(32u, 32)]
    public async Task RepairAsync_WideRootCountsAndErasureCounts_RestoreSourceBytesExactly(uint roots, int erasures)
    {
        var token = TestContext.Current.CancellationToken;
        var dataSymbols = 255 - (int)roots;
        var blocks = (long)dataSymbols + 2;
        var covered = blocks * Block;
        var paritySize = (int)AvbFecCodec.CalculateParitySize(covered, roots);

        var original = Pattern((int)covered, 37);
        var image = new byte[covered + paritySize];
        original.AsSpan().CopyTo(image);
        await using (var source = new MemoryStream(original, writable: false))
        await using (var destination = new MemoryStream(image, writable: true))
        {
            destination.Position = covered;
            await AvbFecCodec.WriteParityAsync(source, destination, covered, roots, token);
        }

        var storedParity = image[(int)covered..].ToArray();
        var rounds = (int)((blocks + dataSymbols - 1) / dataSymbols);
        var bad = new long[erasures];
        for (var index = 0; index < erasures; index++)
        {
            bad[index] = (long)index * rounds;
            Corrupt(image, bad[index]);
        }

        await using var stream = new MemoryStream(image, writable: true);
        var repaired = await AvbFecCodec.RepairAsync(stream, covered, paritySize, roots, bad, token);

        Assert.True(repaired);
        Assert.Equal(original, image[..(int)covered]);
        Assert.Equal(storedParity, image[(int)covered..]);
    }

    public static TheoryData<int, byte> CaseMatrix()
    {
        var cases = new TheoryData<int, byte>();
        foreach (var length in RowLengths)
        {
            foreach (var coefficient in Coefficients)
            {
                cases.Add(length, coefficient);
            }
        }

        return cases;
    }

    private static byte[] Pattern(int length, int salt)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index * 31 + salt) & 0xFF);
        }

        return data;
    }

    private static void Corrupt(byte[] image, long block)
    {
        var offset = checked((int)(block * Block));
        for (var index = 0; index < Block; index++)
        {
            image[offset + index] = (byte)(0xE7 ^ index);
        }
    }
}