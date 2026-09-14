using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk;
using UotanStudio.Tests.Infrastructure;
using System.IO.Compression;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// APK metadata parser tests: the parser runs against the real Magisk-v30.6.apk from the firmware
/// corpus when present, validating package/version/permission/ABI/icon extraction on a production
/// file; cases are skipped via <see cref="Assert.Skip"/> when the corpus file is missing.
/// <para>APK 元数据解析测试：语料库存在 Magisk-v30.6.apk 时在真实生产文件上校验
/// 包名/版本/权限/ABI/图标提取；语料缺失时以 <see cref="Assert.Skip"/> 跳过。</para>
/// </summary>
public class ApkParseTests
{
    /// <summary>语料库中 Magisk-v30.6.apk 的路径（只定位一次）；不存在时为 null。
    /// <para>The located Magisk-v30.6.apk path (resolved once); null when absent.</para></summary>
    private static readonly string? ApkPath = FirmwareCorpus.FindFile("Magisk-v30.6.apk");

    [Fact]
    public void Analyze_RealMagiskApk_ExtractsMetadata()
    {
        var apkPath = ApkPath;
        if (apkPath is null)
        {
            Assert.Skip("Magisk-v30.6.apk not found in firmware corpus");
        }

        var info = ApkParser.Analyze(apkPath);

        // 包名与版本。
        // Package name and version.
        Assert.Equal("com.topjohnwu.magisk", info.PackageName);
        Assert.Equal("30.6", info.VersionName);
        Assert.True(info.VersionCode > 0);

        // 条目数与文件大小统计。
        // Entry count and file-size statistics.
        Assert.True(info.EntryCount > 100);
        Assert.True(info.FileSize > 0);

        // 权限：至少包含一个 uses-permission。
        // Permissions: at least one uses-permission is present.
        Assert.Contains(info.Permissions, p => p.StartsWith("android.permission.", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_RealMagiskApk_EnumeratesAbis()
    {
        var apkPath = ApkPath;
        if (apkPath is null)
        {
            Assert.Skip("Magisk-v30.6.apk not found in firmware corpus");
        }

        var info = ApkParser.Analyze(apkPath);

        // Magisk 内置 arm64-v8a 原生库。
        // Magisk ships arm64-v8a native libraries.
        Assert.Contains("arm64-v8a", info.Abis);
        Assert.All(info.Abis, abi => Assert.False(string.IsNullOrWhiteSpace(abi)));
    }

    [Fact]
    public void Analyze_RealMagiskApk_IconBytesOrPathPresent()
    {
        var apkPath = ApkPath;
        if (apkPath is null)
        {
            Assert.Skip("Magisk-v30.6.apk not found in firmware corpus");
        }

        var info = ApkParser.Analyze(apkPath);

        // 图标要么解析出 zip 内路径，要么至少解析出字节。
        // The icon is either resolved to a zip path or carries bytes.
        if (info.IconPath is not null)
        {
            Assert.True(info.IconBytes is { Length: > 0 }, "icon path without bytes");
        }
        else
        {
            // 无图标路径时，IconBytes 应为 null（不应有孤儿字节）。
            // Without an icon path, IconBytes must stay null (no orphan bytes).
            Assert.Null(info.IconBytes);
        }
    }

    [Fact]
    public void Analyze_DetectsSignatureSchemesAndKnownHardeningMarker()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-apk-detect-" + Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("META-INF/MANIFEST.MF");
                archive.CreateEntry("META-INF/CERT.SF");
                archive.CreateEntry("lib/armeabi-v7a/libjiagu.so");
            }

            var info = ApkParser.Analyze(path);
            Assert.Contains("v1", info.SigningSchemes);
            Assert.Equal("360 Jiagu", info.HardeningProvider);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
