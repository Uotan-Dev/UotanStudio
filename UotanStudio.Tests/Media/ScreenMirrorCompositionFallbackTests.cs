using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Media.Decoding.VideoToolbox;
using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorCompositionFallbackTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void PublishSharedTexture_WithoutAttachedComposition_ReleasesSlotAndReportsFallback()
    {
        using var ring = new ExportSlotRing<MediaFoundationExportSlot>(1,
            _ => new MediaFoundationExportSlot { Texture = 1, OutputView = 2, KeyedMutex = 3, SharedHandle = 4 },
            _ => { },
            _ => true);
        var slot = ring.Rent();
        Assert.NotNull(slot);
        var frame = new D3D11SharedVideoFrame(ring, slot, slot.SharedHandle, 8, 4, 0, VideoColorMatrix.Bt709, VideoColorRange.Limited);
        using var presenter = new ScreenMirrorGpuPresenter();
        Assert.Null(presenter.Composition);
        Assert.False(presenter.UsesCompositionSurface);
        Assert.False(presenter.PublishSharedTexture(frame));
        Assert.Equal(0, presenter.CompositedFrames);
        Assert.NotNull(ring.Rent());
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void PublishIosurface_WithoutAttachedComposition_ReleasesFrameAndReportsFallback()
    {
        int released = 0;
        var frame = new IosurfaceVideoFrame(11, 12, 8, 4, 0, () => Interlocked.Increment(ref released));
        using var presenter = new ScreenMirrorGpuPresenter();
        Assert.False(presenter.PublishIosurface(frame));
        Assert.Equal(1, released);
        Assert.Equal(0, presenter.CompositedFrames);
    }
}