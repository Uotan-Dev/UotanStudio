using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationColorOverrideTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Windows Media Foundation software decoders are required.", SkipUnless = nameof(IsWindows))]
    public void Initialize_WithColorOverrides_NegotiatesForcedOutputType()
    {
        using var decoder = new MediaFoundationVideoDecoder(new VideoDecoderOptions
        {
            ColorMatrix = VideoColorMatrix.Bt709,
            ColorRange = VideoColorRange.Full,
        });
        decoder.Initialize(VideoCodecId.H264, 1920, 1080, static _ => { });
        Assert.Equal(VideoColorMatrix.Bt709, decoder.NegotiatedColorMatrix);
        Assert.Equal(VideoColorRange.Full, decoder.NegotiatedColorRange);
    }

    [Fact(Skip = "Windows Media Foundation software decoders are required.", SkipUnless = nameof(IsWindows))]
    public void Initialize_WithOppositeOverrides_NegotiatesForcedOutputType()
    {
        using var decoder = new MediaFoundationVideoDecoder(new VideoDecoderOptions
        {
            ColorMatrix = VideoColorMatrix.Bt601,
            ColorRange = VideoColorRange.Limited,
        });
        decoder.Initialize(VideoCodecId.H265, 1280, 720, static _ => { });
        Assert.Equal(VideoColorMatrix.Bt601, decoder.NegotiatedColorMatrix);
        Assert.Equal(VideoColorRange.Limited, decoder.NegotiatedColorRange);
    }

    [Fact(Skip = "Windows Media Foundation software decoders are required.", SkipUnless = nameof(IsWindows))]
    public void Initialize_WithoutOverrides_ReportsStreamMetadataOrAuto()
    {
        using var decoder = new MediaFoundationVideoDecoder();
        decoder.Initialize(VideoCodecId.H264, 640, 480, static _ => { });
        Assert.True(Enum.IsDefined(decoder.NegotiatedColorMatrix));
        Assert.True(Enum.IsDefined(decoder.NegotiatedColorRange));
    }
}