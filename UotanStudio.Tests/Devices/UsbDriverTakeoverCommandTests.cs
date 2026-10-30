using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Covers the elevated helper command line the USB takeover and the settings driver installer relaunch the
/// application with, and the row state that decides whether a driver package row offers its buttons.
/// <para>覆盖 USB 接管与设置页驱动安装器用于重启应用的提权助手命令行，以及决定驱动包行是否提供按钮的行状态。</para>
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

    [Fact]
    public void Run_IncompleteRequests_WriteFailureResultsWithoutTouchingDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The elevated driver helper only runs on Windows.");
        }

        var resultPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.usbdriver");
        try
        {
            Assert.Equal(1, UsbDriverElevatedCommand.Run(
                [UsbDriverElevatedCommand.TakeoverSwitch, UsbDriverElevatedCommand.ResultSwitch, resultPath]));
            Assert.Equal("Failed", File.ReadAllLines(resultPath)[0]);

            Assert.Equal(1, UsbDriverElevatedCommand.Run(
                [UsbDriverElevatedCommand.ReleaseSwitch, UsbDriverElevatedCommand.ResultSwitch, resultPath]));
            Assert.Equal("Failed", File.ReadAllLines(resultPath)[0]);

            Assert.Equal(1, UsbDriverElevatedCommand.Run(
                [UsbDriverElevatedCommand.PackageInstallSwitch, UsbDriverElevatedCommand.ResultSwitch, resultPath]));
            Assert.Equal("Failed", File.ReadAllLines(resultPath)[0]);
        }
        finally
        {
            File.Delete(resultPath);
        }
    }

    [Fact]
    public void CanOperate_AvailableIdlePackage_IsTrue()
    {
        var item = new DriverPackageItemViewModel("mediatek-vcom", "name", "coverage", "detail", isAvailable: true,
            unavailableText: string.Empty);

        Assert.True(item.CanOperate);
        Assert.False(item.IsUnavailable);
    }

    [Fact]
    public void CanOperate_UnavailableOrBusyPackage_IsFalse()
    {
        var unavailable = new DriverPackageItemViewModel("wch-serial", "name", "coverage", "detail", isAvailable: false,
            unavailableText: "reason");
        Assert.False(unavailable.CanOperate);
        Assert.True(unavailable.IsUnavailable);

        var busy = new DriverPackageItemViewModel("mediatek-vcom", "name", "coverage", "detail", isAvailable: true,
            unavailableText: string.Empty);
        busy.IsBusy = true;
        Assert.False(busy.CanOperate);
    }
}