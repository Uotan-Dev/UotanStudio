using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Covers the elevated helper command line the USB tree driver operations relaunch the application with.
/// <para>覆盖 USB 树驱动操作用于重启应用的提权助手命令行。</para>
/// </summary>
public sealed class UsbDriverTakeoverCommandTests
{
    [Fact]
    public void IsRequested_TakeoverAndReleaseSwitches_AreTrue()
    {
        Assert.True(UsbDriverElevatedCommand.IsRequested(
            [UsbDriverElevatedCommand.TakeoverSwitch, "USB\\VID_0E8D&PID_2000\\5&1&0", "USB\\VID_0E8D&PID_2000"]));
        Assert.True(UsbDriverElevatedCommand.IsRequested(
            [UsbDriverElevatedCommand.ReleaseSwitch, "USB\\VID_0E8D&PID_2000\\5&1&0"]));
        Assert.True(UsbDriverElevatedCommand.IsRequested([UsbDriverElevatedCommand.TakeoverSwitch]));
    }

    [Fact]
    public void IsRequested_PackageSwitches_AreTrue()
    {
        Assert.True(UsbDriverElevatedCommand.IsRequested(
            [UsbDriverElevatedCommand.PackageInstallSwitch, "mediatek-vcom"]));
        Assert.True(UsbDriverElevatedCommand.IsRequested(
            [UsbDriverElevatedCommand.PackageUninstallSwitch, "mediatek-vcom"]));
    }

    [Fact]
    public void IsRequested_UnrelatedSwitches_AreFalse()
    {
        Assert.False(UsbDriverElevatedCommand.IsRequested([]));
        Assert.False(UsbDriverElevatedCommand.IsRequested(["--usb-driver-packages"]));
    }
}