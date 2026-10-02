using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

public sealed class AdbTargetInfoTests
{
    [Fact]
    public void Parse_EmulatorConsoleLine_ReportsTcpWithoutUsbDescriptor()
    {
        var targets = AdbTargetInfo.Parse(
            "emulator-5554          device product:Amber model:ABR_AL80 device:Amber transport_id:5\n");

        var target = Assert.Single(targets);
        Assert.Equal("emulator-5554", target.Serial);
        Assert.Equal("device", target.State);
        Assert.Equal("TCP", target.Transport);
    }

    [Fact]
    public void Parse_UsbTransportDescriptor_ReportsUsbLink()
    {
        var targets = AdbTargetInfo.Parse(
            "0A1B2C3D4E5F6789  device usb:1-2 product:pantah model:Pixel_7 device:pantah transport_id:3\n");

        Assert.Equal("USB", Assert.Single(targets).Transport);
    }

    [Theory]
    [InlineData("127.0.0.1:5555         device transport_id:2", "TCP")]
    [InlineData("192.168.1.50:5555      offline product:generic transport_id:4", "TCP")]
    [InlineData("[::1]:5555             device transport_id:2", "TCP")]
    [InlineData("emulator-5586          unauthorized transport_id:6", "TCP")]
    [InlineData("0A1B2C3D               unauthorized usb:1-1.4 transport_id:1", "USB")]
    [InlineData("0123456789ABCDEF       device usb:0123456789ABCDEF product:star2lte", "USB")]
    [InlineData("8fad1670\tdevice\tsideload\ttransport_id:7", "")]
    [InlineData("legacy-host            device", "")]
    [InlineData("emulator-abc           device transport_id:1", "")]
    [InlineData("emulator-0             device transport_id:1", "")]
    [InlineData("emulator-70000         device transport_id:1", "")]
    public void Parse_LineEvidence_ReportsServerLinkClass(string line, string expected)
    {
        var target = Assert.Single(AdbTargetInfo.Parse(line + "\n"));

        Assert.Equal(expected, target.Transport);
    }

    [Fact]
    public void Parse_OrderedListing_KeepsEveryTargetInListingOrder()
    {
        var listing = "\r\nemulator-5554 device transport_id:5\r\n" +
            "0A1B2C3D offline usb:1-2 transport_id:3\r\n127.0.0.1:7555 device transport_id:1\r\n";

        var targets = AdbTargetInfo.Parse(listing);

        Assert.Equal(["emulator-5554", "0A1B2C3D", "127.0.0.1:7555"],
            targets.Select(target => target.Serial));
        Assert.Equal(["device", "offline", "device"], targets.Select(target => target.State));
        Assert.Equal(["TCP", "USB", "TCP"], targets.Select(target => target.Transport));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("transport_id:1\n")]
    public void Parse_LineWithoutState_YieldsNoTarget(string listing)
    {
        Assert.Empty(AdbTargetInfo.Parse(listing));
    }
}