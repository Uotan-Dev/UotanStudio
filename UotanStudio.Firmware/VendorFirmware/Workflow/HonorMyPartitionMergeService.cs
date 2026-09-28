using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Firmware.VendorFirmware.Workflow;

/// <summary>
/// Controls where a merged <c>my_*</c> partition is mounted, how its paths are rebased, and whether existing
/// destinations may be replaced.
/// <para>控制被合并的 <c>my_*</c> 分区的挂载位置、路径改基方式，以及是否允许替换已存在的目标。</para>
/// </summary>
internal sealed class HonorMyPartitionMergeOptions
{
    /// <summary>Partition name used for rebasing; <see langword="null"/> takes the leaf of the extracted directory.
    /// <para>改基使用的分区名；为 <see langword="null"/> 时取解包目录的末级名称。</para></summary>
    /// <value>An optional explicit name matching <c>[A-Za-z0-9_-]{1,64}</c>. <para>可选的显式名称，需匹配 <c>[A-Za-z0-9_-]{1,64}</c>。</para></value>
    public string? PartitionName { get; set; }

    /// <summary>Absolute file_contexts mount prefix, e.g. <c>/system</c>. <para>file_contexts 的绝对挂载前缀，例如 <c>/system</c>。</para></summary>
    /// <value>The prefix inserted before the partition name in every rebased expression. <para>该前缀会插入到每个改基表达式中分区名的前面。</para></value>
    public string MountPrefix { get; set; } = "/system";

    /// <summary>Relative fs_config root, e.g. <c>system/</c>; its first segment also locates the moved tree.
    /// <para>fs_config 的相对根前缀，例如 <c>system/</c>；其首段同时决定移动后树的位置。</para></summary>
    /// <value>The root prepended to every entry path during rebase. <para>改基时添加到每个条目路径之前的根。</para></value>
    public string FsConfigRoot { get; set; } = "system/";

    /// <summary>Runtime prefix of the imported build.prop path. <para>被导入 build.prop 路径的运行时前缀。</para></summary>
    /// <value>Combined as <c>&lt;prefix&gt;/&lt;partition&gt;/build.prop</c> inside the import directive. <para>在导入指令中组合为 <c>&lt;前缀&gt;/&lt;分区&gt;/build.prop</c>。</para></value>
    public string ImportPrefix { get; set; } = "/";

    /// <summary>Whether existing destination entries may be replaced. <para>是否允许替换已存在的目标条目。</para></summary>
    /// <value>Defaults to <see langword="true"/>; when <see langword="false"/> any destination conflict aborts before moving. <para>默认 <see langword="true"/>；为 <see langword="false"/> 时任何目标冲突都会在移动前中止。</para></value>
    public bool Overwrite { get; set; } = true;
}

/// <summary>
/// Outcome of a my-partition merge: the published tree location, move counts, and the rebase and import accounting.
/// <para>my 分区合并的结果：发布后的树位置、移动计数，以及改基与导入的统计信息。</para>
/// </summary>
/// <param name="MovedRoot">Absolute directory that received the moved partition tree. <para>接收移动后分区树的绝对目录。</para></param>
/// <param name="MovedEntries">Top-level entries relocated from the extraction. <para>从解包目录迁移的顶层条目数。</para></param>
/// <param name="FileContextPrefixed">file_contexts rules that received the mount prefix. <para>被加上挂载前缀的 file_contexts 规则数。</para></param>
/// <param name="FileContextDropped">file_contexts defaults and duplicates removed. <para>被移除的 file_contexts 默认与重复条目数。</para></param>
/// <param name="MetacharacterWarnings">Regex expressions that were kept verbatim by the rebase. <para>改基时保持原样的含正则表达式。</para></param>
/// <param name="FsConfigPrefixed">fs_config entries that received the root prefix. <para>被加上根前缀的 fs_config 条目数。</para></param>
/// <param name="FsConfigDropped">fs_config defaults and duplicates removed. <para>被移除的 fs_config 默认与重复条目数。</para></param>
/// <param name="BuildPropImported"><see langword="true"/> when the import directive is present after the merge.
/// <para>合并后存在导入指令时为 <see langword="true"/>。</para></param>
public sealed record HonorMyPartitionMergeResult(string MovedRoot, int MovedEntries, int FileContextPrefixed,
    int FileContextDropped, IReadOnlyList<string> MetacharacterWarnings, int FsConfigPrefixed,
    int FsConfigDropped, bool BuildPropImported);

