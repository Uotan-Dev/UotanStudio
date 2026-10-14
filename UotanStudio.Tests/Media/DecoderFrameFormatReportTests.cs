using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Media.Decoding.VideoToolbox;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class DecoderFrameFormatReportTests
{
    [Fact]
    public void MediaFoundationGpuDecoder_WithoutAnExportRing_ReportsNv12()
    {
        using var decoder = new MediaFoundationGpuVideoDecoder();
        Assert.Equal(VideoPixelFormat.Nv12, VideoDecoderFactory.GetFramePixelFormat(decoder));
    }

    [Fact]
    public void MediaFoundationGpuDecoder_ExportSwitchesWithoutARingDoNotOverClaimBgra()
    {
        using var decoder = new MediaFoundationGpuVideoDecoder { GpuExportEnabled = true, GpuExportFrameCallback = static _ => { } };
        Assert.Equal(VideoPixelFormat.Nv12, VideoDecoderFactory.GetFramePixelFormat(decoder));
    }

    [Fact]
    public void MediaFoundationCpuDecoder_ReportsNv12()
    {
        using var decoder = new MediaFoundationVideoDecoder();
        Assert.Equal(VideoPixelFormat.Nv12, VideoDecoderFactory.GetFramePixelFormat(decoder));
    }

    [Fact]
    public void VideoToolboxDecoder_WithoutExport_ReportsNv12()
    {
        using var decoder = new VideoToolboxVideoDecoder();
        Assert.Equal(VideoPixelFormat.Nv12, VideoDecoderFactory.GetFramePixelFormat(decoder));
    }

    [Fact]
    public void VaApiDecoder_ReportsNv12BecauseExportIsAFrameHandleNotACallbackLayout()
    {
        using var decoder = new VaApiVideoDecoder();
        Assert.Equal(VideoPixelFormat.Nv12, VideoDecoderFactory.GetFramePixelFormat(decoder));
    }

    [Fact]
    public void UninitializedFallbackWrapper_ReportsNv12InsteadOfGuessing()
    {
        using var decoder = VideoDecoderFactory.Create(new VideoDecoderOptions());
        Assert.Null(VideoDecoderFactory.GetActiveDecoder(decoder));
        Assert.Equal(VideoPixelFormat.Nv12, VideoDecoderFactory.GetFramePixelFormat(decoder));
    }
}