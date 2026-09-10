namespace UotanStudio.Firmware.SharedKernel;

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
    public bool IsUnspecified => _kind == ContextKind.Unspecified;

    /// <summary>
    /// Creates the unspecified context: resolution falls back to
    /// <see cref="RepackMetadata.DefaultMetadataRoot"/> exactly like the legacy entry points.
    /// <para>创建未指定上下文：解析回退到 <see cref="RepackMetadata.DefaultMetadataRoot"/>，
    /// 与旧入口完全一致。</para>
    /// </summary>
    public static RepackMetadataContext Unspecified => default;

    /// <summary>
    /// Creates an explicit project-root context. The root is used exclusively; a missing project
    /// location never falls back to the extraction directory.
    /// <para>创建显式工程根上下文。只使用该根目录；工程位置缺失时不回退到解包目录。</para>
    /// </summary>
    /// <param name="projectMetadataRoot"><para>统一元数据根目录（如 <c>{项目根}/.uotan/metadata</c>）。</para>The consolidated metadata root (e.g. <c>{projectRoot}/.uotan/metadata</c>).</param>
    public static RepackMetadataContext FromProjectRoot(string projectMetadataRoot)
        => new(ContextKind.ProjectRoot, root: projectMetadataRoot ?? throw new ArgumentNullException(nameof(projectMetadataRoot)));

    /// <summary>
    /// Creates an explicit local-directory context. Only the extraction directory is used, even
    /// when an ambient metadata root is set.
    /// <para>创建显式本地目录上下文。只使用解包目录自身，即使环境元数据根已设置。</para>
    /// </summary>
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
                // Same dual-candidate lookup as the ambient path: the extraction directory is usually
                // named after the source image while metadata is keyed by partition name.
                // 与环境路径相同的双候选查找：解包目录通常以源镜像命名，而元数据按键（分区名）存放。
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

    /// <summary>Freezes the legacy ambient default for one operation. 固定一次操作的旧环境默认值。</summary>
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
