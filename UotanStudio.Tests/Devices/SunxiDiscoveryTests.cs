using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Sunxi;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class SunxiDiscoveryTests
{
    [Fact]
    public void Classify_AllwinnerFelFesPair_ReturnsSunxi()
        => Assert.Equal("sunxi", UsbProtocolClassifier.Classify(0x1F3A, 0xEFE8, null, null, null));

    [Fact]
    public void Classify_AllwinnerWithAnotherPid_IsNotSunxi()
        => Assert.Null(UsbProtocolClassifier.Classify(0x1F3A, 0x1234, null, null, null));

    [Fact]
    public void ExpectedEndpointProtocol_Sunxi_MatchesDiscoveryText()
        => Assert.Equal("sunxi", DeviceMonitorService.ExpectedEndpointProtocol(DeviceProtocolKind.Sunxi));

    [Fact]
    public void DeviceInfo_SunxiEndpoint_ReportsBadgeAndHandshakeMode()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "usb:1F3A:EFE8",
            Serial = "1F3A:EFE8",
            Protocol = DeviceProtocolKind.Sunxi,
            Transport = DeviceTransportKind.Usb,
            State = "sunxi"
        });

        Assert.True(device.IsSunxi);
        Assert.Equal("Sunxi", device.SunxiBadge);
        Assert.Equal("Sunxi", device.Mode);

        device.SunxiMode = "FEL";

        Assert.Equal("Sunxi · FEL", device.SunxiBadge);
    }

    [Fact]
    public void DefaultSessionFactories_RegisterTheSunxiFactory()
        => Assert.Contains(DefaultDeviceSessionFactories.All, factory => factory.Kind == DeviceSessionKind.Sunxi);

    [Fact]
    public void SunxiDeviceStatus_ReportsChipCapacityAndUnknownIdFallback()
    {
        var known = new SunxiDeviceStatus(
            new SunxiDeviceIdentity(0x00168000, 0x1234, SunxiDeviceMode.Fes, 0, 0, 0), 2, 1024, 0x5);
        var unknown = new SunxiDeviceStatus(
            new SunxiDeviceIdentity(0x00123456, 0x1, SunxiDeviceMode.Fel, 0, 0, 0), null, null, null);

        Assert.Equal("H2/H3", known.ChipName);
        Assert.True(known.IsFes);
        Assert.Equal(1024L * 512L, known.CapacityBytes);
        Assert.Equal("0x00123456", unknown.ChipName);
        Assert.False(unknown.IsFes);
        Assert.Null(unknown.CapacityBytes);
    }
}