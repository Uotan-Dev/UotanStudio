using UotanStudio.Hash.XzCrc64;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the slice-by-8 CRC-64 against the XZ container's published check value and against an independent byte-at-a-time
/// reference, including how arbitrary stream chunking composes. A divergence would make an <c>.xz</c> container fail its
/// own integrity check or, worse, verify a corrupted payload on one machine and not another.
/// <para>固定 slice-by-8 CRC-64 与 XZ 容器公开校验值、以及独立逐字节参考实现之间的一致性，并覆盖任意流式分块的组合行为。
/// 一旦分歧，<c>.xz</c> 容器会在完整性校验上失败，或在某台机器上通过而另一台不通过。</para>
/// </summary>
/// <remarks>
/// The lengths bracket every eight-byte fold boundary and several table-slice boundaries, so a tail-handling or
/// table-generation mistake changes the value rather than staying latent until a real container is loaded.
/// <para>长度覆盖每个八字节折叠边界与若干表切片边界，因此尾部处理或建表错误会直接改变数值，而不是潜伏到真实容器加载时才暴露。</para>
/// </remarks>
public class XzCrc64SliceEquivalenceTests
{
    private const ulong ReflectedPolynomial = 0xc96c5795d7870f42;

    private static readonly int[] BoundaryLengths =
    [
        0, 1, 2, 7, 8, 9, 15, 16, 17, 23, 24, 25, 31, 32, 33, 63, 64, 65,
        127, 128, 129, 255, 256, 257, 1023, 1024, 1025, 4095, 4096, 4097, 65_536
    ];

    [Fact]
    public void HashToUInt64_ReferenceCheckVector_MatchesXzGoldenValue()
    {
        Assert.Equal(0x995DC9BBDF1939FAul, XzCrc64.HashToUInt64("123456789"u8));
    }

    [Fact]
    public void HashToUInt64_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0ul, XzCrc64.HashToUInt64([]));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void HashToUInt64_EveryBoundaryLength_MatchesByteWiseReference(int length)
    {
        var data = Pattern(length);
        Assert.Equal(ByteWiseReference(data), XzCrc64.HashToUInt64(data));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void Append_RandomChunking_MatchesSingleBufferValue(int length)
    {
        var data = Pattern(length);
        var expected = XzCrc64.HashToUInt64(data);
        var random = new Random(length + 17);
        var chunked = new XzCrc64();
        var position = 0;
        while (position < data.Length)
        {
            var chunk = Math.Min(random.Next(1, 41), data.Length - position);
            chunked.Append(data.AsSpan(position, chunk));
            position += chunk;
        }

        Assert.Equal(expected, chunked.GetCurrentHashAsUInt64());
    }

    [Fact]
    public void GetCurrentHashAsUInt64_AfterPartialAppend_KeepsAccumulating()
    {
        var data = Pattern(4096);
        var incremental = new XzCrc64();
        incremental.Append(data.AsSpan(0, 1000));
        var partial = incremental.GetCurrentHashAsUInt64();
        incremental.Append(data.AsSpan(1000, 1500));
        var middle = incremental.GetCurrentHashAsUInt64();
        incremental.Append(data.AsSpan(2500));

        Assert.NotEqual(partial, middle);
        Assert.Equal(XzCrc64.HashToUInt64(data), incremental.GetCurrentHashAsUInt64());
    }

    [Fact]
    public void Append_EmptySpan_LeavesStateUnchanged()
    {
        var incremental = new XzCrc64();
        incremental.Append(Pattern(777));
        var before = incremental.GetCurrentHashAsUInt64();
        incremental.Append([]);
        Assert.Equal(before, incremental.GetCurrentHashAsUInt64());
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
            data[index] = (byte)((index * 73 + 29) & 0xFF);
        }

        return data;
    }

    private static ulong ByteWiseReference(ReadOnlySpan<byte> data)
    {
        var table = BuildByteWiseTable();
        var state = ulong.MaxValue;
        foreach (var value in data)
        {
            state = table[(byte)(state ^ value)] ^ (state >> 8);
        }

        return ~state;
    }

    private static ulong[] BuildByteWiseTable()
    {
        var table = new ulong[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (ulong)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) != 0 ? ReflectedPolynomial : 0);
            }

            table[index] = value;
        }

        return table;
    }
}