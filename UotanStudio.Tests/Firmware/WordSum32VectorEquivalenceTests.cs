using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the equality of the two <see cref="WordSum32LittleEndian"/> forms: the portable little-endian reader and the
/// little-endian form that reinterprets doublewords and accumulates whole vector lanes. A divergence would change a
/// preloader XFlash command checksum depending on the processor that computed it.
/// <para>固定 <see cref="WordSum32LittleEndian"/> 两种形态的相等性：可移植的小端读取形态，与把双字重组并按整条向量通道
/// 累加的小端形态。一旦分歧，同一个 preloader XFlash 命令校验和会随计算它的处理器而改变。</para>
/// </summary>
/// <remarks>
/// The lengths bracket the four-byte word boundary, the vector lane boundary and their neighbours, and the trailing
/// rule is asserted separately because a remainder contributes byte values rather than one padded doubleword.
/// <para>长度覆盖四字节字边界、向量通道边界及其相邻值；尾部规则单独断言，因为余数贡献的是逐字节数值而不是补齐后的一个双字。</para>
/// </remarks>
public class WordSum32VectorEquivalenceTests
{
    private static readonly int[] BoundaryLengths =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 15, 16, 17, 19, 20, 21, 23, 24, 25,
        31, 32, 33, 63, 64, 65, 127, 128, 129, 1023, 1024, 1025, 4096, 4097
    ];

    [Fact]
    public void Compute_ReferenceVector_MatchesKnownValue()
    {
        byte[] payload = [0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0xAA];
        Assert.Equal(0x0000_00ADu, WordSum32LittleEndian.Compute(payload));
        Assert.Equal(0x0000_00ADu, WordSum32LittleEndian.ComputePortable(payload));
        Assert.Equal(0x0000_00ADu, WordSum32LittleEndian.ComputeLittleEndian(payload));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void Compute_EveryPath_AgreeOnPatternedInput(int length)
    {
        var data = Pattern(length);
        var portable = WordSum32LittleEndian.ComputePortable(data);
        Assert.Equal(portable, WordSum32LittleEndian.Compute(data));
        Assert.Equal(portable, WordSum32LittleEndian.ComputeLittleEndian(data));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void Compute_EveryPath_AgreeOnHighBytes(int length)
    {
        var data = Pattern(length);
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = 0xFF;
        }

        var portable = WordSum32LittleEndian.ComputePortable(data);
        Assert.Equal(portable, WordSum32LittleEndian.Compute(data));
        Assert.Equal(portable, WordSum32LittleEndian.ComputeLittleEndian(data));
    }

    [Fact]
    public void Compute_TrailingBytes_ContributeByteValuesNotAPaddedWord()
    {
        byte[] threeBytes = [0x01, 0x02, 0x03];
        Assert.Equal(6u, WordSum32LittleEndian.Compute(threeBytes));
        Assert.Equal(6u, WordSum32LittleEndian.ComputePortable(threeBytes));
        Assert.Equal(6u, WordSum32LittleEndian.ComputeLittleEndian(threeBytes));

        byte[] fiveBytes = [0x01, 0x00, 0x00, 0x00, 0x02];
        Assert.Equal(3u, WordSum32LittleEndian.Compute(fiveBytes));
    }

    [Fact]
    public void Compute_OverflowingSum_WrapsModuloTwoToThe32()
    {
        var data = new byte[4096];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = 0xFF;
        }

        var expected = (uint)((data.Length / 4) * uint.MaxValue + data.Length % 4 * 0xFF);
        Assert.Equal(expected, WordSum32LittleEndian.Compute(data));
    }

    public static TheoryData<int> LengthCases()
    {
        var cases = new TheoryData<int>();
        foreach (var length in BoundaryLengths)
        {
            cases.Add(length);
        }

        return cases;
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index * 31 + 7) & 0xFF);
        }

        return data;
    }
}