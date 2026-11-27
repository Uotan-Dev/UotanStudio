using UotanStudio.Assets;
using UotanStudio.Comm.Transports.Usb.Drivers;
using UotanStudio.Net.Components.Root;
using UotanStudio.Pipeline.Delivery;
using UotanStudio.Services.PatchPackages;

namespace UotanStudio.Services.Toolbox;

/// <summary>
/// Kind of content the toolbox surfaces.
/// <para>工具箱呈现的内容类别。</para>
/// </summary>
internal enum ToolboxContentKind
{
    /// <summary>A bundle embedded in the product; its payload can be exported but never deleted.
    /// <para>内嵌在产物中的资源包；负载可以导出，但绝不删除。</para></summary>
    EmbeddedBundle,

    /// <summary>A cache filled by downloads that the application can always rebuild.
    /// <para>由下载填充、应用随时可以重建的缓存。</para></summary>
    DownloadCache,

    /// <summary>A vendor driver package that can be staged into the Windows driver store.
    /// <para>可以暂存到 Windows 驱动仓库的厂商驱动包。</para></summary>
    DriverPackage,

    /// <summary>A root patch package the application downloaded from a root project's releases.
    /// <para>应用从 root 项目 Release 下载的补丁包。</para></summary>
    RootPatchPackage,
}

/// <summary>
/// One toolbox row describing a single unit of embedded, downloaded or installable content.
/// <para>描述单份内嵌、已下载或可安装内容的工具箱行。</para>
/// </summary>
/// <param name="Kind">The content category.<para>内容类别。</para></param>
/// <param name="Id">Stable identifier; driver rows carry the catalog package id, cache rows the directory role and
/// root patch rows the upstream project key.<para>稳定标识；驱动行携带目录包标识，缓存行携带目录角色，补丁包行携带上游项目键。</para></param>
/// <param name="TitleKey">Locale key of the display name; <see langword="null"/> when the row supplies
/// <paramref name="LiteralTitle"/> instead.<para>显示名称的语言资源键；该行改用 <paramref name="LiteralTitle"/> 时为
/// <see langword="null"/>。</para></param>
/// <param name="LiteralTitle">A display name that is not translated, such as an upstream project name.
/// <para>不参与翻译的显示名称，例如上游项目名。</para></param>
/// <param name="DescriptionKey">Locale key of the description.<para>说明的语言资源键。</para></param>
/// <param name="DeclaredBytes">Size declared by the manifest or the package catalog.
/// <para>清单或驱动包目录声明的体积。</para></param>
/// <param name="PresentBytes">Size currently on disk; <c>0</c> when nothing is cached.
/// <para>当前磁盘上的体积；没有缓存时为 <c>0</c>。</para></param>
/// <param name="FileCount">Entries declared by the source; for caches the number of files on disk.
/// <para>来源声明的条目数；缓存为磁盘上的文件数。</para></param>
/// <param name="Locator">Absolute directory or cache path backing the row.
/// <para>该行背后的绝对目录或缓存路径。</para></param>
/// <param name="IsShipped">Whether this build actually carries the payload.
/// <para>本次构建是否真的随包该负载。</para></param>
/// <param name="CanPurge">Whether the derived cache of this row can be deleted.
/// <para>该行的派生缓存是否可删除。</para></param>
/// <param name="CanInstall">Whether the row offers a driver-staging action.
/// <para>该行是否提供驱动暂存动作。</para></param>
/// <param name="IsInstalled">Whether the driver package is currently staged in the Windows driver store; always
/// <see langword="false"/> for rows that are not driver packages.
/// <para>该驱动包当前是否已暂存到 Windows 驱动仓库；非驱动包的行始终为 <see langword="false"/>。</para></param>
/// <param name="Detail">Optional secondary detail such as a driver availability reason.
/// <para>可选附加细节，例如驱动不可用原因。</para></param>
/// <param name="Entries">The individual downloaded artifacts behind a cache or patch row, each with its own path;
/// <see langword="null"/> for rows that describe one indivisible payload.
/// <para>缓存行或补丁行背后逐个下载下来的产物，每个都带自己的路径；描述单一不可分负载的行则为
/// <see langword="null"/>。</para></param>
internal sealed record ToolboxContentRow(
    ToolboxContentKind Kind,
    string Id,
    string? TitleKey,
    string? LiteralTitle,
    string DescriptionKey,
    long DeclaredBytes,
    long PresentBytes,
    int FileCount,
    string? Locator,
    bool IsShipped,
    bool CanPurge,
    bool CanInstall,
    bool IsInstalled,
    string? Detail,
    IReadOnlyList<ToolboxContentEntry>? Entries = null);

