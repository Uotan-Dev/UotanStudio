using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Checks the device entry's user-facing state and link display against the enumerated endpoint data.
/// <para>校验设备条目面向用户的状态与链路显示是否与枚举到的端点数据一致。</para>
/// </summary>
public class DeviceLinkDisplayTests
{
    [Fact]
    public void DisplayName_ReadyAdbTarget_ShowsAndroidPlatform()
    {
        var device = Create(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "device");

        Assert.Equal("Android", device.StateLabel);
        Assert.EndsWith("(Android)", device.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayName_ReadyHdcTarget_ShowsHarmonyOsPlatform()
    {
        var device = Create(DeviceProtocolKind.Hdc, DeviceTransportKind.Usb, "device");

        Assert.Equal("HarmonyOS", device.StateLabel);
        Assert.EndsWith("(HarmonyOS)", device.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayName_ReadyTargetWithDetectedSlot_AppendsSlotLabel()
    {
        var device = Create(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "device");
        device.Slot = "Slot A";

        Assert.Equal("Android Slot A", device.StateLabel);
        Assert.EndsWith("(Android Slot A)", device.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayName_ReadyTargetWithoutSlot_OmitsSlotText()
    {
        var device = Create(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "device");
        device.Slot = "--";

        Assert.Equal("Android", device.StateLabel);
        Assert.EndsWith("(Android)", device.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayName_FastbootTargetWithDetectedSlot_AppendsSlotLabel()
    {
        var device = Create(DeviceProtocolKind.Fastboot, DeviceTransportKind.Usb, "fastboot");
        device.Slot = "Slot B";

        Assert.Equal("fastboot Slot B", device.StateLabel);
    }

    [Fact]
    public void Slot_Changes_NotifiesStateLabel()
    {
        var device = Create(DeviceProtocolKind.Hdc, DeviceTransportKind.Usb, "device");
        var changed = new List<string>();
        device.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        device.Slot = "Slot B";

        Assert.Contains(nameof(DeviceInfo.Slot), changed);
        Assert.Contains(nameof(DeviceInfo.StateLabel), changed);
        Assert.Contains(nameof(DeviceInfo.DisplayName), changed);
        Assert.Equal("HarmonyOS Slot B", device.StateLabel);
    }

    [Fact]
    public void SelectedDeviceName_SlotEnrichmentAfterSelection_UpdatesTopSelectorText()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        var device = Create(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "device");
        viewModel.Devices.Add(device);
        viewModel.SelectedDevice = device;
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        device.Slot = "Slot A";

        Assert.Contains(nameof(DeviceViewModel.SelectedDeviceName), changed);
        Assert.Contains("Android Slot A", viewModel.SelectedDeviceName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, "offline")]
    [InlineData(DeviceProtocolKind.Adb, "unauthorized")]
    [InlineData(DeviceProtocolKind.Hdc, "offline")]
    public void DisplayName_TargetNotReady_KeepsReportedState(DeviceProtocolKind protocol, string state)
    {
        var device = Create(protocol, DeviceTransportKind.Usb, state);

        Assert.Equal(state, device.StateLabel);
        Assert.EndsWith($"({state})", device.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void Transport_SelectedServerEndpointWithoutLinkClass_UsesSiblingLinkEvidence()
    {
        var server = new DetectedDevice
        {
            Key = "adb:R58M12345",
            Serial = "R58M12345",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Unknown,
            State = "device"
        };
        var direct = new DetectedDevice
        {
            Key = "usb:R58M12345",
            Serial = "R58M12345",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            State = "device"
        };
        var device = new DeviceInfo([server, direct]);

        Assert.Equal("USB", device.Transport);
        Assert.Equal("USB", device.LinkPort);
    }

    [Fact]
    public void DisplayName_MdnsInstanceSerialWithModel_UsesModelOnly()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "adb:adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            Serial = "adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        });
        device.Model = "24129PN74C";
        device.Slot = "Slot B";

        Assert.Equal("24129PN74C (Android Slot B)", device.DisplayName);
    }

    [Fact]
    public void DisplayName_MdnsInstanceWithoutModel_UsesDialableAlias()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "adb:adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            Serial = "adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            NetworkAlias = "192.168.1.144:40443",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        });

        Assert.Equal("192.168.1.144:40443 (Android)", device.DisplayName);
    }

    [Fact]
    public void DisplayName_MdnsInstanceWithoutModelOrAlias_TrimsTheServiceSuffix()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "adb:adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            Serial = "adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        });

        Assert.Equal("adb-6a082bda-lZgxIP (Android)", device.DisplayName);
    }

    [Fact]
    public void DisplayName_HardwareSerialWithModel_KeepsSerialAndModel()
    {
        var device = Create(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "device");
        device.Model = "SM_G9730";

        Assert.Equal("test - SM_G9730 (Android)", device.DisplayName);
    }

    [Fact]
    public void LinkPort_ResolvedNetworkAlias_ShowsDialableAddress()
    {
        var device = new DeviceInfo(new DetectedDevice
        {
            Key = "adb:adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            Serial = "adb-6a082bda-lZgxIP._adb-tls-connect._tcp",
            NetworkAlias = "192.168.1.144:40443",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp,
            State = "device"
        });

        Assert.Equal("Network", device.Transport);
        Assert.Equal("192.168.1.144:40443", device.LinkPort);
    }

    [Fact]
    public void State_ChangesFromReadyToOffline_NotifiesBothLabels()
    {
        var device = Create(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "device");
        var changed = new List<string>();
        device.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        device.State = "offline";

        Assert.Contains(nameof(DeviceInfo.State), changed);
        Assert.Contains(nameof(DeviceInfo.StateLabel), changed);
        Assert.Contains(nameof(DeviceInfo.DisplayName), changed);
        Assert.Equal("offline", device.StateLabel);
    }

    [Theory]
    [InlineData("USB", DeviceTransportKind.Usb)]
    [InlineData("usb", DeviceTransportKind.Usb)]
    [InlineData("TCP", DeviceTransportKind.Tcp)]
    [InlineData("tcp", DeviceTransportKind.Tcp)]
    [InlineData("UART", DeviceTransportKind.Unknown)]
    [InlineData(null, DeviceTransportKind.Unknown)]
    public void MapServerTransport_ServerReportedLabel_MapsCaseInsensitively(
        string? label, DeviceTransportKind expected)
        => Assert.Equal(expected, DeviceMonitorService.MapServerTransport(label));

    private static DeviceInfo Create(DeviceProtocolKind protocol, DeviceTransportKind transport, string state)
        => new(new DetectedDevice
        {
            Key = protocol == DeviceProtocolKind.Hdc ? "hdc:test" : "adb:test",
            Serial = "test",
            Protocol = protocol,
            Transport = transport,
            State = state
        });
}