using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

public class GpuDecodedVideoFrameTests
{
    [Fact]
    public void GpuFrame_InheritsDecodedVideoFrame_AndSkipsNv12Validation()
    {
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
        frame.Dispose();
    }

    [Fact]
    public void DecodedVideoFrame_BaseValidation_StillRejectsShortNv12()
    {
        Assert.Throws<ArgumentException>(() =>
            new DecodedVideoFrame(new byte[100], 100, 16, 16, 16, 0));
    }

    [Fact]
    public void GpuFrame_ReportsBgra32PixelFormat()
    {
        byte[] buffer = new byte[64];
        using var frame = new GpuDecodedVideoFrame(buffer, buffer.Length, 4, 2, 16, 0);
        Assert.Equal(VideoPixelFormat.Bgra32, frame.PixelFormat);
    }

    [Fact]
    public void Nv12Frame_ReportsNv12PixelFormat()
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(24);
        using var frame = new DecodedVideoFrame(buffer, 24, 4, 2, 4, 0);
        Assert.Equal(VideoPixelFormat.Nv12, frame.PixelFormat);
    }

    [Fact]
    public void GpuFrame_LengthBelowNv12Total_StillConstructsBecauseLayoutIsDeclared()
    {
        byte[] buffer = new byte[64];
        using var frame = new GpuDecodedVideoFrame(buffer, buffer.Length, 4, 4, 16, 0);
        Assert.Equal(VideoPixelFormat.Bgra32, frame.PixelFormat);
        Assert.Equal(64, frame.Length);
    }
}