/// <summary>
/// One downloaded artifact inside a cache or patch row, expanded on demand by the settings page.
/// <para>缓存行或补丁行内部的一个下载产物，由设置页按需展开显示。</para>
/// </summary>
/// <param name="Name">The library or version directory name as it appears on disk.
/// <para>在磁盘上出现的库名或版本目录名。</para></param>
/// <param name="Path">Absolute path of the artifact.<para>该产物的绝对路径。</para></param>
/// <param name="Bytes">Size of the artifact on disk.<para>该产物在磁盘上的体积。</para></param>
internal sealed record ToolboxContentEntry(string Name, string Path, long Bytes);

/// <summary>
/// Enumerates everything the toolbox ships or has downloaded, and performs the derived-cache operations the settings
/// page exposes: purging a rebuildable cache and exporting one embedded bundle to a directory the user picks.
/// <para>枚举工具箱随包分发或已下载的全部内容，并执行设置页暴露的派生缓存操作：清除可重建缓存，以及把单个内嵌
/// 资源包导出到用户选择的目录。</para>
/// </summary>
/// <remarks>
/// Deleting a derived cache never touches an embedded payload: the frames live in the assembly, so the next use either
/// re-materializes the bundle or re-downloads the archive. Locators are reported for display only; the view model
/// decides whether revealing a directory is offered for that row.
/// <para>删除派生缓存绝不触碰内嵌负载：帧位于程序集内，因此下次使用要么重新物化资源包，要么重新下载归档。路径只为
/// 展示而报告；是否对某行提供“打开目录”由视图模型决定。</para>
/// </remarks>
internal static class ToolboxContentService
{
    /// <summary>Prefix every vendor driver payload bundle is stored under.
    /// <para>每个厂商驱动负载资源包存放时所处的前缀。</para>
    /// </summary>
    private const string DriverBundlePrefix = "drivers/";

    /// <summary>The asset bundle keys whose payloads the toolbox exports as one selectable group.
    /// <para>工具箱作为单个可选组导出的资源包键。</para>
    /// </summary>
    private static readonly Dictionary<string, (string TitleKey, string DescriptionKey)> BundlePresentation =
        new(StringComparer.Ordinal)
        {
            [AssetNames.Bundles.Fonts] = ("Toolbox_BundleFonts", "Toolbox_BundleFontsHint"),
            [AssetNames.Bundles.OhosDevice] = ("Toolbox_BundleOhosDevice", "Toolbox_BundleOhosDeviceHint"),
            [AssetNames.Bundles.WinUsbCoinstaller] =
                ("Toolbox_BundleWinUsbCoinstaller", "Toolbox_BundleWinUsbCoinstallerHint"),
            [AssetNames.Bundles.DeliveryUpdater] = ("Toolbox_BundleDeliveryUpdater", "Toolbox_BundleDeliveryUpdaterHint"),
            [AssetNames.Bundles.MagicRules] = ("Toolbox_BundleMagicRules", "Toolbox_BundleMagicRulesHint"),
        };

    /// <summary>
    /// Builds every toolbox row: embedded bundles with their driver packages, rebuildable download caches, and the
    /// root patch packages already on disk.
    /// <para>构建全部工具箱行：内嵌资源包及其驱动包、可重建的下载缓存，以及磁盘上已有的 root 补丁包。</para>
    /// </summary>
    /// <returns>The rows in display order, drivers directly after the bundle that carries them.
    /// <para>按显示顺序排列的行，驱动包紧跟携带它的资源包。</para></returns>
    internal static IReadOnlyList<ToolboxContentRow> Enumerate()
    {
        List<ToolboxContentRow> rows = [];
        rows.AddRange(EnumerateBundles());
        rows.AddRange(EnumerateDrivers());
        rows.AddRange(EnumerateCaches());
        rows.AddRange(EnumerateRootPatches());
        return rows;
    }