/// <summary>
/// Merges an extracted Honor <c>my_*</c> partition into an extracted system tree: relocates the directory,
/// rebases the <c>file_contexts</c> and <c>fs_config</c> sidecars from the configuration directory, and appends
/// the <c>import /&lt;partition&gt;/build.prop</c> directive to the moved build.prop. Each published file is
/// replaced atomically and progress is reported through <see cref="IFirmwareProgress"/>.
/// <para>将解包的荣耀 <c>my_*</c> 分区合并进解包的 system 目录树：迁移目录、对配置目录中的 <c>file_contexts</c>
/// 与 <c>fs_config</c> 附属文件执行改基，并在迁移后的 build.prop 上追加 <c>import /&lt;分区&gt;/build.prop</c> 指令。
/// 每个发布文件均以原子方式替换，进度通过 <see cref="IFirmwareProgress"/> 报告。</para>
/// </summary>
internal sealed class HonorMyPartitionMergeService
{
    private const long MaximumSidecarBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Runs the merge described by <see cref="HonorMyPartitionMergeService"/>.
    /// <para>执行 <see cref="HonorMyPartitionMergeService"/> 所描述的合并。</para>
    /// </summary>
    /// <param name="myPartitionDirectory">
    /// Extracted my-partition root whose top-level entries are relocated. <para>待迁移其顶层条目的 my 分区解包根目录。</para>
    /// </param>
    /// <param name="targetSystemDirectory">
    /// Extracted system partition root receiving the moved tree under the configured relative root.
    /// <para>按配置的相对根接收迁移树的 system 分区解包根。</para>
    /// </param>
    /// <param name="configDirectory">
    /// Directory holding the <c>file_contexts</c> and <c>fs_config</c> sidecars; rebased copies replace them atomically.
    /// <para>持有 <c>file_contexts</c> 与 <c>fs_config</c> 附属文件的目录；改基后的副本以原子方式替换原文件。</para>
    /// </param>
    /// <param name="options">
    /// Merge placement; <see langword="null"/> uses defaults. <para>合并布局；为 <see langword="null"/> 时使用默认值。</para>
    /// </param>
    /// <param name="progress">
    /// Optional stage progress sink. <para>可选的阶段进度回调。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each phase; cancellation aborts before any destination file is replaced.
    /// <para>在每个阶段前检查；取消时不会替换任何目标文件。</para>
    /// </param>
    /// <returns>
    /// The merge outcome with move, rebase and import accounting. <para>包含移动、改基与导入统计的合并结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException">A path is blank or an option value is invalid. <para>路径为空或选项值无效。</para></exception>
    /// <exception cref="DirectoryNotFoundException">An input directory is missing. <para>输入目录缺失。</para></exception>
    /// <exception cref="InvalidDataException">A sidecar is unreadable, oversized or malformed. <para>附属文件不可读、超限或格式错误。</para></exception>
    /// <exception cref="IOException">A destination conflict blocks the move or publication fails. <para>目标冲突阻碍移动，或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<HonorMyPartitionMergeResult> MergeAsync(string myPartitionDirectory, string targetSystemDirectory,
        string configDirectory, HonorMyPartitionMergeOptions? options = null, IFirmwareProgress? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => MergeCore(myPartitionDirectory, targetSystemDirectory, configDirectory,
            options ?? new HonorMyPartitionMergeOptions(), progress, cancellationToken), cancellationToken);

    private static HonorMyPartitionMergeResult MergeCore(string myPartitionDirectory, string targetSystemDirectory,
        string configDirectory, HonorMyPartitionMergeOptions options, IFirmwareProgress? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(myPartitionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystemDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        if (!Directory.Exists(myPartitionDirectory))
            throw new DirectoryNotFoundException($"The extracted my-partition directory is missing: {myPartitionDirectory}");
        if (!Directory.Exists(targetSystemDirectory))
            throw new DirectoryNotFoundException($"The target system directory is missing: {targetSystemDirectory}");
        if (!Directory.Exists(configDirectory))
            throw new DirectoryNotFoundException($"The configuration directory is missing: {configDirectory}");
        cancellationToken.ThrowIfCancellationRequested();

        var partition = options.PartitionName
            ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(myPartitionDirectory)));
        if (partition.Length is < 1 or > 64 || partition.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not '_' and not '-'))
        {
            throw new InvalidDataException($"The my-partition name '{partition}' is not usable for rebasing.");
        }

        var rootRelative = options.FsConfigRoot.Trim('/');
        if (rootRelative.Length is 0 or > 4096 || rootRelative.Any(character => char.IsWhiteSpace(character) || character == '\\' || character == '#')
            || rootRelative.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException("fs_config root must be a safe relative directory path.");
        }

        var movedRoot = FileExtraction.ResolvePath(targetSystemDirectory, $"{rootRelative}/{partition}");
        progress?.Report(5, "Validating merge inputs");

        var sources = new List<(string Path, bool IsDirectory)>();
        foreach (var path in Directory.EnumerateFileSystemEntries(myPartitionDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
                continue;
            var destination = FileExtraction.ResolvePath(movedRoot, name);
            if (!options.Overwrite && (File.Exists(destination) || Directory.Exists(destination)))
                throw new IOException($"Destination already exists and overwrite is disabled: {destination}");
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && RepackMetadata.IsMetadataFile(path))
                continue;
            sources.Add((path, isDirectory));
        }

        Directory.CreateDirectory(movedRoot);
        foreach (var (path, isDirectory) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = FileExtraction.ResolvePath(movedRoot, Path.GetFileName(path));
            if (isDirectory)
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination, recursive: true);
                Directory.Move(path, destination);
            }
            else
            {
                File.Move(path, destination, overwrite: options.Overwrite);
            }
        }

        progress?.Report(40, $"Moved {sources.Count} entries under {rootRelative}/{partition}");
        var contexts = RebaseSidecar(Path.Combine(configDirectory, "file_contexts"),
            text => FileContextPatch.Rebase(text, options.MountPrefix, partition, cancellationToken),
            result => (result.Text, result.PrefixedRules, result.DroppedRules, (IReadOnlyList<string>?)result.MetacharacterWarnings),
            cancellationToken);
        progress?.Report(60, "Rebased file_contexts");
        var fsConfig = RebaseSidecar(Path.Combine(configDirectory, "fs_config"),
            text => FsConfigPatch.Rebase(text, options.FsConfigRoot, partition, cancellationToken),
            result => (result.Text, result.PrefixedEntries, result.DroppedEntries, (IReadOnlyList<string>?)null),
            cancellationToken);
        progress?.Report(75, "Rebased fs_config");

        var buildPropPath = Path.Combine(movedRoot, "build.prop");
        var imported = false;
        if (File.Exists(buildPropPath))
        {
            var trimmedImportPrefix = options.ImportPrefix.TrimEnd('/');
            var importPath = (trimmedImportPrefix.Length == 1 && trimmedImportPrefix[0] == '/'
                ? "" : trimmedImportPrefix) + "/" + partition + "/build.prop";
            imported = PublishTextSidecar(buildPropPath, text => AndroidPropertyPatch.ImportLine(text, importPath).Text,
                cancellationToken);
        }

        progress?.Report(100, "Merged my-partition tree and rebased metadata");
        return new HonorMyPartitionMergeResult(movedRoot, sources.Count,
            contexts.Prefixed, contexts.Dropped, contexts.Warnings ?? [],
            fsConfig.Prefixed, fsConfig.Dropped, imported);
    }

    private static (int Prefixed, int Dropped, IReadOnlyList<string>? Warnings) RebaseSidecar<T>(string path,
        Func<string, T> rebase, Func<T, (string Text, int Prefixed, int Dropped, IReadOnlyList<string>? Warnings)> project,
        CancellationToken cancellationToken)
    {
        var text = ReadSidecar(path);
        var (rebuilt, prefixed, dropped, warnings) = project(rebase(text));
        FileExtraction.WriteAtomic(path, output =>
            output.Write(Encoding.UTF8.GetBytes(rebuilt)), cancellationToken: cancellationToken);
        return (prefixed, dropped, warnings);
    }

    private static bool PublishTextSidecar(string path, Func<string, string> transform, CancellationToken cancellationToken)
    {
        var text = ReadSidecar(path);
        var result = transform(text);
        if (string.Equals(text, result, StringComparison.Ordinal))
            return true;
        FileExtraction.WriteAtomic(path, output =>
            output.Write(Encoding.UTF8.GetBytes(result)), cancellationToken: cancellationToken);
        return true;
    }

    private static string ReadSidecar(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Required metadata sidecar is missing: {path}");
        if (new FileInfo(path).Length > MaximumSidecarBytes)
            throw new InvalidDataException($"Metadata sidecar exceeds the 8 MiB limit: {path}");
        return Encoding.UTF8.GetString(File.ReadAllBytes(path));
    }
}