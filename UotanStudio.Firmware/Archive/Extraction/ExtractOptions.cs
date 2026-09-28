namespace UotanStudio.Firmware.Archive.Extraction;

/// <summary>
/// Immutable behaviour knobs for <see cref="CpioExtractor"/>: overwrite policy, path safety,
/// filtering and metadata restoration, with defaults aimed at direct extraction.
/// <para><see cref="CpioExtractor"/> 的不可变行为开关：覆盖策略、路径安全、过滤与元数据还原；
/// 默认值面向直接解包场景。</para>
/// </summary>
public sealed class ExtractOptions
{
    /// <summary>
    /// Cancels extraction between entries and during streamed payload copies.
    /// <para>在条目之间及流式复制负载时取消提取。</para>
    /// </summary>
    /// <value>The cancellation token for this extraction. <para>本次提取的取消令牌。</para></value>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Whether existing destination nodes are replaced.
    /// <para>是否替换目标位置上已存在的节点。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> (default) overwrites existing files, links and directories;
    /// <see langword="false"/> leaves an existing target untouched and skips the entry.
    /// <para>默认 <see langword="true"/> 会覆盖已存在的文件、链接与目录；<see langword="false"/>
    /// 保留既有目标并跳过该条目。</para>
    /// </value>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Whether missing parent directories are created automatically.
    /// <para>是否在父目录缺失时自动创建。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> (default) creates the parent chain; <see langword="false"/> makes the
    /// extractor throw <see cref="DirectoryNotFoundException"/> when it is <see langword="false"/>.
    /// <para>默认 <see langword="true"/> 会创建缺失的父级目录链；<see langword="false"/> 时解包器
    /// 抛出 <see cref="DirectoryNotFoundException"/>。</para>
    /// </value>
    public bool MakeDirectories { get; init; } = true;

    /// <summary>
    /// Policy applied to entries whose stored path is absolute.
    /// <para>对存储为绝对路径的条目所采用的策略。</para>
    /// </summary>
    /// <value>
    /// Defaults to <see cref="AbsolutePathPolicy.StripRoot"/>; <see cref="AbsolutePathPolicy.Reject"/>
    /// aborts such entries instead.
    /// <para>默认 <see cref="AbsolutePathPolicy.StripRoot"/>；改用
    /// <see cref="AbsolutePathPolicy.Reject"/> 时此类条目直接中止。</para>
    /// </value>
    public AbsolutePathPolicy AbsolutePathPolicy { get; init; } = AbsolutePathPolicy.StripRoot;

    /// <summary>
    /// Whether the entry's modification time is restored onto the extracted node.
    /// <para>是否把条目的修改时间还原到解出的节点上。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> (default) stamps files and directories with a positive Unix mtime;
    /// symbolic links are left untouched and non-positive values change nothing.
    /// <para>默认 <see langword="true"/> 会用正的 Unix 修改时间标记文件与目录；
    /// 符号链接不受影响，非正值不改动时间。</para>
    /// </value>
    public bool RestoreModificationTime { get; init; } = true;

    /// <summary>
    /// Whether the entry's Unix permission bits are applied to the extracted node.
    /// <para>是否把条目的 Unix 权限位应用到解出的节点。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> (default) sets the lower twelve permission bits on non-Windows
    /// systems, skipping symbolic links; failures are swallowed and Windows changes nothing.
    /// <para>默认 <see langword="true"/> 时在非 Windows 系统设置低 12 位权限并跳过符号链接；
    /// 失败被静默忽略，Windows 上不产生改动。</para>
    /// </value>
    public bool RestorePermissions { get; init; } = true;

    /// <summary>
    /// Ownership-restoration switch accepted by <see cref="CpioExtractor"/> with no observable
    /// effect.
    /// <para><see cref="CpioExtractor"/> 接受的属主还原开关，不产生可观察行为。</para>
    /// </summary>
    /// <value>
    /// <see langword="false"/> by default, and whichever value is set the extractor changes no
    /// owner or group.
    /// <para>默认 <see langword="false"/>；无论取值如何，解包器都不更改属主或属组。</para>
    /// </value>
    public bool RestoreOwnership { get; init; }

    /// <summary>
    /// Archive-relative patterns that limit which entries are extracted.
    /// <para>限定解出条目的归档相对模式。</para>
    /// </summary>
    /// <value>
    /// Empty by default, which extracts everything; a non-empty list enables the
    /// <see cref="NonMatching"/> selection rule.
    /// <para>默认为空即全部解出；非空时启用 <see cref="NonMatching"/> 选择规则。</para>
    /// </value>
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    /// <summary>
    /// Optional text file supplying additional include patterns.
    /// <para>提供附加包含模式的可选文本文件。</para>
    /// </summary>
    /// <value>
    /// Each trimmed non-blank line is appended to <see cref="IncludePatterns"/> for the extraction
    /// pass; <see langword="null"/>, blank or missing paths add nothing.
    /// <para>去空白后的非空行会追加到本次解包的 <see cref="IncludePatterns"/>；
    /// 为 <see langword="null"/>、空白或文件不存在时不追加任何内容。</para>
    /// </value>
    public string? PatternFilePath { get; init; }

    /// <summary>
    /// Whether the include filter keeps non-matching instead of matching entries.
    /// <para>包含过滤是保留非匹配条目还是匹配条目。</para>
    /// </summary>
    /// <value>
    /// <see langword="false"/> (default) extracts only entries matching the include patterns;
    /// <see langword="true"/> skips exactly those matches.
    /// <para>默认 <see langword="false"/> 仅解出匹配包含模式的条目；为 <see langword="true"/>
    /// 时恰好跳过这些匹配项。</para>
    /// </value>
    public bool NonMatching { get; init; }

    /// <summary>
    /// Patterns whose entries are never extracted.
    /// <para>命中即永不解出的模式。</para>
    /// </summary>
    /// <value>
    /// Empty by default; matches are dropped after the include filter has been applied.
    /// <para>默认为空；在包含过滤之后命中排除模式的条目会被丢弃。</para>
    /// </value>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// Case-sensitive mapping from normalized archive paths to replacement relative paths.
    /// <para>从规范化归档路径到替换后相对路径的区分大小写映射。</para>
    /// </summary>
    /// <value>
    /// Applied to entry names and link targets before filtering, independently of each other; the
    /// default map is empty.
    /// <para>解包时先于过滤步骤分别应用于条目名与链接目标，两者互不影响；默认映射为空。</para>
    /// </value>
    public IReadOnlyDictionary<string, string> RenameMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether hardlink identity ignores device numbers.
    /// <para>硬链接身份是否忽略设备号。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> matches hardlinks by inode number alone; <see langword="false"/>
    /// (default) requires inode plus device major and minor to match.
    /// <para><see langword="true"/> 时仅按 inode 号匹配硬链接；默认 <see langword="false"/>
    /// 还要求设备主、次号一致。</para>
    /// </value>
    public bool DeviceIndependentHardlinks { get; init; }
}