    /// <summary>
    /// Purges one rebuildable download cache. Embedded bundles are rejected: their payload is not a cache.
    /// <para>清除一个可重建的下载缓存。内嵌资源包会被拒绝：它们的负载不是缓存。</para>
    /// </summary>
    /// <param name="row">The row to purge.<para>要清除的行。</para></param>
    /// <returns><see langword="true"/> when nothing remains on disk afterwards.
    /// <para>清除后磁盘上不再有残留时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.
    /// <para><paramref name="row"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The row is not a purgeable cache.
    /// <para>该行不是可清除的缓存。</para></exception>
    internal static bool Purge(ToolboxContentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanPurge || row.Locator is not { Length: > 0 } locator)
        {
            throw new InvalidOperationException(
                $"Toolbox row '{row.Id}' does not expose a purgeable cache. 工具箱行 '{row.Id}' 没有可清除的缓存。");
        }

        return DeleteDirectory(Path.GetFullPath(locator));
    }

    /// <summary>
    /// Deletes one downloaded artifact inside a cache or patch row, leaving every other version of that row in place.
    /// <para>删除缓存行或补丁行内部的单个已下载产物，该行的其它版本保持不动。</para>
    /// </summary>
    /// <param name="row">The row the entry belongs to.<para>该条目所属的行。</para></param>
    /// <param name="entry">The entry to delete.<para>要删除的条目。</para></param>
    /// <returns><see langword="true"/> when nothing remains at the entry path afterwards.
    /// <para>删除后该条目路径上不再有残留时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.
    /// <para>某个参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The row is not a purgeable cache or patch row, or the entry is not one
    /// of its declared artifacts.<para>该行不是可清除的缓存或补丁行，或该条目不是它声明的产物之一。</para></exception>
    internal static bool PurgeEntry(ToolboxContentRow row, ToolboxContentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(entry);
        if (!row.CanPurge || row.Entries is not { } entries || !entries.Contains(entry))
        {
            throw new InvalidOperationException(
                $"Toolbox row '{row.Id}' does not own the entry '{entry.Name}'. "
                + $"工具箱行 '{row.Id}' 未持有条目 '{entry.Name}'。");
        }

        var path = Path.GetFullPath(entry.Path);
        if (!IsUnderDerivedRoots(path))
        {
            throw new InvalidOperationException(
                $"Refusing to delete '{path}', which is not a derived toolbox cache. "
                + $"拒绝删除 '{path}'：它不是工具箱的派生缓存。");
        }

        if (Directory.Exists(path))
        {
            return DeleteDirectory(path);
        }

        if (!File.Exists(path))
        {
            return true;
        }

        File.Delete(path);
        return !File.Exists(path);
    }

    /// <summary>
    /// Exports every file of one embedded bundle into an existing directory.
    /// <para>把一个内嵌资源包的每个文件导出到已存在的目录。</para>
    /// </summary>
    /// <param name="bundleKey">The bundle key from <see cref="AssetCatalog"/>.<para><see cref="AssetCatalog"/> 中的资源包键。</para></param>
    /// <param name="destinationDirectory">Directory that receives the files; it is created when missing.
    /// <para>接收文件的目录；缺失时会创建。</para></param>
    /// <param name="cancellationToken">Cancels the loop between entries.
    /// <para>在条目之间取消循环。</para></param>
    /// <returns>The written file names.<para>已写入的文件名。</para></returns>
    /// <exception cref="ArgumentException">The bundle key is unknown or the directory is blank.
    /// <para>资源包键未知，或目录为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The bundle is not shipped in this build.
    /// <para>本次构建未随包该资源。</para></exception>
    /// <exception cref="InvalidDataException">A frame fails its manifest length or digest check.
    /// <para>某个帧未通过清单长度或摘要校验。</para></exception>
    internal static IReadOnlyList<string> ExportBundle(string bundleKey, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (FindBundle(bundleKey) is not { } bundle)
        {
            throw new ArgumentException(
                $"Unknown asset bundle '{bundleKey}'. 未知资源包 '{bundleKey}'。", nameof(bundleKey));
        }

        Directory.CreateDirectory(destinationDirectory);
        return AssetStore.Extract(bundle, destinationDirectory, cancellationToken);
    }

    /// <summary>
    /// Gets the materialization directory an embedded bundle currently occupies, or <see langword="null"/> when the
    /// bundle key is unknown.
    /// <para>获取内嵌资源包当前占用的物化目录；资源包键未知时为 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="bundleKey">The bundle key from <see cref="AssetCatalog"/>.
    /// <para><see cref="AssetCatalog"/> 中的资源包键。</para></param>
    /// <param name="digestPrefix">Receives the digest directory name used by the cache layout.
    /// <para>接收缓存布局使用的摘要目录名。</para></param>
    /// <returns>The absolute cache directory, or <see langword="null"/> when the bundle never materialized.
    /// <para>绝对缓存目录；资源包从未物化时为 <see langword="null"/>。</para></returns>
    internal static string? MaterializedDirectory(string bundleKey, out string digestPrefix)
    {
        digestPrefix = string.Empty;
        if (FindBundle(bundleKey) is not { } bundle || !bundle.IsPresent)
        {
            return null;
        }

        var digest = bundle.Digest;
        if (digest.Length < 16)
        {
            return null;
        }

        digestPrefix = digest[..16];
        var relative = bundle.Key.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Path.GetTempPath(), "UotanStudio", "assets", relative, digestPrefix);
    }

    /// <summary>
    /// Resolves one bundle by its catalog key, so the settings page never has to hold asset types itself.
    /// <para>按目录键解析单个资源包，使设置页无需自行持有资源类型。</para>
    /// </summary>
    /// <param name="bundleKey">The bundle key.<para>资源包键。</para></param>
    /// <returns>The bundle, or <see langword="null"/> when the key is unknown.
    /// <para>资源包；键未知时为 <see langword="null"/>。</para></returns>
    /// <summary>
    /// Resolves the asset bundle key that backs a row, so an export never has to guess how a driver package id maps to
    /// its bundle.<para>解析某行背后的资源包键，使导出流程无需猜测驱动包标识如何对应到资源包。</para>
    /// </summary>
    /// <param name="row">The row to resolve.<para>要解析的行。</para></param>
    /// <returns>The bundle key present in <see cref="AssetCatalog.Bundles"/>.
    /// <para><see cref="AssetCatalog.Bundles"/> 中存在的资源包键。</para></returns>
    /// <exception cref="ArgumentException">The row has no shipped bundle behind it.
    /// <para>该行背后没有随包的资源包。</para></exception>
    internal static string BundleKeyFor(ToolboxContentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Kind == ToolboxContentKind.DriverPackage)
        {
            var driverKey = DriverBundlePrefix + row.Id;
            if (FindBundle(driverKey) is { } driverBundle)
            {
                return driverBundle.Key;
            }
        }

        if (FindBundle(row.Id) is { } bundle)
        {
            return bundle.Key;
        }

        throw new ArgumentException(
            $"Toolbox row '{row.Id}' has no exportable asset bundle. "
            + $"工具箱行 '{row.Id}' 没有可导出的资源包。", nameof(row));
    }

    private static AssetBundle? FindBundle(string? bundleKey)
    {
        if (string.IsNullOrWhiteSpace(bundleKey))
        {
            return null;
        }

        foreach (var bundle in AssetCatalog.Bundles)
        {
            if (string.Equals(bundle.Key, bundleKey, StringComparison.Ordinal))
            {
                return bundle;
            }
        }

        return null;
    }

    private static IEnumerable<ToolboxContentRow> EnumerateBundles()
    {
        foreach (var bundle in AssetCatalog.Bundles)
        {
            if (!BundlePresentation.TryGetValue(bundle.Key, out var presentation))
            {
                continue;
            }

            long declared = 0;
            foreach (var entry in bundle.Entries)
            {
                declared += entry.Length;
            }

            var cacheDirectory = MaterializedDirectory(bundle.Key, out _);
            var present = cacheDirectory is null ? 0 : DirectoryBytes(cacheDirectory);
            yield return new ToolboxContentRow(
                ToolboxContentKind.EmbeddedBundle,
                bundle.Key,
                presentation.TitleKey,
                LiteralTitle: null,
                presentation.DescriptionKey,
                declared,
                present,
                bundle.Entries.Count,
                cacheDirectory,
                bundle.IsPresent,
                CanPurge: false,
                CanInstall: false,
                IsInstalled: false,
                Detail: null);
        }
    }

    private static IEnumerable<ToolboxContentRow> EnumerateDrivers()
    {
        foreach (var package in DriverPackageCatalog.Packages)
        {
            yield return new ToolboxContentRow(
                ToolboxContentKind.DriverPackage,
                package.Id,
                $"Toolbox_Driver{package.DisplayNameKey}",
                LiteralTitle: null,
                $"Toolbox_Driver{package.DisplayNameKey}Hint",
                package.TotalBytes,
                0,
                package.FileCount,
                Locator: null,
                package.IsAvailable,
                CanPurge: false,
                CanInstall: package.IsAvailable,
                IsInstalled: package.IsAvailable && IsDriverInstalled(package.Id),
                Detail: package.UnavailableReasonKey);
        }
    }

    /// <summary>
    /// Lists the rebuildable download caches the toolbox manages. The libusb runtime cache is deliberately absent:
    /// the native loader binds the library for the whole process, so purging it does not take effect until the next
    /// launch and is not a toolbox concern.
    /// <para>列出工具箱管理的可重建下载缓存。libusb 运行库缓存被有意排除：原生加载器为整个进程绑定该库，清除它要到
    /// 下次启动才生效，不属于工具箱的职责。</para>
    /// </summary>
    /// <returns>The cache descriptors in display order.<para>按显示顺序排列的缓存描述。</para></returns>
    private static (string Id, string TitleKey, string DescriptionKey, string Root)[] DownloadCacheCatalog() =>
    [
        ("delivery-tools", "Toolbox_CacheDeliveryTools", "Toolbox_CacheDeliveryToolsHint",
            DeliveryService.DefaultToolCacheDirectory),
        ("asset-cache", "Toolbox_CacheAsset", "Toolbox_CacheAssetHint",
            Path.Combine(Path.GetTempPath(), "UotanStudio", "assets")),
    ];

    private static IEnumerable<ToolboxContentRow> EnumerateCaches()
    {
        foreach (var cache in DownloadCacheCatalog())
        {
            var (bytes, files) = DirectoryStatistics(cache.Root);
            yield return new ToolboxContentRow(
                ToolboxContentKind.DownloadCache,
                cache.Id,
                cache.TitleKey,
                LiteralTitle: null,
                cache.DescriptionKey,
                bytes,
                bytes,
                files,
                cache.Root,
                IsShipped: true,
                CanPurge: bytes > 0,
                CanInstall: false,
                IsInstalled: false,
                Detail: null,
                CollectEntries(cache.Root));
        }
    }

    /// <summary>
    /// Lists one row per root project that already holds downloaded patch packages, so the settings page shows what
    /// the patching flow actually kept on disk rather than only aggregate bytes.
    /// <para>为每个已存放下载补丁包的 root 项目列出一行，使设置页展示修补流程实际留在磁盘上的内容，而不只是汇总字节。</para>
    /// </summary>
    /// <returns>Rows for sources whose download folder currently holds files.
    /// <para>下载目录当前含有文件的来源所对应的行。</para></returns>
    private static IEnumerable<ToolboxContentRow> EnumerateRootPatches()
    {
        var root = RootPatchPackageService.DefaultResourceRoot();
        foreach (var source in RootPatchSources.All)
        {
            var folder = Path.Combine(root, source.CommandFolderName());
            var (bytes, files) = DirectoryStatistics(folder);
            if (files == 0)
            {
                continue;
            }

            var key = source.Key();
            yield return new ToolboxContentRow(
                ToolboxContentKind.RootPatchPackage,
                key,
                TitleKey: null,
                LiteralTitle: key,
                "Toolbox_RootPatchHint",
                bytes,
                bytes,
                files,
                folder,
                IsShipped: false,
                CanPurge: bytes > 0,
                CanInstall: false,
                IsInstalled: false,
                Detail: null,
                CollectDownloadedFiles(folder));
        }
    }

    /// <summary>Reports whether one vendor driver package is currently staged in the Windows driver store.
    /// <para>判断某个厂商驱动包当前是否已暂存到 Windows 驱动仓库。</para>
    /// </summary>
    /// <param name="packageId">Driver package id from the catalog.<para>目录中的驱动包标识。</para></param>
    /// <returns><see langword="true"/> when the package is staged; the driver store only exists on Windows.
    /// <para>驱动包已暂存时为 <see langword="true"/>；驱动仓库仅存在于 Windows。</para></returns>
    private static bool IsDriverInstalled(string packageId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return DriverPackageInstaller.IsInstalled(packageId);
    }

    /// <summary>
    /// Expands one download cache into its top-level libraries and their version folders, newest name first, so the
    /// settings page can reveal exactly which artifacts were downloaded instead of only an aggregate size.
    /// <para>把一个下载缓存展开成顶层库及其版本目录（名称倒序），使设置页可以显示究竟下载了哪些产物，而不只是汇总体积。</para>
    /// </summary>
    /// <param name="root">Absolute cache root.<para>缓存根目录的绝对路径。</para></param>
    /// <returns>One entry per library or loose file; empty when nothing is cached.
    /// <para>每个库或散落文件一个条目；没有缓存时为空。</para></returns>
    private static IReadOnlyList<ToolboxContentEntry> CollectEntries(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            var entries = new List<ToolboxContentEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(path);
                if (Directory.Exists(path))
                {
                    foreach (var version in Directory.EnumerateDirectories(path))
                    {
                        entries.Add(new ToolboxContentEntry(
                            name + "/" + Path.GetFileName(version),
                            version,
                            DirectoryStatistics(version).Bytes));
                    }

                    if (Directory.EnumerateDirectories(path).Any())
                    {
                        continue;
                    }

                    entries.Add(new ToolboxContentEntry(name, path, DirectoryStatistics(path).Bytes));
                }
                else
                {
                    entries.Add(new ToolboxContentEntry(name, path, FileBytes(path)));
                }
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(right.Name, left.Name));
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Lists the individual package files a root project left on disk, which is what the patching flow actually
    /// downloaded.
    /// <para>列举某个 root 项目留在磁盘上的单个包文件，也就是修补流程实际下载的内容。</para>
    /// </summary>
    /// <param name="folder">Absolute download folder.<para>下载目录的绝对路径。</para></param>
    /// <returns>One entry per file, largest first.<para>每个文件一个条目，体积大的在前。</para></returns>
    private static IReadOnlyList<ToolboxContentEntry> CollectDownloadedFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            var entries = new List<ToolboxContentEntry>();

            foreach (var path in Directory.EnumerateDirectories(folder))
            {
                var statistics = DirectoryStatistics(path);
                if (statistics.Files == 0)
                {
                    continue;
                }

                entries.Add(new ToolboxContentEntry(Path.GetFileName(path), path, statistics.Bytes));
            }

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                entries.Add(new ToolboxContentEntry(Path.GetFileName(path), path, FileBytes(path)));
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(right.Name, left.Name));
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long FileBytes(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long DirectoryBytes(string directory) => DirectoryStatistics(directory).Bytes;

    private static (long Bytes, int Files) DirectoryStatistics(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }

        return (bytes, files);
    }

    private static bool DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        if (!IsUnderDerivedRoots(directory))
        {
            throw new InvalidOperationException(
                $"Refusing to purge '{directory}', which is not a derived toolbox cache. "
                + $"拒绝清除 '{directory}'：它不是工具箱的派生缓存。");
        }

        Directory.Delete(directory, recursive: true);
        return !Directory.Exists(directory);
    }

    /// <summary>
    /// Confines deletion to the derived roots the toolbox owns, so a misconfigured setting can never wipe a user
    /// directory.
    /// <para>把删除限制在工具箱持有的派生根目录内，使配置错误的设置绝不会清空用户目录。</para>
    /// </summary>
    /// <param name="directory">Absolute directory about to be deleted.<para>即将删除的绝对目录。</para></param>
    /// <returns><see langword="true"/> when the directory lies under one of the known cache roots.
    /// <para>该目录位于某个已知缓存根目录之下时为 <see langword="true"/>。</para></returns>
    private static bool IsUnderDerivedRoots(string directory)
    {
        foreach (var cache in DownloadCacheCatalog())
        {
            if (IsSameOrUnder(directory, cache.Root))
            {
                return true;
            }
        }

        var root = RootPatchPackageService.DefaultResourceRoot();
        foreach (var source in RootPatchSources.All)
        {
            if (IsSameOrUnder(directory, Path.Combine(root, source.CommandFolderName())))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameOrUnder(string directory, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedDirectory = Path.GetFullPath(directory);
        return normalizedDirectory.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), comparison)
            || normalizedDirectory.StartsWith(normalizedRoot, comparison);
    }
}