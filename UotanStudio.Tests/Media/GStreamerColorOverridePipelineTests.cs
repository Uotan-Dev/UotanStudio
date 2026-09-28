using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.GStreamer;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class GStreamerColorOverridePipelineTests
{
    public static bool GStreamerRuntimeAvailable =>
        Environment.GetEnvironmentVariable("UOTAN_GSTREAMER") == "1"
        && GStreamerVideoDecoder.IsAvailable(VideoCodecId.H264, hardware: false);

    [Theory]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full)]
    [InlineData(VideoColorMatrix.Auto, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Auto)]
    public void Initialize_WithColorOverrides_BuildsPipelineWithForcedColorimetry(VideoColorMatrix matrix, VideoColorRange range)
    {
        Assert.SkipWhen(!GStreamerRuntimeAvailable,
            "Set UOTAN_GSTREAMER=1 with GStreamer 1.24+ and its codec plugins installed to run this test.");
        using var decoder = new GStreamerVideoDecoder(hardware: false, new VideoDecoderOptions
        {
            ColorMatrix = matrix,
            ColorRange = range,
        });
        decoder.Initialize(VideoCodecId.H264, 64, 64, static _ => { });
        Assert.False(string.IsNullOrEmpty(decoder.DecoderElement));
    }
}