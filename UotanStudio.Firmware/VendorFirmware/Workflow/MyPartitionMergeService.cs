using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Firmware.VendorFirmware.Workflow;

/// <summary>
/// Controls where a merged <c>my_*</c> partition is mounted, how its paths are rebased, and how the
/// extraction metadata participates in the merge.
/// <para>控制被合并的 <c>my_*</c> 分区的挂载位置、路径改基方式，以及解包元数据如何参与合并。</para>
/// </summary>
public sealed class MyPartitionMergeOptions
{
    /// <summary>Partition name used for rebasing; <see langword="null"/> takes the leaf of the extracted directory.
    /// <para>改基使用的分区名；为 <see langword="null"/> 时取解包目录的末级名称。</para></summary>
    /// <value>An optional explicit name matching <c>[A-Za-z0-9_-]{1,64}</c>. <para>可选的显式名称，需匹配 <c>[A-Za-z0-9_-]{1,64}</c>。</para></value>
    public string? PartitionName { get; set; }

    /// <summary>Absolute file_contexts mount prefix, e.g. <c>/system</c>; it also forms the imported build.prop path.
    /// <para>file_contexts 的绝对挂载前缀，例如 <c>/system</c>；同时构成被导入 build.prop 的路径。</para></summary>
    /// <value>The prefix inserted before the partition name in every rebased expression and import directive. <para>该前缀会插入到每个改基表达式与导入指令中分区名的前面。</para></value>
    public string MountPrefix { get; set; } = "/system";

    /// <summary>Relative fs_config root, e.g. <c>system/</c>; its first segment also locates the moved tree.
    /// <para>fs_config 的相对根前缀，例如 <c>system/</c>；其首段同时决定移动后树的位置。</para></summary>
    /// <value>The root prepended to every entry path during rebase. <para>改基时添加到每个条目路径之前的根。</para></value>
    public string FsConfigRoot { get; set; } = "system/";

    /// <summary>Whether existing destination entries may be replaced. <para>是否允许替换已存在的目标条目。</para></summary>
    /// <value>Defaults to <see langword="true"/>; when <see langword="false"/> any destination conflict aborts before moving. <para>默认 <see langword="true"/>；为 <see langword="false"/> 时任何目标冲突都会在移动前中止。</para></value>
    public bool Overwrite { get; set; } = true;

    /// <summary>Whether a missing <c>file_contexts</c> or <c>fs_config</c> sidecar skips its rebase instead of failing.
    /// <para>缺失 <c>file_contexts</c> 或 <c>fs_config</c> 附属文件时，是跳过对应改基而不是失败。</para></summary>
    /// <value>Defaults to <see langword="false"/>, preserving the strict contract; when <see langword="true"/>, sidecars
    /// absent from every candidate location are skipped and reported as not rebased. <para>默认 <see langword="false"/>，
    /// 保持严格契约；为 <see langword="true"/> 时，所有候选位置都缺失的附属文件会被跳过并在结果中报告为未改基。</para></value>
    public bool SkipMissingSidecars { get; set; }

    /// <summary>The merged partition's consolidated metadata directory, e.g.
    /// <c>&lt;projectRoot&gt;/.uotan/metadata/my_product_a</c>: its <c>&lt;partition&gt;_file_contexts</c> and
    /// <c>&lt;partition&gt;_fs_config</c> sidecars are rebased in place and folded into the system sidecars, and its
    /// <c>metadata.json</c> preservation payload is folded into the system manifest.
    /// <para>被合并分区的统一元数据目录，例如 <c>&lt;projectRoot&gt;/.uotan/metadata/my_product_a</c>：其中的
    /// <c>&lt;分区&gt;_file_contexts</c> 与 <c>&lt;分区&gt;_fs_config</c> 附属文件原地改基并折叠进 system 附属文件，
    /// 其 <c>metadata.json</c> 保留负载折叠进 system 清单。</para></summary>
    /// <value><see langword="null"/> skips both folds. <para>为 <see langword="null"/> 时跳过两类折叠。</para></value>
    public string? MyMetadataDirectory { get; set; }

