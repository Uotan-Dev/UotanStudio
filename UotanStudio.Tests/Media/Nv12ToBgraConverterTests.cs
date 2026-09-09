using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

/// <summary>
/// Nv12ToBgraConverter 像素级正确性与边界条件测试。
/// </summary>
public class Nv12ToBgraConverterTests
{
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
                var u = nv12[uvRow + (col & ~1)];
                var v = nv12[uvRow + (col & ~1) + 1];
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

        // 全图与参考实现逐像素比对（含最后一行/最后一列边界）。
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var y = nv12[(row * stride) + col];
                var uvRow = (height * stride) + ((row / 2) * stride);
                var u = nv12[uvRow + (col & ~1)];
                var v = nv12[uvRow + (col & ~1) + 1];
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
        // 纯白（Y=235, U=V=128）与纯黑（Y=16, U=V=128）的 BT.601 有限范围结果。
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

        // 饱和色度（U=16/V=240）应精确钳位到参考值：B=29 / G=208 / R=255。
        // Saturated chroma (U=16/V=240) must clamp exactly to the reference values.
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
}
