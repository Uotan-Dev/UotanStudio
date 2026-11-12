using UotanStudio.Hash.Simd;
using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins every byte-block kernel, including the forced scalar software fallback, to one byte-wise oracle across the
/// 16/32/64-byte vector boundaries and large buffers.
/// <para>把每个字节块内核（含强制标量软件回退）在 16/32/64 字节向量边界与大缓冲上都钉到同一个逐字节预言。</para>
/// </summary>
/// <remarks>
/// A single machine only executes its preferred kernel in production, so each kernel is invoked explicitly here.
/// Unsupported kernels must quietly fall back to the scalar loop, which is what makes this suite meaningful on x64,
/// ARM64 and RISC-V hosts alike.
/// <para>生产环境在一台机器上只执行首选内核，因此这里显式调用每一个内核。不可用的内核必须静默回退到标量循环，
/// 这正是本套测试在 x64、ARM64 与 RISC-V 主机上都有意义的原因。</para>
/// </remarks>
public class SimdOpsEquivalenceTests
{
    private static readonly int[] BoundaryLengths =
    [
        0, 1, 2, 3, 7, 15, 16, 17, 31, 32, 33, 47, 48, 63, 64, 65, 95, 96,
        127, 128, 129, 255, 256, 257, 511, 512, 513, 1023, 1024, 4096, 4097,
        65535, 65536, 300_000
    ];

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void XorInPlace_EveryKernelAndFallback_MatchesByteWiseOracle(int length)
    {
        var left = Pattern(length, 37, 11);
        var right = Pattern(length, 91, 203);
        var expected = new byte[length];
        for (var index = 0; index < length; index++)
        {
            expected[index] = (byte)(left[index] ^ right[index]);
        }

        foreach (var kernel in Enum.GetValues<SimdOps.SimdKernel>())
        {
            var actual = (byte[])left.Clone();
            SimdOps.XorKernel(actual, right, kernel);
            Assert.Equal(expected, actual);
        }

        var dispatched = (byte[])left.Clone();
        SimdOps.XorInPlace(dispatched, right);
        Assert.Equal(expected, dispatched);

        var previous = SimdOps.ForceSoftwareFallback;
        try
        {
            SimdOps.ForceSoftwareFallback = true;
            var fallback = (byte[])left.Clone();
            SimdOps.XorInPlace(fallback, right);
            Assert.Equal(expected, fallback);
        }
        finally
        {
            SimdOps.ForceSoftwareFallback = previous;
        }
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void XorConstantInPlace_EveryKernelAndFallback_MatchesByteWiseOracle(int length)
    {
        var data = Pattern(length, 37, 11);
        const byte value = 0xA5;
        var expected = new byte[length];
        for (var index = 0; index < length; index++)
        {
            expected[index] = (byte)(data[index] ^ value);
        }

        foreach (var kernel in Enum.GetValues<SimdOps.SimdKernel>())
        {
            var actual = (byte[])data.Clone();
            SimdOps.XorConstantKernel(actual, value, kernel);
            Assert.Equal(expected, actual);
        }

        var dispatched = (byte[])data.Clone();
        SimdOps.XorConstantInPlace(dispatched, value);
        Assert.Equal(expected, dispatched);

        var previous = SimdOps.ForceSoftwareFallback;
        try
        {
            SimdOps.ForceSoftwareFallback = true;
            var fallback = (byte[])data.Clone();
            SimdOps.XorConstantInPlace(fallback, value);
            Assert.Equal(expected, fallback);
        }
        finally
        {
            SimdOps.ForceSoftwareFallback = previous;
        }
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void SumBytes_EveryKernelAndFallback_MatchesByteWiseOracle(int length)
    {
        var data = Pattern(length, 37, 11);
        var oracle = ByteWiseSum(data);

        foreach (var kernel in Enum.GetValues<SimdOps.SimdKernel>())
        {
            Assert.Equal(oracle, SimdOps.SumBytesKernel(data, kernel));
        }

        Assert.Equal(oracle, SimdOps.SumBytes(data));

        var previous = SimdOps.ForceSoftwareFallback;
        try
        {
            SimdOps.ForceSoftwareFallback = true;
            Assert.Equal(oracle, SimdOps.SumBytes(data));
        }
        finally
        {
            SimdOps.ForceSoftwareFallback = previous;
        }
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void ByteSum8And16_Compute_ReturnLowBitsOfSharedSum(int length)
    {
        var data = Pattern(length, 37, 11);
        var shared = SimdOps.SumBytes(data);

        Assert.Equal((byte)shared, ByteSum8.Compute(data));
        Assert.Equal((ushort)shared, ByteSum16.Compute(data));
        Assert.Equal(shared, ByteSum32.Compute(data));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void XorFoldWords_EveryKernelAndFallback_MatchesWordWiseOracle(int length)
    {
        var data = Pattern(length, 37, 11);
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(data);
        ushort oracle = 0;
        foreach (var word in words)
        {
            oracle ^= word;
        }

        foreach (var kernel in Enum.GetValues<SimdOps.SimdKernel>())
        {
            Assert.Equal(oracle, SimdOps.XorFoldWordsKernel(words, kernel));
        }

        Assert.Equal(oracle, SimdOps.XorFoldWords(words));

        var previous = SimdOps.ForceSoftwareFallback;
        try
        {
            SimdOps.ForceSoftwareFallback = true;
            Assert.Equal(oracle, SimdOps.XorFoldWords(words));
        }
        finally
        {
            SimdOps.ForceSoftwareFallback = previous;
        }
    }

    [Fact]
    public void XorInPlace_LengthMismatch_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SimdOps.XorInPlace(new byte[65], new byte[64]));

        var previous = SimdOps.ForceSoftwareFallback;
        try
        {
            SimdOps.ForceSoftwareFallback = true;
            Assert.Throws<ArgumentException>(() => SimdOps.XorInPlace(new byte[65], new byte[64]));
        }
        finally
        {
            SimdOps.ForceSoftwareFallback = previous;
        }
    }

    [Fact]
    public void SimdCapabilities_Describe_ReportsSupportedFamiliesAndActiveWidth()
    {
        var report = SimdCapabilities.Describe();

        Assert.Contains("arch=", report);
        Assert.Contains("supported=", report);
        Assert.Contains("active=", report);
        Assert.Contains("width=", report);
        Assert.True((SimdCapabilities.SupportedIsas & SimdIsa.Scalar) != 0);
        Assert.True(SimdCapabilities.ActiveVectorWidthBytes >= 1);
        Assert.Equal(SimdCapabilities.HardwareAccelerated, SimdCapabilities.ActiveIsa != SimdIsa.Scalar);
    }

    [Fact]
    public void SimdCapabilities_ActiveKernel_MatchesReportedActiveIsa()
    {
        var activeKernel = SimdOps.ActiveKernel;

        Assert.Equal(SimdCapabilities.ActiveIsa, SimdCapabilities.ToIsa(activeKernel));
        Assert.Equal(SimdCapabilities.ActiveVectorWidthBytes, SimdCapabilities.WidthOf(activeKernel));
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

    private static byte[] Pattern(int length, int multiplier, int seed)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index * multiplier + seed) & 0xFF);
        }

        return data;
    }

    private static uint ByteWiseSum(ReadOnlySpan<byte> data)
    {
        ulong sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return (uint)sum;
    }
}