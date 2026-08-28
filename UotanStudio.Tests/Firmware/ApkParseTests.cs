using UotanStudio.Firmware.Apk;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// APK metadata parser tests: the parser analyzes a real APK when present on this developer machine
/// (skipped otherwise), validating package/version/permission/ABI/icon extraction on a production file.
/// <para>APK 元数据解析测试：本机存在真实 APK 时分析验证（否则跳过），在生产文件上校验
/// 包名/版本/权限/ABI/图标提取。</para>
/// </summary>
public class ApkParseTests
{
    private const string RealApkPath =
        @"C:\Users\28319\Desktop\新建文件夹\test\images\Magisk-v30.6.apk";

    [Fact]
    public void Analyze_RealMagiskApk_ExtractsMetadata()
    {
        if (!File.Exists(RealApkPath))
        {
            return; // 非本机环境：跳过真实文件测试。
            // Non-local environment: skip the real-file test.
        }

        var info = ApkParser.Analyze(RealApkPath);

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
        if (!File.Exists(RealApkPath))
        {
            return;
        }

        var info = ApkParser.Analyze(RealApkPath);

        // Magisk 内置 arm64-v8a 原生库。
        // Magisk ships arm64-v8a native libraries.
        Assert.Contains("arm64-v8a", info.Abis);
        Assert.All(info.Abis, abi => Assert.False(string.IsNullOrWhiteSpace(abi)));
    }

    [Fact]
    public void Analyze_RealMagiskApk_IconBytesOrPathPresent()
    {
        if (!File.Exists(RealApkPath))
        {
            return;
        }

        var info = ApkParser.Analyze(RealApkPath);

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
}