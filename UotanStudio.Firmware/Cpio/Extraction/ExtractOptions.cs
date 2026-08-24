namespace UotanStudio.Firmware.Cpio.Extraction;

/// <summary>
/// Options for archive extraction behavior.
/// <para>归档解包行为选项。</para>
/// </summary>
public sealed class ExtractOptions
{
    /// <summary>
    /// Overwrites existing destination files when true.
    /// <para>为 true 时覆盖已存在的目标文件。</para>
    /// </summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Create missing parent directories for extracted files.
    /// When false, extraction fails if a parent directory does not already exist.
    /// <para>为已解包文件创建缺失的父目录；为 false 时若父目录不存在则解包失败。</para>
    /// </summary>
    public bool MakeDirectories { get; init; } = true;

    /// <summary>
    /// Policy for absolute archive entry paths.
    /// Default strips absolute prefixes and extracts relative to destination.
    /// <para>绝对归档条目路径的策略；默认去除绝对前缀并相对于目标目录解包。</para>
    /// </summary>
    public AbsolutePathPolicy AbsolutePathPolicy { get; init; } = AbsolutePathPolicy.StripRoot;

    /// <summary>
    /// Restores last modification timestamps on extracted file system nodes.
    /// <para>恢复已解包文件系统节点的最后修改时间戳。</para>
    /// </summary>
    public bool RestoreModificationTime { get; init; } = true;

    /// <summary>
    /// Best-effort restoration of Unix permission bits.
    /// Non-Unix platforms may ignore this option.
    /// <para>尽力恢复 Unix 权限位；非 Unix 平台可能忽略此选项。</para>
    /// </summary>
    public bool RestorePermissions { get; init; } = true;

    /// <summary>
    /// Best-effort restoration of Unix owner/group metadata.
    /// This may require elevated privileges and is ignored when unsupported.
    /// <para>尽力恢复 Unix 属主/属组元数据；可能需要提升权限，不受支持时忽略。</para>
    /// </summary>
    public bool RestoreOwnership { get; init; }

    /// <summary>
    /// Include only entries matching at least one wildcard pattern (for example: *.txt, dir/*).
    /// When empty, all entries are included.
    /// <para>仅包含匹配至少一个通配符模式的条目（例如：*.txt、dir/*）；为空时包含全部条目。</para>
    /// </summary>
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    /// <summary>
    /// Optional pattern file path, one pattern per line.
    /// <para>可选的模式文件路径，每行一个模式。</para>
    /// </summary>
    public string? PatternFilePath { get; init; }

    /// <summary>
    /// Inverts include-pattern selection (GNU --nonmatching style).
    /// <para>反转包含模式选择（GNU --nonmatching 风格）。</para>
    /// </summary>
    public bool NonMatching { get; init; }

    /// <summary>
    /// Exclude entries matching wildcard patterns. Applied after IncludePatterns.
    /// <para>排除匹配通配符模式的条目；在 IncludePatterns 之后应用。</para>
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// Optional exact-path rename map applied before include/exclude checks.
    /// Keys and values use archive-style relative paths.
    /// <para>可选：在包含/排除检查之前应用的精确路径重命名映射；键与值使用归档风格的相对路径。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> RenameMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Resolves hardlinks by inode only, ignoring device numbers.
    /// <para>仅按 inode 解析硬链接，忽略设备号。</para>
    /// </summary>
    public bool DeviceIndependentHardlinks { get; init; }
}