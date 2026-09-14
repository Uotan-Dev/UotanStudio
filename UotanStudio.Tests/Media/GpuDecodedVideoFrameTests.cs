using System.Buffers;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

public class GpuDecodedVideoFrameTests
{
    [Fact]
    public void GpuFrame_InheritsDecodedVideoFrame_AndSkipsNv12Validation()
    {
        // BGRA layout: 4 bytes per pixel; length may be smaller than the NV12
        // requirement, so the base validation must be skipped for this shape.
        int width = 4, height = 2;
        byte[] buffer = new byte[width * height * 4];
        using var frame = new GpuDecodedVideoFrame(buffer, buffer.Length, width, height, width * 4, 1234L);
        Assert.IsAssignableFrom<DecodedVideoFrame>(frame);
        Assert.Equal(width * height * 4, frame.Length);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(width * 4, frame.Stride);
        Assert.Equal(1234L, frame.TimestampTicks);
    }

    [Fact]
    public void GpuFrame_StrideMayExceedWidthTimesFour()
    {
        int width = 4, height = 2, stride = width * 4 + 16;
        byte[] buffer = new byte[stride * height];
        using var frame = new GpuDecodedVideoFrame(buffer, stride * height, width, height, stride, 0);
        Assert.Equal(stride, frame.Stride);
    }

    [Fact]
    public void GpuFrame_AfterDispose_BufferThrows()
    {
        var frame = new GpuDecodedVideoFrame(new byte[64], 64, 4, 4, 16, 0);
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Buffer);
        // Double dispose is safe. 二次释放安全。
        frame.Dispose();
    }

    [Fact]
    public void DecodedVideoFrame_BaseValidation_StillRejectsShortNv12()
    {
        // NV12 for 16x16 requires 16*16*3/2 bytes; a short buffer must fail.
        Assert.Throws<ArgumentException>(() =>
            new DecodedVideoFrame(new byte[100], 100, 16, 16, 16, 0));
    }
}