    /// <summary>The system partition's consolidated metadata directory, e.g.
    /// <c>&lt;projectRoot&gt;/.uotan/metadata/system_a</c>; it receives the folded sidecars and the folded
    /// preservation manifest.
    /// <para>system 分区的统一元数据目录，例如 <c>&lt;projectRoot&gt;/.uotan/metadata/system_a</c>；
    /// 接收折叠后的附属文件与保留清单。</para></summary>
    /// <value><see langword="null"/> skips both folds. <para>为 <see langword="null"/> 时跳过两类折叠。</para></value>
    public string? SystemMetadataDirectory { get; set; }

    /// <summary>The system partition's own name, e.g. <c>system</c> or <c>system_a</c>; it names the system sidecar
    /// files and validates the folded manifest's identity.
    /// <para>system 分区自身的名称，例如 <c>system</c> 或 <c>system_a</c>；用于命名 system 附属文件并校验折叠清单的身份。</para></summary>
    /// <value><see langword="null"/> skips both folds. <para>为 <see langword="null"/> 时跳过两类折叠。</para></value>
    public string? SystemPartitionName { get; set; }
}

/// <summary>
/// Outcome of a my-partition merge: the published tree location, move counts, and the rebase, import and
/// preservation-fold accounting.
/// <para>my 分区合并的结果：发布后的树位置、移动计数，以及改基、导入与保留负载折叠的统计信息。</para>
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
/// <param name="FileContextsRebased"><see langword="true"/> when a file_contexts sidecar was found and rebased.
/// <para>找到并改基了 file_contexts 附属文件时为 <see langword="true"/>。</para></param>
/// <param name="FsConfigRebased"><see langword="true"/> when an fs_config sidecar was found and rebased.
/// <para>找到并改基了 fs_config 附属文件时为 <see langword="true"/>。</para></param>
/// <param name="PreservationFolded"><see langword="true"/> when the partition's preservation payload was folded
/// into the system manifest. <para>分区保留负载已折叠进 system 清单时为 <see langword="true"/>。</para></param>
/// <param name="FoldedInodeEntries">Preserved inode entries added to the system manifest by the fold.
/// <para>折叠时加入 system 清单的保留 inode 条目数。</para></param>
public sealed record MyPartitionMergeResult(string MovedRoot, int MovedEntries, int FileContextPrefixed,
    int FileContextDropped, IReadOnlyList<string> MetacharacterWarnings, int FsConfigPrefixed,
    int FsConfigDropped, bool BuildPropImported, bool FileContextsRebased, bool FsConfigRebased,
    bool PreservationFolded, int FoldedInodeEntries);

