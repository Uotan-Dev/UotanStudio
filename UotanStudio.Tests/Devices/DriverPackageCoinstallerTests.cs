using UotanStudio.Assets;
using UotanStudio.Comm.Transports.Usb.Drivers;
using UotanStudio.Hash.Digests;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Verifies the materialized shape of the bundled ADB packages after the shared amd64 coinstallers moved into
/// their own bundle: each package still lands with the full directory layout its staged INF expects, the
/// shared payloads appear exactly once, and the declared file count matches what reaches the disk.
/// <para>验证共享 amd64 coinstaller 迁到独立资源包后随包 ADB 驱动包的物化形态：每个包仍然带着暂存 INF 所需的
/// 完整目录布局，共享负载只出现一次，声明的文件数与落盘结果一致。</para>
/// </summary>
public sealed class DriverPackageCoinstallerTests
{
    /// <summary>The coinstaller paths every WinUSB-binding package must carry after materialization.
    /// <para>每个绑定 WinUSB 的包物化后都必须携带的 coinstaller 路径。</para>
    /// </summary>
    private static readonly string[] SharedCoinstallerPaths =
    [
        "amd64/WdfCoInstaller01009.dll",
        "amd64/winusbcoinstaller2.dll",
    ];

    [Theory]
    [InlineData("google-usb")]
    [InlineData("rockchip-adb")]
    public void Materialize_WinUsbBindingPackage_WritesEverySharedCoinstallerOnce(string packageId)
    {
        using var temp = new TempDirectory();

        var materialized = DriverPackageAssets.Materialize(packageId, temp.Path);

        Assert.Equal(materialized.Count, materialized.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var relativePath in SharedCoinstallerPaths)
        {
            Assert.Contains(relativePath, materialized, StringComparer.OrdinalIgnoreCase);
            var written = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(written), $"{packageId} did not materialize {relativePath}");

            var entry = AssetCatalog.WinUsbCoinstaller[DriverPackageAssets.PayloadNameOf(relativePath)];
            var payload = File.ReadAllBytes(written);
            Assert.Equal(entry.Length, payload.LongLength);
            Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
        }

        var descriptor = DriverPackageCatalog.Find(packageId);
        Assert.NotNull(descriptor);
        Assert.Equal(materialized.Count, descriptor.FileCount);
    }

    [Fact]
    public void Materialize_MediaTekFullPackage_KeepsItsOwnCoinstallerCopy()
    {
        using var temp = new TempDirectory();

        var materialized = DriverPackageAssets.Materialize("mediatek-full", temp.Path);

        Assert.Equal(materialized.Count, materialized.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var own = AssetCatalog.MediaTekFullDriver["amd64__WdfCoInstaller01009.dll"];
        var written = Path.Combine(temp.Path, "amd64", "WdfCoInstaller01009.dll");
        Assert.Equal(own.Sha256, Sha256.ComputeHexLower(File.ReadAllBytes(written)));
        Assert.NotEqual(AssetCatalog.WinUsbCoinstaller["amd64__WdfCoInstaller01009.dll"].Sha256, own.Sha256);
    }
}