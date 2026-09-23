using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class ScreenMirrorTransportTests
{
    [Fact]
    public void OpenAdbSession_NetworkTargetWithoutServer_DoesNotOpenUsb()
    {
        if (DebugBridgeRoute.UseAdbServer())
            Assert.Skip("An ADB server is running; the absent-server route cannot be tested.");

        var target = new DetectedDevice
        {
            Key = "adb:192.0.2.1:5555",
            Serial = "192.0.2.1:5555",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Tcp
        };
        Assert.Throws<IOException>(() => DeviceMonitorService.Instance.OpenAdbSession(target));
    }
}