/// <summary>
/// Merges an extracted <c>my_*</c> partition into an extracted system tree: relocates the directory under its slot-stripped name, rebases the <c>file_contexts</c> and <c>fs_config</c> sidecars, appends the <c>import &lt;mountPrefix&gt;/&lt;partition&gt;/build.prop</c> directive to the system build.prop, and folds the sidecars into the system metadata so a preserved rebuild keeps ownership, permissions, SELinux labels and symbolic links.
/// <para>将解包的 <c>my_*</c> 分区合并进解包的 system 目录树：按去槽位后的名称迁移目录、改基 <c>file_contexts</c> 与 <c>fs_config</c> 附属文件、向 system 的 build.prop 追加 <c>import &lt;mountPrefix&gt;/&lt;partition&gt;/build.prop</c> 指令，并把附属文件折入 system 元数据，使保留式重建保留所有权、权限、SELinux 标签与符号链接。</para>
/// </summary>
public sealed class MyPartitionMergeService
{
    private const long MaximumSidecarBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Runs the merge described by <see cref="MyPartitionMergeService"/>.
    /// <para>执行 <see cref="MyPartitionMergeService"/> 所描述的合并。</para>
    /// </summary>
    /// <param name="myPartitionDirectory">
    /// Extracted my-partition root whose top-level entries are relocated. <para>待迁移其顶层条目的 my 分区解包根目录。</para>
    /// </param>
    /// <param name="targetSystemDirectory">
    /// Extracted system partition root receiving the moved tree under the configured relative root.
    /// <para>按配置的相对根接收迁移树的 system 分区解包根。</para>
    /// </param>
    /// <param name="configDirectory">
    /// Directory holding the plain <c>file_contexts</c> and <c>fs_config</c> sidecars; <see langword="null"/> falls
    /// back to the consolidated metadata sidecars and the moved partition root itself. Rebased copies replace the
    /// originals atomically. <para>持有裸名 <c>file_contexts</c> 与 <c>fs_config</c> 附属文件的目录；为
    /// <see langword="null"/> 时依次回退到统一元数据附属文件与移动后分区根目录自带的附属文件。
    /// 改基后的副本以原子方式替换原文件。</para>
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
    /// The merge outcome with move, rebase, import and fold accounting. <para>包含移动、改基、导入与折叠统计的合并结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException">A path is blank or an option value is invalid. <para>路径为空或选项值无效。</para></exception>
    /// <exception cref="DirectoryNotFoundException">An input directory is missing. <para>输入目录缺失。</para></exception>
    /// <exception cref="InvalidDataException">A sidecar is unreadable, oversized or malformed. <para>附属文件不可读、超限或格式错误。</para></exception>
    /// <exception cref="IOException">A destination conflict blocks the move or publication fails. <para>目标冲突阻碍移动，或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<MyPartitionMergeResult> MergeAsync(string myPartitionDirectory, string targetSystemDirectory,
        string? configDirectory, MyPartitionMergeOptions? options = null, IFirmwareProgress? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(async () => await MergeCoreAsync(myPartitionDirectory, targetSystemDirectory, configDirectory,
            options ?? new MyPartitionMergeOptions(), progress, cancellationToken).ConfigureAwait(false), cancellationToken);

