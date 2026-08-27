using System.Collections.Generic;

namespace UotanStudio.Firmware.Apk;

/// <summary>
/// <para>APK 元数据模型：从 APK 容器中提取的应用信息（包名、版本、权限、ABI、图标等）。</para>
/// APK metadata model: application information extracted from an APK container (package name,
/// version, permissions, ABIs, icon, etc.).
/// </summary>
public sealed class ApkInfo
{
    /// <summary>APK 文件路径。<para>APK file path.</para></summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。<para>File size in bytes.</para></summary>
    public long FileSize { get; set; }

    /// <summary>zip 条目总数。<para>Total zip entry count.</para></summary>
    public int EntryCount { get; set; }

    /// <summary>应用包名（manifest 的 package 属性）。<para>Application package name.</para></summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>版本名（versionName）。<para>Version name.</para></summary>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>版本号（versionCode）。<para>Version code.</para></summary>
    public long VersionCode { get; set; }

    /// <summary>最低 SDK（minSdkVersion）。<para>Minimum SDK version.</para></summary>
    public long MinSdkVersion { get; set; }

    /// <summary>目标 SDK（targetSdkVersion）。<para>Target SDK version.</para></summary>
    public long TargetSdkVersion { get; set; }

    /// <summary>编译 SDK（compileSdkVersion）。<para>Compile SDK version.</para></summary>
    public long CompileSdkVersion { get; set; }

    /// <summary>申请的权限列表（uses-permission）。<para>Requested permissions (uses-permission).</para></summary>
    public List<string> Permissions { get; } = [];

    /// <summary>原生库 ABI 列表（lib/ 目录名）。<para>Native-library ABI list (lib/ directory names).</para></summary>
    public List<string> Abis { get; } = [];

    /// <summary>应用图标在 zip 内的路径；未找到为 <c>null</c>。<para>Icon path inside the zip; <c>null</c> when not found.</para></summary>
    public string? IconPath { get; set; }

    /// <summary>应用图标字节（PNG/WebP 原始数据）；未找到为 <c>null</c>。<para>Icon bytes (raw PNG/WebP); <c>null</c> when not found.</para></summary>
    public byte[]? IconBytes { get; set; }

    /// <summary>
    /// <para>manifest 图标属性解析出的资源 ID（0xPPTTEEEE）；仅用于 ARSC 查表，不对外展示。</para>
    /// Resource ID resolved from the manifest icon attribute (0xPPTTEEEE); used only for ARSC
    /// lookup, not exposed.
    /// </summary>
    public uint IconReference { get; set; }

    /// <summary>
    /// <para>生成可读描述摘要（属性面板/日志用）。</para>
    /// Produces a readable description summary (for the properties panel / logs).
    /// </summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Android Package (APK)");
        if (PackageName.Length > 0)
        {
            sb.Append(" | package: ").Append(PackageName);
        }
        if (VersionName.Length > 0)
        {
            sb.Append(" | version: ").Append(VersionName);
        }
        sb.Append(" | abis: ").Append(Abis.Count);
        sb.Append(" | permissions: ").Append(Permissions.Count);
        sb.Append(" | entries: ").Append(EntryCount);
        return sb.ToString();
    }
}
