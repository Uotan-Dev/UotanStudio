using System.Runtime.Versioning;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using UotanStudio.Comm.Transports.Usb.Drivers;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Covers the Windows driver-takeover boundary that produces an EDL endpoint no native backend can open.
/// <para>覆盖导致 EDL 端点无法被任何原生后端打开的 Windows 驱动接管边界。</para>
/// </summary>
public sealed class WinUsbTakeoverEndpointTests
{
    private const string EdlUsbDevicePath =
        @"\\?\usb#vid_05c6&pid_9008#30000030#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";

    private const string EdlComPortPath =
        @"\\?\usb#vid_05c6&pid_9008#30000030#{86e0d1e0-8089-11d0-9ce4-08003e301f73}";

    [Fact]
    public void IsNonWinUsbInterfacePath_HubAndComPortInterfaces_AreTrue()
    {
        Assert.True(CommDeviceTransportOpener.IsNonWinUsbInterfacePath(EdlUsbDevicePath));
        Assert.True(CommDeviceTransportOpener.IsNonWinUsbInterfacePath(EdlComPortPath));
    }

    [Fact]
    public void IsNonWinUsbInterfacePath_WinUsbInterfaceAndEmptyPath_AreFalse()
    {
        Assert.False(CommDeviceTransportOpener.IsNonWinUsbInterfacePath(
            @"\\?\usb#vid_05c6&pid_9008#30000030#{fb49d77d-9040-4feb-904b-b119574dbda4}"));
        Assert.False(CommDeviceTransportOpener.IsNonWinUsbInterfacePath(string.Empty));
    }

    [Fact]
    public void TryDeriveInstanceId_EdlErrorPath_ReturnsExactUsbInstance()
    {
        Assert.Equal(@"usb\vid_05c6&pid_9008\30000030", EdlSerialPortFallback.TryDeriveInstanceId(EdlUsbDevicePath));
    }

    [Fact]
    public void IsWinUsbBoundNode_TakeoverBinding_RefusesSerialFallback()
    {
        Assert.True(EdlSerialPortFallback.IsWinUsbBoundNode("WINUSB", null));
        Assert.True(EdlSerialPortFallback.IsWinUsbBoundNode("winusb", null));
        Assert.True(EdlSerialPortFallback.IsWinUsbBoundNode("qcusbser",
            new[] { "{fb49d77d-9040-4feb-904b-b119574dbda4}" }));
    }

    [Fact]
    public void IsWinUsbBoundNode_RealSerialBinding_AllowsSerialFallback()
    {
        Assert.False(EdlSerialPortFallback.IsWinUsbBoundNode("usbser", null));
        Assert.False(EdlSerialPortFallback.IsWinUsbBoundNode("qcwdfserial", null));
        Assert.False(EdlSerialPortFallback.IsWinUsbBoundNode(null, null));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ParseInterfaceGuids_BracedMultiSz_ReturnsDistinctGuids()
    {
        AssertSkippedOutsideWindows();

        var guids = WinUsbDriverInstaller.ParseInterfaceGuids(
            new[] { "{FB49D77D-9040-4FEB-904B-B119574DBDA4}", "{fb49d77d-9040-4feb-904b-b119574dbda4}", "not-a-guid" });

        Assert.Equal(new Guid("fb49d77d-9040-4feb-904b-b119574dbda4"), Assert.Single(guids));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ParseInterfaceGuids_SingleStringAndMissingValue_AreHandled()
    {
        AssertSkippedOutsideWindows();

        Assert.Equal(new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed"),
            Assert.Single(WinUsbDriverInstaller.ParseInterfaceGuids("{a5dcbf10-6530-11d2-901f-00c04fb951ed}")));
        Assert.Empty(WinUsbDriverInstaller.ParseInterfaceGuids(null));
    }

    private static void AssertSkippedOutsideWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The WinUSB takeover helpers only apply to Windows.");
        }
    }
}