using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.GStreamer;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class GStreamerColorimetryTests
{
    [Theory]
    [InlineData(VideoColorMatrix.Auto, VideoColorRange.Auto, null)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited, "2:3:5:1")]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Full, "1:3:5:1")]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Limited, "2:4:4:4")]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full, "1:4:4:4")]
    public void ColorimetryFor_MapsMatrixAndRangeOntoGStreamerNumericTuples(VideoColorMatrix matrix, VideoColorRange range, string? expected)
        => Assert.Equal(expected, GStreamerVideoDecoder.ColorimetryFor(matrix, range));

    [Theory]
    [InlineData(VideoColorMatrix.Bt709, "0:3:5:1")]
    [InlineData(VideoColorMatrix.Bt601, "0:4:4:4")]
    public void ColorimetryFor_MatrixWithoutRangeOverride_LeavesRangeUntouched(VideoColorMatrix matrix, string expected)
        => Assert.Equal(expected, GStreamerVideoDecoder.ColorimetryFor(matrix, VideoColorRange.Auto));

    [Theory]
    [InlineData(VideoColorRange.Full, "1:0:0:0")]
    [InlineData(VideoColorRange.Limited, "2:0:0:0")]
    public void ColorimetryFor_RangeWithoutMatrixOverride_LeavesMatrixUntouched(VideoColorRange range, string expected)
        => Assert.Equal(expected, GStreamerVideoDecoder.ColorimetryFor(VideoColorMatrix.Auto, range));
}