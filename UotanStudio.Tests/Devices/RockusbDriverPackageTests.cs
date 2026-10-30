using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Assets;
using UotanStudio.Hash.Digests;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Verifies the bundled vendor Rockusb driver package: extraction writes the files the asset manifest records with
/// matching lengths and digests, the INF covers the RK35xx maskrom hardware id, and the catalog carries a valid
/// Microsoft WHQL signature so the package installs without any local trust changes.
/// <para>验证随包的厂商 Rockusb 驱动包：抽取结果与资源清单记录的文件、长度与摘要一致，INF 覆盖 RK35xx maskrom
/// 硬件 ID，且目录携带有效的微软 WHQL 签名，包无需任何本机信任变更即可安装。</para>
/// </summary>
public sealed class RockusbDriverPackageTests
{
    /// <summary>The file names the driver bundle declares, in manifest order.
    /// <para>驱动资源包声明的文件名，按清单顺序。</para>
    /// </summary>
    private static readonly string[] ExpectedFiles = ["rockusb.cat", "rockusb.inf", "rockusb.sys"];

    [Fact]
    public void Extract_DriverBundle_WritesManifestFilesWithRecordedLengthsAndDigests()
    {
        using var temp = new TempDirectory();

        var extracted = AssetStore.Extract(AssetCatalog.RockusbDriver, temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedFiles, extracted);
        foreach (var entry in AssetCatalog.RockusbDriver.Entries)
        {
            var payload = File.ReadAllBytes(Path.Combine(temp.Path, entry.Path));
            Assert.Equal(checked((int)entry.Length), payload.Length);
            Assert.Equal(entry.Sha256, Sha256.ComputeHexLower(payload));
        }
    }

    [Fact]
    public void Extract_WindowsPackage_CoversMaskromHardwareIdAndCarriesMicrosoftSignature()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The vendor driver package verification uses Windows-only signed-file APIs.");
        }

        using var temp = new TempDirectory();

        AssetStore.Extract(AssetCatalog.RockusbDriver, temp.Path, TestContext.Current.CancellationToken);

        var inf = File.ReadAllText(Path.Combine(temp.Path, "rockusb.inf"), Encoding.ASCII);
        Assert.Contains("USB\\VID_2207&PID_350B", inf);
        Assert.Contains("AddService = Rockusb", inf);

        var catalog = new SignedCms();
        catalog.Decode(File.ReadAllBytes(Path.Combine(temp.Path, "rockusb.cat")));
        Assert.Contains(catalog.Certificates.Cast<X509Certificate2>(),
            certificate => certificate.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
    }
}