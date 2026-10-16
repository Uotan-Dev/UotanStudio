using System.Buffers;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorPixelBufferTests
{
    [Fact]
    public void BorrowedReadback_DisposalInvalidatesFrameWithoutReturningSessionArray()
    {
        var pixels = new byte[24];
        var frame = new GpuDecodedVideoFrame(pixels, 24, 3, 2, 12, 0)
        { ReturnBufferToPool = false };
        var buffer = new ScreenMirrorPixelBuffer();
        pixels.AsSpan().Fill(17);
        buffer.Update(frame, VideoColorMatrix.Auto, VideoColorRange.Auto);
        frame.Dispose();
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Buffer);
        pixels.AsSpan().Fill(42);
        Assert.All(buffer.Pixels, value => Assert.Equal(17, value));
    }

    [Fact]
    public void DesktopDefaults_ReadBackNv12BeforeAnyDraw()
    {
        var mirror = new ViewModels.Editors.ScreenMirrorViewModel();
        Assert.False(mirror.GpuPresenter.IsAvailable);
        Assert.True(mirror.ActiveDecoderOptions.PreferNv12Output);
    }
    [Fact]
    public void RepeatedFrames_ReuseExactSizeStorageAndOwnTheCopy()
    {
        var buffer = new ScreenMirrorPixelBuffer();
        using var frame = Bgra(16, 16, 64);
        frame.Buffer.AsSpan(0, frame.Length).Fill(17);
        buffer.Update(frame, VideoColorMatrix.Auto, VideoColorRange.Auto);
        var storage = buffer.Pixels;
        Assert.Equal(16 * 16 * 4, storage.Length);
        frame.Buffer.AsSpan(0, frame.Length).Fill(42);
        Assert.All(storage, value => Assert.Equal(17, value));
        for (var i = 0; i < 1000; i++) buffer.Update(frame, VideoColorMatrix.Auto, VideoColorRange.Auto);
        Assert.Same(storage, buffer.Pixels);
        Assert.All(storage, value => Assert.Equal(42, value));
    }

    [Fact]
    public void PaddedBgraRows_ExcludePadding()
    {
        var buffer = new ScreenMirrorPixelBuffer();
        using var frame = Bgra(2, 2, 12);
        frame.Buffer.AsSpan(0, 24).Fill(255);
        frame.Buffer.AsSpan(0, 8).Fill(10);
        frame.Buffer.AsSpan(12, 8).Fill(20);
        buffer.Update(frame, VideoColorMatrix.Auto, VideoColorRange.Auto);
        Assert.Equal(Enumerable.Repeat((byte)10, 8).Concat(Enumerable.Repeat((byte)20, 8)), buffer.Pixels);
    }

    [Fact]
    public void SizeChanges_ReplaceStorageAndUpdateDimensions()
    {
        var buffer = new ScreenMirrorPixelBuffer();
        using var large = Bgra(8, 4, 32);
        using var small = Bgra(2, 2, 8);
        buffer.Update(large, VideoColorMatrix.Auto, VideoColorRange.Auto);
        var old = buffer.Pixels;
        buffer.Update(small, VideoColorMatrix.Auto, VideoColorRange.Auto);
        Assert.NotSame(old, buffer.Pixels);
        Assert.Equal(16, buffer.Pixels.Length);
        Assert.Equal((2, 2), (buffer.Width, buffer.Height));
    }

    [Fact]
    public void Nv12LimitedBlack_ConvertsToOpaqueBlack()
    {
        var pixels = ArrayPool<byte>.Shared.Rent(6);
        pixels.AsSpan(0, 4).Fill(16);
        pixels.AsSpan(4, 2).Fill(128);
        using var frame = new DecodedVideoFrame(pixels, 6, 2, 2, 2, 0)
        { ColorMatrix = VideoColorMatrix.Bt709, ColorRange = VideoColorRange.Limited };
        var buffer = new ScreenMirrorPixelBuffer();
        buffer.Update(frame, VideoColorMatrix.Auto, VideoColorRange.Auto);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 }, buffer.Pixels);
    }

    private static GpuDecodedVideoFrame Bgra(int width, int height, int stride)
        => new(ArrayPool<byte>.Shared.Rent(stride * height), stride * height, width, height, stride, 0);
}