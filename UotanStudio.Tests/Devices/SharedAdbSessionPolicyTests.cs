using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class SharedAdbSessionPolicyTests
{
    [Fact]
    public void UsesSharedAdbSession_DirectNetworkAdbTarget_ReusesOneSession()
    {
        var device = new DetectedDevice
        {
            Key = "adb-tcp:192.168.1.144:40667",
            Serial = "192.168.1.144:40667",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp
        };

        Assert.True(DeviceMonitorService.UsesSharedAdbSession(device));
    }

    [Fact]
    public void UsesSharedAdbSession_DirectUsbAdbTarget_StillReusesOneSession()
    {
        var device = new DetectedDevice
        {
            Key = "usb-adb-key",
            Serial = "ADB0001",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb
        };

        Assert.True(DeviceMonitorService.UsesSharedAdbSession(device));
    }

    [Fact]
    public void UsesSharedAdbSession_ServerManagedAdbTarget_KeepsPerCallSessions()
    {
        var device = new DetectedDevice
        {
            Key = "adb:192.168.1.144:40667",
            Serial = "192.168.1.144:40667",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp
        };

        Assert.False(DeviceMonitorService.UsesSharedAdbSession(device));
    }

    [Fact]
    public void UsesSharedAdbSession_NonAdbTarget_IsNeverShared()
    {
        var device = new DetectedDevice
        {
            Key = "usb-fastboot-key",
            Serial = "ADB0001",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Usb
        };

        Assert.False(DeviceMonitorService.UsesSharedAdbSession(device));
    }
}