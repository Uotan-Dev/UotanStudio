using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors, tail-padding rule and word-aligned continuation of the MTK upload reduction (XOR of little-endian
/// 16-bit words from a zero seed), cross-checked against a scalar word loop written in this class so the odd-byte tail
/// and the chunk-boundary contract are exercised independently of the production reader.
/// <para>MTK 上传归约（以零种子对小端 16 位字做异或）的黄金向量、尾部补齐规则与字对齐续算，与本类内书写的标量逐词
/// 循环互相校验，从而在不依赖生产读取器的前提下检验奇数尾部与分块边界契约。</para>
/// </summary>
public class WordXor16LittleEndianTests
{
    [Fact]
    public void Compute_MtkAgentUploadGoldenVector_MatchesWordByWordReduction()
    {
        Assert.Equal((ushort)0x0800, WordXor16LittleEndian.Compute([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Compute_EmptySpan_ReturnsSeedUnchanged()
    {
        Assert.Equal((ushort)0, WordXor16LittleEndian.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0x1D0D, WordXor16LittleEndian.Compute([], 0x1D0D));
    }

    [Fact]
    public void Compute_SingleByte_ReturnsItsValueAsLowHalfOfWord()
    {
        Assert.Equal((ushort)0x0007, WordXor16LittleEndian.Compute([7]));
        Assert.Equal((ushort)0x0012, WordXor16LittleEndian.Compute([0x12]));
        Assert.Equal((ushort)0x0000, WordXor16LittleEndian.Compute([0]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(63)]
    [InlineData(1023)]
    [InlineData(1025)]
    [InlineData(4097)]
    public void Compute_OddLengthTail_EqualsExplicitZeroPadding(int length)
    {
        var odd = Payload(length);
        var padded = new byte[length + 1];
        odd.AsSpan().CopyTo(padded);

        Assert.Equal(WordXor16LittleEndian.Compute(padded), WordXor16LittleEndian.Compute(odd));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(66)]
    [InlineData(1024)]
    [InlineData(1026)]
    [InlineData(2048)]
    [InlineData(2050)]
    public void Compute_WordAlignedSplit_ContinuationMatchesSinglePass(int split)
    {
        var data = Payload(2052);
        var single = WordXor16LittleEndian.Compute(data);
        var continued = WordXor16LittleEndian.Compute(
            data.AsSpan(split),
            WordXor16LittleEndian.Compute(data.AsSpan(0, split)));

        Assert.Equal(single, continued);
    }

    [Fact]
    public void Compute_OddSplit_ContinuationDiffersFromSinglePass()
    {
        var data = Payload(3);
        var continued = WordXor16LittleEndian.Compute(
            data.AsSpan(1),
            WordXor16LittleEndian.Compute(data.AsSpan(0, 1)));

        Assert.NotEqual(WordXor16LittleEndian.Compute(data), continued);
    }

    [Fact]
    public void Compute_ChunkedLikeAgentUpload_ContinuationMatchesSinglePassOverWholeImage()
    {
        var image = Payload(3 * 1024 + 512);
        ushort running = 0;
        for (var offset = 0; offset < image.Length; offset += 1024)
        {
            running = WordXor16LittleEndian.Compute(image.AsSpan(offset, Math.Min(1024, image.Length - offset)), running);
        }

        Assert.Equal(WordXor16LittleEndian.Compute(image), running);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void Compute_EveryWidthUpToTheUploadChunk_MatchesScalarWordReference(int length)
    {
        var data = Payload(length);
        Assert.Equal(ScalarReference(data, 0), WordXor16LittleEndian.Compute(data));
        Assert.Equal(ScalarReference(data, 0x1D0D), WordXor16LittleEndian.Compute(data, 0x1D0D));
    }

    [Fact]
    public void Compute_ZeroWordRun_ReturnsSeedUnchanged()
    {
        Assert.Equal((ushort)0x0000, WordXor16LittleEndian.Compute(new byte[4096]));
        Assert.Equal((ushort)0xFFFF, WordXor16LittleEndian.Compute(new byte[4096], 0xFFFF));
    }

    private static byte[] Payload(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index % 251) + 1);
        }

        return data;
    }

    private static ushort ScalarReference(ReadOnlySpan<byte> data, ushort seed)
    {
        var checksum = seed;
        var offset = 0;
        for (; offset + 2 <= data.Length; offset += 2)
        {
            checksum ^= (ushort)((data[offset + 1] << 8) | data[offset]);
        }

        if (offset < data.Length)
        {
            checksum ^= data[offset];
        }

        return checksum;
    }
}