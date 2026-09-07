namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk;

public sealed class ApkInfo
{
    public string FilePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public int EntryCount { get; set; }

    public string PackageName { get; set; } = string.Empty;

    public string VersionName { get; set; } = string.Empty;

    public long VersionCode { get; set; }

    public long MinSdkVersion { get; set; }

    public long TargetSdkVersion { get; set; }

    public long CompileSdkVersion { get; set; }

    public List<string> Permissions { get; } = [];

    public List<string> Abis { get; } = [];

    public string? IconPath { get; set; }

    public byte[]? IconBytes { get; set; }

    public uint IconReference { get; set; }

    /// <summary>
    /// Produces a readable description summary (for the properties panel / logs).
    /// <para>生成可读描述摘要（属性面板/日志用）。</para>
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
