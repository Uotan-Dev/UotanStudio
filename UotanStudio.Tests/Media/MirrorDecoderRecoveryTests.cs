using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.GStreamer;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class MirrorDecoderRecoveryTests
{
    private static readonly VideoDecoderOptions Full = new()
    {
        Backend = VideoDecoderBackend.MediaFoundationGpu,
        ColorMatrix = VideoColorMatrix.Bt709,
        ColorRange = VideoColorRange.Full,
        PreferNv12Output = false,
        MediaFoundationPreferGpuExport = true,
        MediaFoundationAdapterLuid = [1, 2, 3, 4, 5, 6, 7, 8],
        VaApiPreferGpuExport = true,
        VaApiRenderNode = "/dev/dri/renderD128",
        VideoToolboxPreferGpuExport = true,
    };

    [Fact]
    public void IsRecoverable_ClassifiesDecoderFaults()
    {
        Assert.True(MirrorDecoderRecovery.IsRecoverable(new InvalidDataException("layout")));
        Assert.True(MirrorDecoderRecovery.IsRecoverable(new InvalidOperationException("queue")));
        Assert.False(MirrorDecoderRecovery.IsRecoverable(new ObjectDisposedException("decoder")));
        Assert.False(MirrorDecoderRecovery.IsRecoverable(new OperationCanceledException()));
        Assert.False(MirrorDecoderRecovery.IsRecoverable(new ArgumentException("argument")));
    }

    [Fact]
    public void BackendOf_DistinguishesGStreamerHardwareFromSoftware()
    {
        using var hardware = new GStreamerVideoDecoder(hardware: true);
        using var software = new GStreamerVideoDecoder(hardware: false);
        Assert.Equal(VideoDecoderBackend.GStreamerHardware, MirrorDecoderRecovery.BackendOf(hardware));
        Assert.Equal(VideoDecoderBackend.GStreamerSoftware, MirrorDecoderRecovery.BackendOf(software));
    }

    [Fact]
    public void BackendOf_TreatsMissingAndUnrecognisedDecodersAsNotRebuildable()
    {
        Assert.Equal(VideoDecoderBackend.Auto, MirrorDecoderRecovery.BackendOf(null));
        Assert.Equal(VideoDecoderBackend.Auto, MirrorDecoderRecovery.BackendOf(new UnknownDecoder()));
    }

    [Fact]
    public void DegradeWindows_SwapHardwareForCpuAndDropEveryGpuClaim()
    {
        var degraded = MirrorDecoderRecovery.DegradeWindows(Full, VideoDecoderBackend.MediaFoundationGpu);
        Assert.NotNull(degraded);
        Assert.Equal(VideoDecoderBackend.MediaFoundationCpu, degraded!.Backend);
        Assert.False(degraded.MediaFoundationPreferGpuExport);
        Assert.True(degraded.PreferNv12Output);
        Assert.Null(degraded.MediaFoundationAdapterLuid);
    }

    [Fact]
    public void DegradeWindows_KeepsUnrelatedSessionChoicesIntact()
    {
        var degraded = MirrorDecoderRecovery.DegradeWindows(Full, VideoDecoderBackend.MediaFoundationGpu);
        Assert.NotNull(degraded);
        Assert.Equal(VideoColorMatrix.Bt709, degraded!.ColorMatrix);
        Assert.Equal(VideoColorRange.Full, degraded.ColorRange);
        Assert.Equal("/dev/dri/renderD128", degraded.VaApiRenderNode);
    }

    [Theory]
    [InlineData(VideoDecoderBackend.MediaFoundationCpu)]
    [InlineData(VideoDecoderBackend.GStreamerSoftware)]
    [InlineData(VideoDecoderBackend.Auto)]
    public void DegradeWindows_RefusesToRebuildWhenNothingSimplerRemains(VideoDecoderBackend active)
        => Assert.Null(MirrorDecoderRecovery.DegradeWindows(Full, active));

    [Theory]
    [InlineData(VideoDecoderBackend.VaApi)]
    [InlineData(VideoDecoderBackend.GStreamerHardware)]
    public void DegradeLinux_FallsBackToSoftwareGStreamer(VideoDecoderBackend active)
    {
        var degraded = MirrorDecoderRecovery.DegradeLinux(Full, active);
        Assert.NotNull(degraded);
        Assert.Equal(VideoDecoderBackend.GStreamerSoftware, degraded!.Backend);
        Assert.False(degraded.VaApiPreferGpuExport);
    }

    [Fact]
    public void DegradeLinux_RefusesToRebuildSoftwareGStreamer()
        => Assert.Null(MirrorDecoderRecovery.DegradeLinux(Full, VideoDecoderBackend.GStreamerSoftware));

    [Fact]
    public void DegradeMacOS_FirstStepDropsOnlyTheIosurfaceExport()
    {
        var degraded = MirrorDecoderRecovery.DegradeMacOS(Full, VideoDecoderBackend.VideoToolbox);
        Assert.NotNull(degraded);
        Assert.False(degraded!.VideoToolboxPreferGpuExport);
        Assert.Equal(VideoDecoderBackend.MediaFoundationGpu, degraded.Backend);
    }

    [Fact]
    public void DegradeMacOS_SecondStepMovesToSoftwareGStreamer()
    {
        var degraded = MirrorDecoderRecovery.DegradeMacOS(Full, VideoDecoderBackend.GStreamerHardware);
        Assert.NotNull(degraded);
        Assert.Equal(VideoDecoderBackend.GStreamerSoftware, degraded!.Backend);
    }

    [Theory]
    [InlineData(VideoDecoderBackend.MediaFoundationGpu)]
    [InlineData(VideoDecoderBackend.VaApi)]
    [InlineData(VideoDecoderBackend.GStreamerSoftware)]
    public void DegradeMacOS_RefusesForeignBackends(VideoDecoderBackend active)
        => Assert.Null(MirrorDecoderRecovery.DegradeMacOS(Full, active));

    [Fact]
    public void RebuildBudget_StopsAfterASingleAttempt()
    {
        Assert.Equal(1, MirrorDecoderRecovery.RebuildAttemptsPerSession);
        Assert.Equal(3, MirrorDecoderRecovery.ConsecutiveFailuresBeforeRebuild);
        Assert.True(MirrorDecoderRecovery.KeyFrameWaitMilliseconds >= 1000);
    }

    private sealed class UnknownDecoder : IVideoDecoder
    {
        public bool IsInitialized => false;
        public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback) { }
        public void Decode(ReadOnlySpan<byte> accessUnit) { }
        public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks) { }
        public void Drain() { }
        public void Reset() { }
        public void Dispose() { }
    }
}