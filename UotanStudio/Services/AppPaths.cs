using UotanStudio.Services.Logging;

namespace UotanStudio.Services;

/// <summary>
/// Single source of the writable per-user data root and its well-known subdirectories. Every persisted
/// application location (settings, keys, logs, caches) must resolve under <see cref="Root"/> so the app
/// stays compatible with read-only install locations such as MSIX packages and macOS app bundles.
/// <para>可写按用户数据根目录及其已知子目录的唯一来源。全部持久化位置（设置、密钥、日志、缓存）都必须
/// 解析到 <see cref="Root"/> 之下，以保持与 MSIX 包、macOS .app 包等只读安装位置的兼容。</para>
/// </summary>
/// <remarks>
/// The root follows the platform convention: <c>%APPDATA%\UotanStudio</c> on Windows,
/// <c>~/Library/Application Support/UotanStudio</c> on macOS and <c>~/.local/share/UotanStudio</c>
/// (XDG data home) on Linux. ADB and HDC key material intentionally keeps the ecosystem paths
/// (<c>~/.android</c>, <c>~/.hdc</c>) so device authorizations are shared with the standalone tools, and
/// one-time scratch data stays under <c>%TEMP%</c> by repository policy.
/// <para>根目录遵循平台约定：Windows 为 <c>%APPDATA%\UotanStudio</c>，macOS 为
/// <c>~/Library/Application Support/UotanStudio</c>，Linux 为 <c>~/.local/share/UotanStudio</c>（XDG 数据目录）。
/// ADB 与 HDC 密钥有意保留生态约定路径（<c>~/.android</c>、<c>~/.hdc</c>），以便与独立工具共享设备授权；
/// 一次性暂存数据按仓库规则留在 <c>%TEMP%</c>。</para>
/// </remarks>
public static class AppPaths
{
    /// <summary>
    /// Writable per-user data root hosting every persisted application location.
    /// <para>承载全部持久化位置的可写按用户数据根目录。</para>
    /// </summary>
    /// <value>
    /// <c>%APPDATA%\UotanStudio</c> on Windows, <c>~/Library/Application Support/UotanStudio</c> on macOS and
    /// <c>~/.local/share/UotanStudio</c> on Linux.
    /// <para>Windows 为 <c>%APPDATA%\UotanStudio</c>，macOS 为 <c>~/Library/Application Support/UotanStudio</c>，
    /// Linux 为 <c>~/.local/share/UotanStudio</c>。</para>
    /// </value>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Application log directory. <para>应用日志目录。</para></summary>
    /// <value><c><see cref="Root"/>/logs</c>. <para><c><see cref="Root"/>/logs</c>。</para></value>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Signature key library root. <para>签名密钥库根目录。</para></summary>
    /// <value><c><see cref="Root"/>/keys</c>. <para><c><see cref="Root"/>/keys</c>。</para></value>
    public static string Keys => Path.Combine(Root, "keys");

    /// <summary>Visual plugin installation directory. <para>可视化插件安装目录。</para></summary>
    /// <value><c><see cref="Root"/>/visual-plugins</c>. <para><c><see cref="Root"/>/visual-plugins</c>。</para></value>
    public static string VisualPlugins => Path.Combine(Root, "visual-plugins");

    /// <summary>Pinned publisher trust file. <para>已固定发布者的信任文件。</para></summary>
    /// <value><c><see cref="Root"/>/publishers/trust.json</c>. <para><c><see cref="Root"/>/publishers/trust.json</c>。</para></value>
    public static string PublisherTrustFile => Path.Combine(Root, "publishers", "trust.json");

    /// <summary>scrcpy server jar cache root. <para>scrcpy server jar 缓存根目录。</para></summary>
    /// <value><c><see cref="Root"/>/ScrcpyServer</c>. <para><c><see cref="Root"/>/ScrcpyServer</c>。</para></value>
    public static string ScrcpyServer => Path.Combine(Root, "ScrcpyServer");

    /// <summary>Per-RID native runtime library cache root. <para>各 RID 原生运行库缓存根目录。</para></summary>
    /// <value><c><see cref="Root"/>/Native</c>. <para><c><see cref="Root"/>/Native</c>。</para></value>
    public static string Native => Path.Combine(Root, "Native");

    /// <summary>Apple lockdown and remote-pairing record directory. <para>Apple lockdown 与 remote-pairing 配对记录目录。</para></summary>
    /// <value><c><see cref="Root"/>/apple-lockdown</c>. <para><c><see cref="Root"/>/apple-lockdown</c>。</para></value>
    public static string AppleLockdown => Path.Combine(Root, "apple-lockdown");

    /// <summary>Downloaded delivery tool cache (pinned platform-tools and zstd). <para>交付工具下载缓存（钉定的 platform-tools 与 zstd）。</para></summary>
    /// <value>The shared cache directory resolved by the delivery facade. <para>由交付门面解析的共享缓存目录。</para></value>
    public static string DeliveryTools => Pipeline.Delivery.DeliveryService.DefaultToolCacheDirectory;