    private static async Task<MyPartitionMergeResult> MergeCoreAsync(string myPartitionDirectory, string targetSystemDirectory,
        string? configDirectory, MyPartitionMergeOptions options, IFirmwareProgress? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(myPartitionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystemDirectory);
        if (!Directory.Exists(myPartitionDirectory))
            throw new DirectoryNotFoundException($"The extracted my-partition directory is missing: {myPartitionDirectory}");
        if (!Directory.Exists(targetSystemDirectory))
            throw new DirectoryNotFoundException($"The target system directory is missing: {targetSystemDirectory}");
        if (configDirectory is not null && !Directory.Exists(configDirectory))
            throw new DirectoryNotFoundException($"The configuration directory is missing: {configDirectory}");
        cancellationToken.ThrowIfCancellationRequested();

        var fullName = options.PartitionName
            ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(myPartitionDirectory)));
        if (fullName.Length is < 1 or > 64 || fullName.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not '_' and not '-'))
        {
            throw new InvalidDataException($"The my-partition name '{fullName}' is not usable for rebasing.");
        }
        var partition = RepackMetadata.StripSlotSuffix(fullName);

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
            var isDirectory = Directory.Exists(path);
            if (isDirectory && name.Equals("lost+found", StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = FileExtraction.ResolvePath(movedRoot, name);
            if (!options.Overwrite && (File.Exists(destination) || Directory.Exists(destination)))
                throw new IOException($"Destination already exists and overwrite is disabled: {destination}");
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
        var contexts = RebaseSidecar(FindSidecar("file_contexts", fullName, configDirectory, options, movedRoot),
            "file_contexts", fullName, options.SkipMissingSidecars,
            text => FileContextPatch.Rebase(text, options.MountPrefix, partition, cancellationToken),
            result => (result.Text, result.PrefixedRules, result.DroppedRules, (IReadOnlyList<string>?)result.MetacharacterWarnings),
            cancellationToken);
        progress?.Report(60, "Rebased file_contexts");
        var fsConfig = RebaseSidecar(FindSidecar("fs_config", fullName, configDirectory, options, movedRoot),
            "fs_config", fullName, options.SkipMissingSidecars,
            text => FsConfigPatch.Rebase(text, options.FsConfigRoot, partition, cancellationToken),
            result => (result.Text, result.PrefixedEntries, result.DroppedEntries, (IReadOnlyList<string>?)null),
            cancellationToken);
        progress?.Report(75, "Rebased fs_config");

        var imported = PublishBuildPropImport(targetSystemDirectory, rootRelative, partition, options, cancellationToken);
        var (folded, foldedEntries) = await FoldPreservationAsync(options, partition, fullName, rootRelative, cancellationToken).ConfigureAwait(false);
        progress?.Report(100, "Merged my-partition tree, rebased metadata and folded preservation");
        return new MyPartitionMergeResult(movedRoot, sources.Count,
            contexts.Prefixed, contexts.Dropped, contexts.Warnings ?? [],
            fsConfig.Prefixed, fsConfig.Dropped, imported, contexts.Rebased, fsConfig.Rebased,
            folded, foldedEntries);
    }

    /// <summary>
    /// Locates one sidecar by its bare name: the explicit configuration directory first, then the consolidated
    /// metadata directory under the slotted partition name, then the moved partition root.
    /// <para>按裸名定位附属文件：先显式配置目录，再统一元数据目录（带槽位分区名），最后移动后的分区根目录。</para>
    /// </summary>
    private static string? FindSidecar(string bareName, string fullName, string? configDirectory,
        MyPartitionMergeOptions options, string movedRoot)
    {
        string? existing(string? directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;
            var path = Path.Combine(directory, fileName);
            return File.Exists(path) ? path : null;
        }
        return existing(configDirectory, bareName)
            ?? existing(options.MyMetadataDirectory, fullName + "_" + bareName)
            ?? existing(movedRoot, bareName);
    }

    /// <summary>
    /// Appends the <c>import &lt;mountPrefix&gt;/&lt;partition&gt;/build.prop</c> directive to the first existing
    /// system build.prop: the fs-config-root copy (e.g. <c>system/build.prop</c>), the nested
    /// <c>system/system/build.prop</c>, then the tree-root <c>build.prop</c>; a tree without any build.prop skips
    /// the import. The moved partition's own build.prop is never modified.
    /// <para>向第一个存在的 system build.prop 追加 <c>import &lt;挂载前缀&gt;/&lt;分区&gt;/build.prop</c> 指令：
    /// 依次尝试 fs-config 根下的副本（如 <c>system/build.prop</c>）、嵌套的 <c>system/system/build.prop</c>，
    /// 再到树根的 <c>build.prop</c>；没有任何 build.prop 时跳过导入。被移动分区自己的 build.prop 不会被修改。</para>
    /// </summary>
    private static bool PublishBuildPropImport(string targetSystemDirectory, string rootRelative, string partition,
        MyPartitionMergeOptions options, CancellationToken cancellationToken)
    {
        var mountPrefix = options.MountPrefix.TrimEnd('/');
        var importPath = mountPrefix + "/" + partition + "/build.prop";
        string[] candidates =
        [
            FileExtraction.ResolvePath(targetSystemDirectory, $"{rootRelative}/build.prop"),
            FileExtraction.ResolvePath(targetSystemDirectory, "system/system/build.prop"),
            FileExtraction.ResolvePath(targetSystemDirectory, "build.prop")
        ];
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate)) continue;
            return PublishTextSidecar(candidate, text => AndroidPropertyPatch.ImportLine(text, importPath).Text,
                cancellationToken);
        }
        return false;
    }

    /// <summary>
    /// Folds the rebased partition sidecars into the system sidecar files (stale
    /// <c>system/&lt;partition&gt;</c> entries are dropped first) and delegates the preservation-manifest fold.
    /// <para>把改基后的分区附属文件折叠进 system 附属文件（先移除过期的 <c>system/&lt;分区&gt;</c> 条目），
    /// 并委托保留清单折叠。</para>
    /// </summary>
    private static async Task<(bool Folded, int Entries)> FoldPreservationAsync(MyPartitionMergeOptions options,
        string partition, string fullName, string rootRelative, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.MyMetadataDirectory)
            || string.IsNullOrWhiteSpace(options.SystemMetadataDirectory)
            || string.IsNullOrWhiteSpace(options.SystemPartitionName))
        {
            return (false, 0);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var systemDirectory = options.SystemMetadataDirectory!;
        Directory.CreateDirectory(systemDirectory);
        FoldSidecar(Path.Combine(options.MyMetadataDirectory!, fullName + "_file_contexts"),
            Path.Combine(systemDirectory, options.SystemPartitionName + "_file_contexts"),
            options.MountPrefix.TrimEnd('/') + "/" + partition, cancellationToken);
        FoldSidecar(Path.Combine(options.MyMetadataDirectory!, fullName + "_fs_config"),
            Path.Combine(systemDirectory, options.SystemPartitionName + "_fs_config"),
            options.FsConfigRoot.Trim('/') + "/" + partition, cancellationToken);
        return await MyPartitionPreservationFold.FoldAsync(options.MyMetadataDirectory!, systemDirectory,
            fullName, options.SystemPartitionName!, $"{rootRelative}/{partition}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends one rebased sidecar into the system sidecar file, dropping stale entries whose path already sits
    /// under the merged location; a missing source sidecar skips the fold.
    /// <para>把一份改基后的附属文件追加进 system 附属文件，先丢弃路径已位于合并位置之下的过期条目；
    /// 源附属文件缺失时跳过折叠。</para>
    /// </summary>
    private static void FoldSidecar(string sourcePath, string targetPath, string mergedRoot, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) return;
        var merged = mergedRoot.Trim('/') + "/";
        StringBuilder output = new();
        if (File.Exists(targetPath))
        {
            foreach (var line in ReadSidecar(targetPath).Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0 && output.Length == 0) continue;
                var firstToken = trimmed.Split(' ', 2)[0].TrimEnd('/');
                if (firstToken.Equals(mergedRoot.Trim('/'), StringComparison.Ordinal)
                    || firstToken.StartsWith(merged, StringComparison.Ordinal))
                {
                    continue;
                }
                output.Append(trimmed).Append('\n');
            }
        }
        output.Append(ReadSidecar(sourcePath));
        FileExtraction.WriteAtomic(targetPath, stream => stream.Write(Encoding.UTF8.GetBytes(output.ToString())),
            cancellationToken: cancellationToken);
    }

    private static (int Prefixed, int Dropped, IReadOnlyList<string>? Warnings, bool Rebased) RebaseSidecar<T>(string? path,
        string bareName, string fullName, bool skipWhenMissing, Func<string, T> rebase,
        Func<T, (string Text, int Prefixed, int Dropped, IReadOnlyList<string>? Warnings)> project,
        CancellationToken cancellationToken)
    {
        if (path is null)
        {
            if (skipWhenMissing) return (0, 0, null, false);
            throw new InvalidDataException($"Required metadata sidecar '{bareName}' is missing for '{fullName}'.");
        }
        var text = ReadSidecar(path);
        var (rebuilt, prefixed, dropped, warnings) = project(rebase(text));
        FileExtraction.WriteAtomic(path, output =>
            output.Write(Encoding.UTF8.GetBytes(rebuilt)), cancellationToken: cancellationToken);
        return (prefixed, dropped, warnings, true);
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