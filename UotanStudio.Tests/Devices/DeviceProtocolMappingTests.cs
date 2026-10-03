using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class DeviceProtocolMappingTests
{
    [Fact]
    public void ProtocolLabel_EveryDefinedProtocol_ProducesANonEmptyLabel()
    {
        foreach (DeviceProtocolKind protocol in Enum.GetValues<DeviceProtocolKind>())
        {
            var device = new DetectedDevice
            {
                Key = "usb:test",
                Serial = "test",
                Protocol = protocol,
                Transport = DeviceTransportKind.Usb
            };

            Assert.False(string.IsNullOrWhiteSpace(device.ProtocolLabel), protocol.ToString());
        }
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, "ADB")]
    [InlineData(DeviceProtocolKind.Hdc, "HDC")]
    [InlineData(DeviceProtocolKind.Fastboot, "Fastboot")]
    [InlineData(DeviceProtocolKind.Edl, "EDL")]
    [InlineData(DeviceProtocolKind.Rkdl, "RKDL")]
    [InlineData(DeviceProtocolKind.Mtk, "MTK")]
    public void ProtocolLabel_KnownProtocol_ReportsItsDisplayName(DeviceProtocolKind protocol, string expected)
    {
        var device = new DetectedDevice
        {
            Key = "usb:test",
            Serial = "test",
            Protocol = protocol,
            Transport = DeviceTransportKind.Usb
        };

        Assert.Equal(expected, device.ProtocolLabel);
    }

    [Fact]
    public void ExpectedEndpointProtocol_EveryDefinedProtocol_MapsToADiscoveryProtocolText()
    {
        foreach (DeviceProtocolKind protocol in Enum.GetValues<DeviceProtocolKind>())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(DeviceMonitorService.ExpectedEndpointProtocol(protocol)),
                protocol.ToString());
        }
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, "adb")]
    [InlineData(DeviceProtocolKind.Hdc, "hdc")]
    [InlineData(DeviceProtocolKind.Fastboot, "fastboot")]
    [InlineData(DeviceProtocolKind.Edl, "edl")]
    [InlineData(DeviceProtocolKind.Rkdl, "rkdl")]
    [InlineData(DeviceProtocolKind.Mtk, "mtk")]
    public void ExpectedEndpointProtocol_KnownProtocol_MatchesDiscoveryText(
        DeviceProtocolKind protocol, string expected)
        => Assert.Equal(expected, DeviceMonitorService.ExpectedEndpointProtocol(protocol));

    [Fact]
    public void DeviceInfoMode_MtkDevice_ReportsLabelWithoutThrowing()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "mtk:test",
            Serial = "test",
            Protocol = DeviceProtocolKind.Mtk,
            Transport = DeviceTransportKind.Usb,
            State = "brom"
        });

        Assert.True(device.IsMtk);
        Assert.Equal("MTK", device.Mode);
        Assert.Contains("brom", device.Device.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void MtkInfo_SetThenSwitchEndpoint_ClearsEndpointSpecificValues()
    {
        var first = new DetectedDevice
        {
            Key = "mtk:first",
            Serial = "first",
            Protocol = DeviceProtocolKind.Mtk,
            Transport = DeviceTransportKind.Usb
        };
        var second = new DetectedDevice
        {
            Key = "mtk:second",
            Serial = "second",
            Protocol = DeviceProtocolKind.Mtk,
            Transport = DeviceTransportKind.Usb
        };
        var device = new DeviceInfo([first, second]);

        Assert.False(device.HasMtkInfo);
        device.SetMtkInfo(new UotanStudio.Comm.Protocols.Mtk.MtkDeviceInfo(0x8A00, 0x0001, 1, 2, 0x17));
        Assert.True(device.HasMtkInfo);
        Assert.Equal("0x8A00 / 0x0001", device.MtkHardware);
        Assert.Equal("1 / 2", device.MtkFirmware);
        Assert.Equal("SBC=1 SLA=1 DAA=1 CERT=1", device.MtkSecurityBits);

        device.SelectedConnection = second;

        Assert.False(device.HasMtkInfo);
        Assert.Equal(string.Empty, device.MtkHardware);
        Assert.Equal(string.Empty, device.MtkFirmware);
        Assert.Equal(string.Empty, device.MtkSecurityBits);
    }

    [Theory]
    [InlineData("brom", false, true)]
    [InlineData("preloader", false, true)]
    [InlineData("brom", true, false)]
    [InlineData("preloader", true, false)]
    [InlineData("da", false, false)]
    [InlineData("logging", false, false)]
    [InlineData("adb", false, false)]
    [InlineData("", false, false)]
    public void ShouldQueryMtkIdentity_GatedOnDescriptorStateAndFreshness(
        string state, bool hasMtkInfo, bool expected)
        => Assert.Equal(expected, DeviceViewModel.ShouldQueryMtkIdentity(state, hasMtkInfo));
}