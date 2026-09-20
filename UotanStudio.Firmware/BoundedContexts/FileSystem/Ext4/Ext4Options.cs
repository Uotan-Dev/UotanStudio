namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed class Ext4Options
{
    public int BlockSize { get; init; } = 4096;

    public int InodeSize { get; init; } = 256;

    public bool Ext4Compress { get; set; } = false;

    public bool EnableSparse { get; set; } = false;

    /// <summary>Filesystem volume label, limited to 16 UTF-8 bytes.</summary>
    public string VolumeName { get; init; } = "UOTANSTUDIO";

    public byte[]? Uuid { get; init; }

    public bool PreservePermissions { get; set; } = true;

    public bool PreserveTimestamps { get; set; } = true;

    public bool EnableMetadataCsum { get; set; } = true;

    public bool IncludeJournal { get; set; } = true;

    /// <summary>
    /// Optional path to an Android-style <c>fs_config</c> file (<c>path uid gid mode capabilities</c>)
    /// applied over the source tree; matching entries override the scanned uid/gid/mode.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件路径（<c>path uid gid mode capabilities</c>），
    /// 覆盖源树扫描结果：命中的条目以文件中的 uid/gid/mode 为准。</para>
    /// </summary>
    public string? FsConfigPath { get; init; }

    /// <summary>
    /// Optional path to an Android-style <c>file_contexts</c> file (<c>path label</c>) applied over
    /// the source tree; matching entries are stored as <c>security.selinux</c> xattrs.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件路径（<c>path label</c>），
    /// 命中的条目以 <c>security.selinux</c> xattr 写入镜像。</para>
    /// </summary>
    public string? FileContextsPath { get; init; }

    /// <summary>Preloaded override tables; when set they take precedence over the path-based sources above.</summary>
    internal IReadOnlyDictionary<string, UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata.FsConfigEntry>? FsConfig { get; set; }
    internal IReadOnlyDictionary<string, string>? FileContexts { get; set; }
}