using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

/// <summary>
/// <para>Nv12ToBgraConverter 像素级正确性与边界条件测试。
/// </para>
/// </summary>
public class Nv12ToBgraConverterTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(4, 2)]
    [InlineData(7, 9)]
    [InlineData(65, 17)]
    [InlineData(1920, 3)]
    public void Convert_RandomPixelsAndPaddedRows_MatchScalarReference(int width, int height)
    {
        var stride = ((width + 1) & ~1) + 6;
        var outputStride = width * 4 + 13;
        var source = new byte[stride * (height + (height + 1) / 2)];
        new Random(42).NextBytes(source);
        var actual = new byte[outputStride * height];
        Array.Fill(actual, (byte)0xa5);
        Nv12ToBgraConverter.Convert(source, width, height, stride, actual, outputStride);
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var uvRow = stride * height + (row / 2) * stride;
                var uvRowNext = row / 2 + 1 < (height + 1) / 2 ? uvRow + stride : uvRow;
                var u = SampleChroma(source, uvRow, uvRowNext, col >> 1, width, row & 1);
                var v = SampleChroma(source, uvRow + 1, uvRowNext + 1, col >> 1, width, row & 1);
                var expected = ExpectedPixel(source[row * stride + col], u, v);
                var offset = row * outputStride + col * 4;
                Assert.Equal(expected, (actual[offset], actual[offset + 1], actual[offset + 2], actual[offset + 3]));
            }
            Assert.All(actual.AsSpan(row * outputStride + width * 4, 13).ToArray(), value => Assert.Equal(0xa5, value));
        }
    }

    /// <summary>构造一帧 NV12 数据（可选自定义 stride）。</summary>
    private static (byte[] Nv12, int Stride) BuildNv12(int width, int height, int stride, Func<int, int, byte>? yFunc = null, Func<int, int, (byte U, byte V)>? uvFunc = null)
    {
        yFunc ??= (x, y) => (byte)(16 + ((x * 219 / Math.Max(1, width - 1)) + (y * 219 / Math.Max(1, height - 1))) / 2);
        uvFunc ??= (x, y) => ((byte)(64 + x % 128), (byte)(96 + y % 160));

        var uvHeight = (height + 1) / 2;
        var nv12 = new byte[stride * (height + uvHeight)];

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                nv12[(row * stride) + col] = yFunc(col, row);
            }
        }

        for (var row = 0; row < uvHeight; row++)
        {
            for (var col = 0; col < width; col += 2)
            {
                var (u, v) = uvFunc(col, row * 2);
                nv12[(height * stride) + (row * stride) + col] = u;
                nv12[(height * stride) + (row * stride) + col + 1] = v;
            }
        }

        return (nv12, stride);
    }

    private static (byte B, byte G, byte R, byte A) ExpectedPixel(byte y, byte u, byte v)
    {
        var c = y - 16;
        var d = u - 128;
        var e = v - 128;
        var r = (298 * c + 409 * e + 128) >> 8;
        var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
        var b = (298 * c + 516 * d + 128) >> 8;
        return (Clamp(b), Clamp(g), Clamp(r), 255);
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));

    /// <summary>
    /// <para>双线性色度参考采样：水平 3:1 加权靠前一列，奇数亮度行垂直 1:1 混合，末列钳制。
    /// </para>
    /// </summary>
    private static byte SampleChroma(byte[] nv12, int uvRow, int uvRowNext, int cell, int width, int verticalWeight)
    {
        var nextCell = cell + 1 < (width + 1) / 2 ? cell + 1 : cell;
        var upper = (nv12[uvRow + cell * 2] * 3 + nv12[uvRow + nextCell * 2] + 2) >> 2;
        var lower = (nv12[uvRowNext + cell * 2] * 3 + nv12[uvRowNext + nextCell * 2] + 2) >> 2;
        return (byte)(verticalWeight != 0 ? (upper + lower + 1) >> 1 : upper);
    }

    [Fact]
    public void GetBgraBufferSize_ReturnsWidthTimesHeightTimes4()
    {
        Assert.Equal(1920 * 1080 * 4, Nv12ToBgraConverter.GetBgraBufferSize(1920, 1080));
    }

    [Fact]
    public void GetBgraBufferSize_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Nv12ToBgraConverter.GetBgraBufferSize(0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => Nv12ToBgraConverter.GetBgraBufferSize(16, 0));
    }

    [Fact]
    public void Convert_GradientFrame_MatchesReferencePerPixel()
    {
        const int width = 64;
        const int height = 48;
        var (nv12, stride) = BuildNv12(width, height, width);
        var destination = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];

        Nv12ToBgraConverter.Convert(nv12, width, height, stride, destination);

        var uvHeight = height / 2;
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var y = nv12[(row * stride) + col];
                var uvRow = (height * stride) + ((row / 2) * stride);
                var uvRowNext = row / 2 + 1 < (height + 1) / 2 ? uvRow + stride : uvRow;
                var u = SampleChroma(nv12, uvRow, uvRowNext, col >> 1, width, row & 1);
                var v = SampleChroma(nv12, uvRow + 1, uvRowNext + 1, col >> 1, width, row & 1);
                var (b, g, r, a) = ExpectedPixel(y, u, v);

                var offset = (row * width * 4) + (col * 4);
                Assert.Equal(b, destination[offset]);
                Assert.Equal(g, destination[offset + 1]);
                Assert.Equal(r, destination[offset + 2]);
                Assert.Equal(a, destination[offset + 3]);
            }
        }

        _ = uvHeight;
    }

    [Fact]
    public void Convert_OddWidthAndHeight_HandlesBoundaryPixels()
    {
        const int width = 61;
        const int height = 33;
        var (nv12, stride) = BuildNv12(width, height, stride: 64);
        var destination = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];

        Nv12ToBgraConverter.Convert(nv12, width, height, stride, destination);

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var y = nv12[(row * stride) + col];
                var uvRow = (height * stride) + ((row / 2) * stride);
                var uvRowNext = row / 2 + 1 < (height + 1) / 2 ? uvRow + stride : uvRow;
                var u = SampleChroma(nv12, uvRow, uvRowNext, col >> 1, width, row & 1);
                var v = SampleChroma(nv12, uvRow + 1, uvRowNext + 1, col >> 1, width, row & 1);
                var (b, g, r, a) = ExpectedPixel(y, u, v);

                var offset = (row * width * 4) + (col * 4);
                Assert.Equal(b, destination[offset]);
                Assert.Equal(g, destination[offset + 1]);
                Assert.Equal(r, destination[offset + 2]);
                Assert.Equal(a, destination[offset + 3]);
            }
        }
    }

    [Fact]
    public void Convert_PureColors_ProduceExpectedBgra()
    {
        const int width = 2;
        const int height = 2;
        var nv12 = new byte[width * height * 3 / 2];
        var yPlane = nv12.AsSpan(0, width * height);
        yPlane.Fill(235);
        var uvPlane = nv12.AsSpan(width * height);
        uvPlane.Fill(128);

        var destination = new byte[width * height * 4];
        Nv12ToBgraConverter.Convert(nv12, width, height, width, destination);

        for (var i = 0; i < width * height; i++)
        {
            Assert.Equal(255, destination[(i * 4) + 0]);
            Assert.Equal(255, destination[(i * 4) + 1]);
            Assert.Equal(255, destination[(i * 4) + 2]);
            Assert.Equal(255, destination[(i * 4) + 3]);
        }

        yPlane.Fill(16);
        Nv12ToBgraConverter.Convert(nv12, width, height, width, destination);

        for (var i = 0; i < width * height; i++)
        {
            Assert.Equal(0, destination[(i * 4) + 0]);
            Assert.Equal(0, destination[(i * 4) + 1]);
            Assert.Equal(0, destination[(i * 4) + 2]);
            Assert.Equal(255, destination[(i * 4) + 3]);
        }
    }

    [Fact]
    public void Convert_SaturatedChroma_ClampsToByteRange()
    {
        const int width = 4;
        const int height = 4;
        var (nv12, stride) = BuildNv12(
            width,
            height,
            stride: width,
            yFunc: static (_, _) => (byte)235,
            uvFunc: static (_, _) => ((byte)16, (byte)240));
        var destination = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];

        Nv12ToBgraConverter.Convert(nv12, width, height, stride, destination);

        var (b, g, r, a) = ExpectedPixel(y: 235, u: 16, v: 240);
        for (var i = 0; i < width * height; i++)
        {
            Assert.Equal(b, destination[(i * 4) + 0]);
            Assert.Equal(g, destination[(i * 4) + 1]);
            Assert.Equal(r, destination[(i * 4) + 2]);
            Assert.Equal(a, destination[(i * 4) + 3]);
        }
    }

    [Fact]
    public void Convert_DestinationTooSmall_Throws()
    {
        const int width = 8;
        const int height = 8;
        var (nv12, stride) = BuildNv12(width, height, width);
        var destination = new byte[(width * height * 4) - 4];

        Assert.Throws<ArgumentException>(
            () => Nv12ToBgraConverter.Convert(nv12, width, height, stride, destination));
    }

    [Fact]
    public void Convert_StrideSmallerThanWidth_Throws()
    {
        const int width = 8;
        const int height = 8;
        var nv12 = new byte[width * height * 3 / 2];
        var destination = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Nv12ToBgraConverter.Convert(nv12, width, height, nv12Stride: width - 2, destination));
    }

    [Fact]
    public void Convert_FrameOverload_MatchesSpanOverload()
    {
        const int width = 32;
        const int height = 24;
        var (nv12, stride) = BuildNv12(width, height, stride: 48);

        var spanResult = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];
        Nv12ToBgraConverter.Convert(nv12, width, height, stride, spanResult);

        var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(stride * (height + (height / 2)));
        Array.Copy(nv12, pooled, nv12.Length);
        var frameResult = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];
        using var frame = new DecodedVideoFrame(pooled, nv12.Length, width, height, stride, timestampTicks: 42);
        Nv12ToBgraConverter.Convert(in frame, frameResult);
        Assert.Equal(spanResult, frameResult);
    }

    [Fact]
    public void Convert_FrameOverload_PackedFrame_ThrowsAndNamesObservedLayout()
    {
        using DecodedVideoFrame frame = new GpuDecodedVideoFrame(new byte[64], 64, 4, 4, 16, 0);
        var destination = new byte[Nv12ToBgraConverter.GetBgraBufferSize(4, 4)];
        var error = Assert.Throws<ArgumentException>(() => Nv12ToBgraConverter.Convert(in frame, destination));
        Assert.Contains(nameof(VideoPixelFormat.Bgra32), error.Message, StringComparison.Ordinal);
    }
}