using UotanStudio.Firmware.Core;
namespace UotanStudio.Firmware.Core.Repacking;

/// <summary>
/// Explicit metadata context distinguishing "explicit project root", "explicit no-project local
/// mode" and "unspecified (legacy ambient default)". Unspecified keeps the historical behavior of
/// reading <see cref="RepackMetadata.DefaultMetadataRoot"/> at use time; the two explicit kinds
/// never read the ambient root and never cross-fallback (single-location rule unchanged).
/// <para>显式元数据上下文，区分「显式工程根」「显式无工程本地模式」与「未指定（沿用旧环境默认值）」。
/// Unspecified 保持历史行为：使用时读取 <see cref="RepackMetadata.DefaultMetadataRoot"/>；
/// 两个显式状态绝不读取环境根、也绝不交叉回退（单一位置规则不变）。</para>
/// </summary>
public readonly struct RepackMetadataContext
{
    private enum ContextKind
    {
        /// <summary>Unspecified: resolve via the ambient <see cref="RepackMetadata.DefaultMetadataRoot"/> like legacy callers.
        /// <para>未指定：像旧调用方一样经环境 <see cref="RepackMetadata.DefaultMetadataRoot"/> 解析。</para></summary>
        Unspecified,
        /// <summary>Explicit project root: only <c>{root}/{partition}/</c> is used.
        /// <para>显式工程根：只使用 <c>{root}/{分区名}/</c>。</para></summary>
        ProjectRoot,
        /// <summary>Explicit local mode: only the extraction directory itself is used.
        /// <para>显式本地模式：只使用解包目录自身。</para></summary>
        LocalDirectory,
    }

    private readonly ContextKind _kind;
    private readonly string _root;

    private RepackMetadataContext(ContextKind kind, string root)
    {
        _kind = kind;
        _root = root;
    }

    /// <summary>
    /// Gets a value indicating whether this context is unspecified (legacy ambient behavior).
    /// <para>获取一个值，指示上下文是否未指定（沿用旧环境行为）。</para>
    /// </summary>
    /// <value><see langword="true"/> when resolution still defers to the ambient metadata root; explicit contexts report <see langword="false"/>.
    /// <para>解析仍交由环境元数据根决定时为 <see langword="true"/>；显式上下文返回 <see langword="false"/>。</para></value>
    public bool IsUnspecified => _kind == ContextKind.Unspecified;

    /// <summary>
    /// Creates the unspecified context: resolution falls back to
    /// <see cref="RepackMetadata.DefaultMetadataRoot"/> exactly like the legacy entry points.
    /// <para>创建未指定上下文：解析回退到 <see cref="RepackMetadata.DefaultMetadataRoot"/>，
    /// 与旧入口完全一致。</para>
    /// </summary>
    /// <value>A context that resolves the metadata location at use time instead of at construction.
    /// <para>在使用时才解析元数据位置、而不是在构造时就固定的上下文。</para></value>
    public static RepackMetadataContext Unspecified => default;

    /// <summary>
    /// Creates an explicit project-root context. The root is used exclusively; a missing project
    /// location never falls back to the extraction directory.
    /// <para>创建显式工程根上下文。只使用该根目录；工程位置缺失时不回退到解包目录。</para>
    /// </summary>
    /// <param name="projectMetadataRoot">The consolidated metadata root (e.g. <c>{projectRoot}/.uotan/metadata</c>).
    /// <para>统一元数据根目录（如 <c>{项目根}/.uotan/metadata</c>）。</para></param>
    /// <returns>A context that resolves metadata only below <paramref name="projectMetadataRoot"/>.
    /// <para>只在 <paramref name="projectMetadataRoot"/> 之下解析元数据的上下文。</para></returns>
    /// <exception cref="ArgumentNullException"><c>projectMetadataRoot</c> is <see langword="null"/>.
    /// <para><c>projectMetadataRoot</c> 为 <see langword="null"/>。</para></exception>
    public static RepackMetadataContext FromProjectRoot(string projectMetadataRoot)
        => new(ContextKind.ProjectRoot, root: projectMetadataRoot ?? throw new ArgumentNullException(nameof(projectMetadataRoot)));

    /// <summary>
    /// Creates an explicit local-directory context. Only the extraction directory is used, even
    /// when an ambient metadata root is set.
    /// <para>创建显式本地目录上下文。只使用解包目录自身，即使环境元数据根已设置。</para>
    /// </summary>
    /// <value>A context that looks for metadata next to the extracted files and ignores ambient roots.
    /// <para>在解出的文件旁查找元数据、并忽略环境根目录的上下文。</para></value>
    public static RepackMetadataContext LocalDirectory => new(ContextKind.LocalDirectory, root: string.Empty);

    /// <summary>
    /// Resolves the metadata file path for an extraction directory under this context.
    /// <para>在该上下文下解析解包目录的元数据文件路径。</para>
    /// </summary>
    internal string? ResolveMetadataPath(string extractionDirectory)
    {
        switch (_kind)
        {
            case ContextKind.LocalDirectory:
                var localPath = Path.Combine(extractionDirectory, RepackMetadata.FileName);
                return File.Exists(localPath) ? localPath : null;
            case ContextKind.ProjectRoot:
                if (string.IsNullOrWhiteSpace(_root)) return null;
                var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionDirectory)));
                if (string.IsNullOrWhiteSpace(directoryName)) return null;
                foreach (var candidate in new[] { directoryName, RepackMetadata.DetectPartitionName(directoryName) })
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    var metaPath = Path.Combine(_root, candidate, RepackMetadata.FileName);
                    if (File.Exists(metaPath)) return metaPath;
                }

                return null;
            default:
                return RepackMetadata.ResolveAmbientMetadataPath(extractionDirectory);
        }
    }

    /// <summary>
    /// Freezes the ambient default metadata root for one operation, so later changes to that default
    /// cannot redirect the operation mid-way.
    /// <para>为单次操作固定当前的环境默认元数据根，之后该默认值的改动不会在操作过程中改变解析位置。</para>
    /// </summary>
    /// <returns>This instance when the context is already explicit; otherwise a project-root context
    /// for the current default, or a local-directory context when the default is empty or whitespace.
    /// <para>上下文已经显式时返回自身；否则返回以当前默认根为工程根的上下文，默认根为空或仅空白时返回本地目录上下文。</para></returns>
    public RepackMetadataContext Capture()
    {
        if (!IsUnspecified) return this;
        var root = RepackMetadata.DefaultMetadataRoot;
        return string.IsNullOrWhiteSpace(root) ? LocalDirectory : FromProjectRoot(root);
    }

    /// <summary>Resolves the write root; explicit local mode never uses the ambient root.
    /// <para>解析写入根；显式本地模式绝不使用环境根。</para></summary>
    internal string? ResolveWriteRoot(string? metadataRoot)
        => _kind switch
        {
            ContextKind.ProjectRoot => _root,
            ContextKind.LocalDirectory => null,
            _ => metadataRoot ?? RepackMetadata.DefaultMetadataRoot,
        };
}