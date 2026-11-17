using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Discovery-side tests of the UNISOC protocol: the classifier pair, the endpoint protocol mapping, the session
/// factory registration and the device badge/stage surface.
/// <para>紫光展锐协议的发现侧测试：分类器 VID/PID 对、端点协议映射、会话工厂注册与设备徽章/阶段表面。</para>
/// </summary>
public class UnisocDiscoveryTests
{
    [Fact]
    public void Classify_UnisocDownloadPair_ReturnsUnisoc()
        => Assert.Equal("unisoc", UsbProtocolClassifier.Classify(0x1782, 0x4D00, null, null, null));

    [Fact]
    public void Classify_UnisocWithAnotherPid_IsNotUnisoc()
        => Assert.Null(UsbProtocolClassifier.Classify(0x1782, 0x1234, null, null, null));

    [Fact]
    public void ExpectedEndpointProtocol_Unisoc_MatchesDiscoveryText()
        => Assert.Equal("unisoc", DeviceMonitorService.ExpectedEndpointProtocol(DeviceProtocolKind.Unisoc));

    [Fact]
    public void DeviceInfo_UnisocEndpoint_ReportsBadgeAndHandshakeStage()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "usb:1782:4D00",
            Serial = "1782:4D00",
            Protocol = DeviceProtocolKind.Unisoc,
            Transport = DeviceTransportKind.Usb,
            State = "unisoc"
        });

        Assert.True(device.IsUnisoc);
        Assert.Equal("Unisoc", device.UnisocBadge);
        Assert.False(device.HasUnisocStage);

        device.UnisocStage = "FDL2";

        Assert.Equal("Unisoc · FDL2", device.UnisocBadge);
        Assert.True(device.HasUnisocStage);
    }

    [Fact]
    public void DefaultSessionFactories_RegisterTheUnisocFactory()
        => Assert.Contains(DefaultDeviceSessionFactories.All, factory => factory.Kind == DeviceSessionKind.Unisoc);

    [Fact]
    public void UnisocDeviceStatus_DecodesVersionAndFallsBackToStageName()
    {
        var withVersion = new UnisocDeviceStatus(UnisocBootStage.BootRom,
            "SPRD3\0"u8.ToArray(), SecureBoot: false);
        var withoutVersion = new UnisocDeviceStatus(UnisocBootStage.Fdl2, null, SecureBoot: false);

        Assert.Equal("SPRD3", withVersion.VersionText);
        Assert.Equal("Fdl2", withoutVersion.VersionText);
        Assert.Equal(UnisocBootStage.Fdl2, withoutVersion.Stage);
    }
}