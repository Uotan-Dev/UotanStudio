using UotanStudio.Assets;
using UotanStudio.Comm.Transports.Usb.Drivers;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Verifies the device-to-package matching behind the USB tree driver installer: a bundled package matches
/// when one of the bundled INF files claims one of the device's hardware or compatible ids, catalog order
/// breaks ties, and ids no package claims match nothing.
/// <para>验证 USB 树驱动安装器背后的设备与驱动包匹配：任一内置 INF 声明了设备的硬件或兼容 ID 即为匹配，
/// 目录顺序决定并列时的结果，无包声明的 ID 匹配不到任何包。</para>
/// </summary>
public sealed class DriverPackageCatalogMatchTests
{
    [Fact]
    public void FindMatch_RockchipMaskromHardwareId_MatchesRockusbPackage()
    {
        var package = DriverPackageCatalog.FindMatch(
        [
            "USB\\VID_2207&PID_350B&REV_0302",
            "USB\\VID_2207&PID_350B"
        ]);

        Assert.Equal("rockusb", package?.Id);
    }

    [Fact]
    public void FindMatch_GoogleGenericAdbHardwareId_MatchesGoogleUsbPackage()
    {
        var package = DriverPackageCatalog.FindMatch(["USB\\VID_18D1&PID_4EE0"]);

        Assert.Equal("google-usb", package?.Id);
    }

    [Fact]
    public void FindMatch_MediaTekPreloaderHardwareId_MatchesVcomPackageBeforeFullPackage()
    {
        var package = DriverPackageCatalog.FindMatch(["USB\\VID_0E8D&PID_2000"]);

        Assert.Equal("mediatek-vcom", package?.Id);
    }

    [Fact]
    public void FindMatch_UnisocDownloadAndAdbHardwareIds_MatchesSpreadtrumPackage()
    {
        var download = DriverPackageCatalog.FindMatch([@"USB\VID_1782&PID_4D00"]);
        var adb = DriverPackageCatalog.FindMatch([@"USB\VID_1782&PID_5A5B&MI_01"]);
        var diag = DriverPackageCatalog.FindMatch([@"USB\VID_0525&PID_A4A7"]);

        Assert.Equal("unisoc-spreadtrum", download?.Id);
        Assert.Equal("unisoc-spreadtrum", adb?.Id);
        Assert.Equal("unisoc-spreadtrum", diag?.Id);
    }

    [Fact]
    public void Find_UnisocSpreadtrum_BundlesAllFourInfsForAmd64()
    {
        var package = DriverPackageCatalog.Find("unisoc-spreadtrum");

        Assert.NotNull(package);
        Assert.True(package.IsAvailable);
        Assert.All(["sprdadb.inf", "sprdvcom.inf", "sprdvmdm.inf", "rdavcom.inf"],
            inf => Assert.Contains(inf, package.InfFileNames));
        var paths = AssetCatalog.UnisocSpreadtrumDriver.Entries.Select(static entry => entry.Path).ToList();
        Assert.Contains("amd64__sprdvcom.sys", paths);
        Assert.Contains("amd64__winusbcoinstaller2.dll", paths);
        Assert.DoesNotContain(paths, static path => path.StartsWith("i386__", StringComparison.Ordinal));
    }

    [Fact]
    public void FindMatch_UsbUartBridgeVendorIds_MatchNothing()
    {
        Assert.Null(DriverPackageCatalog.Find("silabs-cp210x"));
        Assert.Null(DriverPackageCatalog.Find("ftdi-cdm"));
        Assert.Null(DriverPackageCatalog.FindMatch([@"USB\VID_10C4&PID_EA60"]));
        Assert.Null(DriverPackageCatalog.FindMatch([@"USB\VID_0403&PID_6001"]));
    }

    [Fact]
    public void FindMatch_UnknownDeviceId_MatchesNothing()
    {
        Assert.Null(DriverPackageCatalog.FindMatch(["USB\\VID_9999&PID_0001"]));
        Assert.Null(DriverPackageCatalog.FindMatch([]));
        Assert.Null(DriverPackageCatalog.FindMatch(null));
    }
}