using System.Buffers;
using Avalonia;
using SkiaSharp;
using UotanStudio.Media.Decoding;
using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorGpuPresenterTests
{
    [Theory]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Full)]
    [InlineData(VideoColorMatrix.Auto, VideoColorRange.Auto)]
    public void Shader_PreservesOddDimensionsStrideAndColorAfterDecoderReusesBuffer(VideoColorMatrix matrix, VideoColorRange range)
    {
        using var frame = Frame();
        var expected = new byte[5 * 3 * 4];
        Nv12ToBgraConverter.Convert(frame.Span, 5, 3, 8, expected, matrix: matrix, range: range);
        var image = new ScreenMirrorGpuPresenter.VideoImage(frame, matrix, range, 1, () => { });
        try
        {
            frame.Buffer.AsSpan().Fill(0);
            frame.Dispose();
            var info = new SKImageInfo(5, 3, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            image.Draw(surface.Canvas, new Rect(0, 0, 5, 3), 1.0, null);
            using var actual = new SKBitmap(info);
            Assert.True(surface.ReadPixels(info, actual.GetPixels(), actual.RowBytes, 0, 0));
            var bytes = actual.Bytes;
            for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - bytes[i]), 0, 2);
        }
        finally { image.Release(); }
    }

    [Fact]
    public void SlowCompositor_BoundsFramesAndReleasesOutstandingLeasesAfterClose()
    {
        using var source = new ScreenMirrorGpuPresenter();
        using var frame = Frame();
        Assert.True(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
        var first = source.CreateOperation(new Rect(0, 0, 5, 3));
        Assert.True(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
        var second = source.CreateOperation(new Rect(0, 0, 5, 3));
        Assert.True(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
        for (int i = 0; i < 1000; i++) Assert.True(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
        Assert.Equal(3, source.LiveFrames);
        Assert.Equal(1000, source.Skipped);
        first.Dispose(); first.Dispose();
        Assert.True(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
        Assert.Equal(2, source.LiveFrames);
        source.Dispose();
        Assert.Equal(1, source.LiveFrames);
        Assert.False(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
        second.Dispose(); second.Dispose();
        Assert.Equal(0, source.LiveFrames);
    }

    private static DecodedVideoFrame Frame()
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(40);
        bytes.AsSpan(0, 40).Fill(255);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 5; col++) bytes[row * 8 + col] = (byte)(16 + row * 40 + col * 31);
        new byte[] { 128, 128, 90, 240, 240, 110 }.CopyTo(bytes, 24);
        new byte[] { 54, 34, 128, 128, 192, 192 }.CopyTo(bytes, 32);
        return new DecodedVideoFrame(bytes, 40, 5, 3, 8, 0);
    }
}