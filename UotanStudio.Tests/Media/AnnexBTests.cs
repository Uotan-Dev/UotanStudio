using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

public class AnnexBTests
{
    [Fact]
    public void MixedStartCodes_ConvertWithoutChangingNalPayload()
    {
        byte[] input = [0, 0, 0, 1, 0x67, 1, 0, 0, 3, 1, 0, 0, 1, 0x68, 2];
        var ranges = AnnexB.Split(input);
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new byte[] { 0, 0, 0, 6, 0x67, 1, 0, 0, 3, 1, 0, 0, 0, 2, 0x68, 2 }, AnnexB.ToLengthPrefixed(input, ranges));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0, 0, 1 })]
    [InlineData(new byte[] { 7, 0, 0, 1, 0x67 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 1, 0x67 })]
    public void InvalidFraming_IsRejected(byte[] data) => Assert.Throws<InvalidDataException>(() => AnnexB.Split(data));

    [Fact]
    public void Convert_TruncatedSource_IsRejectedBeforePointerAccess() =>
        Assert.Throws<ArgumentException>(() => Nv12ToBgraConverter.Convert(new byte[23], 4, 4, 4, new byte[64]));

    [Fact]
    public void Convert_OddWidthWithoutFullChromaPair_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Nv12ToBgraConverter.Convert(new byte[15], 3, 3, 3, new byte[36]));
}