    /// <summary>
    /// Default parent directory offered when creating a new solution. Solutions are user documents, so the
    /// default lives under the Documents folder instead of the read-only install directory.
    /// <para>新建解决方案时提供的默认父目录。解决方案属于用户文档，默认位置放在文档目录而不是只读安装目录。</para>
    /// </summary>
    /// <value><c>Documents/UotanStudio</c>, with profile and temporary fallbacks.
    /// <para><c>文档/UotanStudio</c>，带回退到用户主目录与临时目录。</para></value>
    public static string DefaultProjectsRoot { get; } = ComputeDefaultProjectsRoot();

    /// <summary>
    /// Moves legacy scattered data locations into <see cref="Root"/>. The operation is idempotent: a child
    /// is only moved when the source exists and the target does not, so repeated calls and partial previous
    /// migrations are safe. Call it once during process startup, before any service reads its storage path.
    /// <para>把遗留的分散数据位置搬入 <see cref="Root"/>。操作幂等：仅当源存在且目标不存在时才搬移，
    /// 重复调用与半完成的迁移都安全。在进程启动时、任何服务读取存储路径之前调用一次。</para>
    /// </summary>
    /// <remarks>
    /// macOS consolidates the pre-2026-11 XDG root; Windows consolidates the former
    /// <c>%LOCALAPPDATA%\UotanStudio</c> children (Apple pairing records, delivery tool cache). Failures are
    /// logged and leave both locations intact, so the app keeps working with a re-downloadable cache.
    /// <para>macOS 收编 2026-11 之前的 XDG 根目录；Windows 收编原 <c>%LOCALAPPDATA%\UotanStudio</c> 的子目录
    /// （Apple 配对记录、交付工具缓存）。失败会记录日志并保持两处原样，应用仍可借助可重新下载的缓存继续工作。</para>
    /// </remarks>
    public static void MigrateLegacyLocations()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                MigrateDirectoryContents(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio"), Root);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio");
                MigrateDirectoryContents(Path.Combine(legacy, "apple-lockdown"), AppleLockdown);
                MigrateDirectoryContents(Path.Combine(legacy, "delivery-tools"), DeliveryTools);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogService.Instance.Write(LogLevel.Warn, "AppPaths",
                $"Migrating legacy data locations failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves every child of <paramref name="sourceRoot"/> into <paramref name="targetRoot"/>, skipping children
    /// whose target already exists, then removes the emptied source root. A move that fails (for example across
    /// volumes) falls back to a copy followed by a best-effort source delete.
    /// <para>把 <paramref name="sourceRoot"/> 的全部子项搬入 <paramref name="targetRoot"/>，目标已存在的子项跳过，
    /// 随后删除已清空的源根目录。搬移失败（例如跨卷）时回退为复制加尽力删除源。</para>
    /// </summary>
    /// <param name="sourceRoot">Legacy directory to consolidate; missing directories are a no-op.
    /// <para>待收编的遗留目录；目录不存在时为空操作。</para></param>
    /// <param name="targetRoot">Destination directory, created when absent.
    /// <para>目标目录；不存在时创建。</para></param>
    internal static void MigrateDirectoryContents(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot)) return;
        var source = Path.GetFullPath(sourceRoot);
        var target = Path.GetFullPath(targetRoot);
        if (string.Equals(source, target,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(target);
        var children = new DirectoryInfo(source).EnumerateFileSystemInfos().ToArray();
        foreach (var child in children)
        {
            FileSystemInfo destination = child is DirectoryInfo
                ? new DirectoryInfo(Path.Combine(target, child.Name))
                : new FileInfo(Path.Combine(target, child.Name));
            MigrateChild(child, destination);
        }

        try { Directory.Delete(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void MigrateChild(FileSystemInfo source, FileSystemInfo target)
    {
        if (!source.Exists || target.Exists) return;
        try
        {
            if (source is DirectoryInfo directory)
            {
                Directory.Move(directory.FullName, target.FullName);
            }
            else
            {
                File.Move(source.FullName, target.FullName);
            }
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cross-volume or locked source: fall back to copying so the data still lands under the root.
        }

        if (source is DirectoryInfo copySource)
        {
            CopyTree(copySource, (DirectoryInfo)target);
        }
        else
        {
            File.Copy(source.FullName, target.FullName, overwrite: false);
        }
        try { source.Delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyTree(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var child in source.EnumerateFileSystemInfos())
        {
            if (child is DirectoryInfo directory)
            {
                CopyTree(directory, new DirectoryInfo(Path.Combine(target.FullName, child.Name)));
            }
            else
            {
                File.Copy(child.FullName, Path.Combine(target.FullName, child.Name), overwrite: false);
            }
        }
    }

    private static string ComputeRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "UotanStudio");
        }

        // Windows resolves to %APPDATA% (Roaming); Linux resolves to the XDG data home (~/.local/share).
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio");
    }

    private static string ComputeDefaultProjectsRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return string.IsNullOrWhiteSpace(documents)
            ? Path.Combine(Path.GetTempPath(), "UotanStudio-projects")
            : Path.Combine(documents, "UotanStudio");
    }
}