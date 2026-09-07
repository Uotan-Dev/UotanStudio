using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class LogChannelTests
{
    [Theory]
    [InlineData("Images", LogChannel.Firmware)]
    [InlineData("images", LogChannel.Firmware)]
    [InlineData("IMAGES", LogChannel.Firmware)]
    [InlineData("OtherImages", LogChannel.General)]
    [InlineData("Firmware", LogChannel.Firmware)]
    [InlineData("DexEditor", LogChannel.Firmware)]
    [InlineData("FlashService", LogChannel.Flash)]
    [InlineData("DeviceManager", LogChannel.Device)]
    [InlineData("USB", LogChannel.Device)]
    [InlineData("NodeEditor", LogChannel.Build)]
    [InlineData("Packing", LogChannel.Build)]
    [InlineData("App", LogChannel.General)]
    [InlineData("UnknownComponent", LogChannel.General)]
    [InlineData("", LogChannel.General)]
    public void ChannelOf_AssignsSourcesToExpectedChannel(string source, LogChannel expected)
    {
        Assert.Equal(expected, LogService.ChannelOf(source));
    }
}
