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
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Full)]
    [InlineData(VideoColorMatrix.Auto, VideoColorRange.Auto)]
    public void Shader_PreservesOddDimensionsStrideAndColorAfterDecoderReusesBuffer(VideoColorMatrix matrix, VideoColorRange range)
    {
        using var frame = Frame();
        var expected = new byte[5 * 3 * 4];
        Nv12ToBgraConverter.Convert(frame.Span, 5, 3, 8, expected, matrix: matrix, range: range);
        var image = new ScreenMirrorGpuPresenter.VideoImage(null, frame, matrix, range, 1, () => { });
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
            for (var i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - bytes[i]), 0, 2);
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
        for (var i = 0; i < 1000; i++) Assert.True(source.Publish(frame, VideoColorMatrix.Auto, VideoColorRange.Auto));
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

    [Fact]
    public void PaintNv12_SwappedChroma_MatchesCpuConverterOnExchangedChromaBytes()
    {
        const int width = 6, height = 4, stride = 8;
        var planes = new byte[stride * (height + height / 2)];
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++) planes[row * stride + col] = (byte)(30 + row * 47 + col * 9);
        for (var i = stride * height; i < planes.Length; i += 2)
        {
            planes[i] = 70;
            planes[i + 1] = 205;
        }
        var exchanged = (byte[])planes.Clone();
        for (var i = stride * height; i < exchanged.Length; i += 2)
            (exchanged[i], exchanged[i + 1]) = (planes[i + 1], planes[i]);

        var expected = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];
        Nv12ToBgraConverter.Convert(exchanged, width, height, stride, expected, matrix: VideoColorMatrix.Bt601, range: VideoColorRange.Limited);

        using var luma = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.R8Unorm, SKAlphaType.Opaque), planes, stride);
        using var chroma = SKImage.FromPixelCopy(new SKImageInfo((width + 1) / 2, (height + 1) / 2, SKColorType.Rg88, SKAlphaType.Opaque), planes[(stride * height)..], stride);
        Assert.NotNull(luma);
        Assert.NotNull(chroma);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        using var paint = new SKPaint { Color = SKColors.White };
        ScreenMirrorGpuPresenter.PaintNv12(surface.Canvas, paint, luma, chroma,
            new SKSamplingOptions(SKFilterMode.Nearest), VideoColorMatrix.Bt601, VideoColorRange.Limited, chromaSwapped: true, width, height);
        using var actual = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, actual.GetPixels(), actual.RowBytes, 0, 0));
        var bytes = actual.Bytes;
        for (var i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - bytes[i]), 0, 2);
    }

    [Fact]
    public void PaintNv12_UnswappedChroma_DivergesFromExchangedChromaReference()
    {
        const int width = 6, height = 4, stride = 8;
        var planes = new byte[stride * (height + height / 2)];
        planes.AsSpan(0, stride * height).Fill(140);
        for (var i = stride * height; i < planes.Length; i += 2)
        {
            planes[i] = 40;
            planes[i + 1] = 220;
        }
        var exchangedExpected = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];
        var exchanged = (byte[])planes.Clone();
        for (var i = stride * height; i < exchanged.Length; i += 2)
            (exchanged[i], exchanged[i + 1]) = (planes[i + 1], planes[i]);
        Nv12ToBgraConverter.Convert(exchanged, width, height, stride, exchangedExpected, matrix: VideoColorMatrix.Bt601, range: VideoColorRange.Limited);

        using var luma = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.R8Unorm, SKAlphaType.Opaque), planes, stride);
        using var chroma = SKImage.FromPixelCopy(new SKImageInfo((width + 1) / 2, (height + 1) / 2, SKColorType.Rg88, SKAlphaType.Opaque), planes[(stride * height)..], stride);
        Assert.NotNull(luma);
        Assert.NotNull(chroma);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        using var paint = new SKPaint { Color = SKColors.White };
        ScreenMirrorGpuPresenter.PaintNv12(surface.Canvas, paint, luma, chroma,
            new SKSamplingOptions(SKFilterMode.Nearest), VideoColorMatrix.Bt601, VideoColorRange.Limited, chromaSwapped: false, width, height);
        using var actual = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, actual.GetPixels(), actual.RowBytes, 0, 0));
        Assert.NotEqual(exchangedExpected, actual.Bytes);
    }

    private static DecodedVideoFrame Frame()
    {
        var bytes = ArrayPool<byte>.Shared.Rent(40);
        bytes.AsSpan(0, 40).Fill(255);
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 5; col++) bytes[row * 8 + col] = (byte)(16 + row * 40 + col * 31);
        new byte[] { 128, 128, 90, 240, 240, 110 }.CopyTo(bytes, 24);
        new byte[] { 54, 34, 128, 128, 192, 192 }.CopyTo(bytes, 32);
        return new DecodedVideoFrame(bytes, 40, 5, 3, 8, 0);
    }
}