using UotanStudio.Comm.Devices.Discovery;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class UsbProtocolClassifierPathTests
{
    [Theory]
    [InlineData("\\\\?\\USB#VID_0E8D&PID_2000#123#{guid}", "mtk")]
    [InlineData("\\\\?\\USB#VID_1782&PID_4D00#123#{guid}", "unisoc")]
    [InlineData("\\\\?\\USB#VID_05C6&PID_9008#123#{guid}", "edl")]
    public void ClassifyPath_KnownBootPair_UsesCanonicalClassifier(string path, string expected)
    {
        Assert.Equal(expected, UsbProtocolClassifier.ClassifyPath(path));
    }

    [Theory]
    [InlineData("\\\\?\\USB#VID_1234&PID_5678#123#{guid}")]
    [InlineData("")]
    [InlineData(null)]
    public void ClassifyPath_UnknownPairOrEmpty_ReturnsNull(string? path)
    {
        Assert.Null(UsbProtocolClassifier.ClassifyPath(path));
    }
}