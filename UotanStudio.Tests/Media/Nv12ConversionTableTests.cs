using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class Nv12ConversionTableTests
{
    private static (int Red, int GreenU, int GreenV, int Blue) Derive(double kr, double kb, bool full)
    {
        double kt = 1 - kr - kb;
        double chromaScale = full ? 1.0 : 255.0 / 224.0;
        int red = (int)Math.Round(2 * (1 - kr) * chromaScale * 256);
        int greenU = (int)Math.Round(-2 * kb * (1 - kb) / kt * chromaScale * 256);
        int greenV = (int)Math.Round(-2 * kr * (1 - kr) / kt * chromaScale * 256);
        int blue = (int)Math.Round(2 * (1 - kb) * chromaScale * 256);
        return (red, greenU, greenV, blue);
    }

    [Theory]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full, 0.299, 0.114)]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Limited, 0.299, 0.114)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Full, 0.2126, 0.0722)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited, 0.2126, 0.0722)]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Full, 0.2627, 0.0593)]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Limited, 0.2627, 0.0593)]
    public void For_MatchesMatrixDerivedReference(VideoColorMatrix matrix, VideoColorRange range, double kr, double kb)
    {
        var actual = Nv12ConversionTable.For(matrix, range);
        var expected = Derive(kr, kb, range == VideoColorRange.Full);
        Assert.Equal(range == VideoColorRange.Full ? 0 : 16, actual.LumaOffset);
        Assert.Equal(range == VideoColorRange.Full ? 256 : 298, actual.LumaScale);
        Assert.InRange(actual.RedFromChromaRed - expected.Red, -1, 1);
        Assert.InRange(actual.GreenFromChromaBlue - expected.GreenU, -1, 1);
        Assert.InRange(actual.GreenFromChromaRed - expected.GreenV, -1, 1);
        Assert.InRange(actual.BlueFromChromaBlue - expected.Blue, -1, 1);
    }

    [Fact]
    public void For_ResolvesAutoToBt601Limited()
    {
        Assert.Equal(Nv12ConversionTable.For(VideoColorMatrix.Bt601, VideoColorRange.Limited),
            Nv12ConversionTable.For(VideoColorMatrix.Auto, VideoColorRange.Auto));
    }

    [Fact]
    public void For_DistinguishesAllSixMatrixRangePairs()
    {
        VideoColorMatrix[] matrices = [VideoColorMatrix.Bt601, VideoColorMatrix.Bt709, VideoColorMatrix.Bt2020];
        VideoColorRange[] ranges = [VideoColorRange.Limited, VideoColorRange.Full];
        var distinct = new HashSet<Nv12Coefficients>();
        foreach (VideoColorMatrix matrix in matrices)
            foreach (VideoColorRange range in ranges)
                Assert.True(distinct.Add(Nv12ConversionTable.For(matrix, range)), $"{matrix} {range} duplicates another pair.");
        Assert.Equal(6, distinct.Count);
    }

    [Theory]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Full)]
    public void CpuConverter_AppliesBt2020TableDifferentlyFromBt601(VideoColorMatrix matrix, VideoColorRange range)
    {
        const int width = 8, height = 4, stride = 8;
        byte[] source = new byte[stride * (height + height / 2)];
        source.AsSpan(0, stride * height).Fill(180);
        for (int i = stride * height; i < source.Length; i += 2)
        {
            source[i] = 90;
            source[i + 1] = 200;
        }
        var actual = new byte[Nv12ToBgraConverter.GetBgraBufferSize(width, height)];
        var reference = new byte[actual.Length];
        Nv12ToBgraConverter.Convert(source, width, height, stride, actual, matrix: matrix, range: range);
        Nv12ToBgraConverter.Convert(source, width, height, stride, reference, matrix: VideoColorMatrix.Bt601, range: range);
        Assert.NotEqual(reference, actual);
    }
}