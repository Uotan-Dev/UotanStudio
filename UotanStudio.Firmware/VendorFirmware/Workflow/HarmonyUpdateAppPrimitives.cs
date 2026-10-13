using System.Text;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Firmware.VendorFirmware.Workflow;

/// <summary>
/// Outcome of extracting requested logical partitions from a super image: which names were present and which
/// were absent from the LP metadata.
/// <para>从 super 镜像提取请求的逻辑分区的结果：哪些名称存在、哪些在 LP 元数据中缺失。</para>
/// </summary>
/// <param name="ExtractedPartitions">Partition names whose images were committed to the output directory.
/// <para>镜像已提交到输出目录的分区名称。</para></param>
/// <param name="MissingPartitions">Requested names absent from the image, tolerated rather than failed.
/// <para>请求但镜像中不存在的名称，按容忍处理而非报错。</para></param>
/// <param name="OutputDirectory">Directory that received the <c>&lt;name&gt;.img</c> files.
/// <para>接收 <c>&lt;名称&gt;.img</c> 文件的目录。</para></param>
public sealed record HarmonySuperExtractionResult(IReadOnlyList<string> ExtractedPartitions,
    IReadOnlyList<string> MissingPartitions, string OutputDirectory);

/// <summary>
/// Outcome of regenerating mergeable filesystem metadata from a configuration directory.
/// <para>从配置目录重新生成可合并文件系统元数据的结果。</para>
/// </summary>
/// <param name="FileContextPrefixed">file_contexts rules that received the mount prefix. <para>被加上挂载前缀的 file_contexts 规则数。</para></param>
/// <param name="FileContextDropped">file_contexts defaults and duplicates removed. <para>被移除的 file_contexts 默认与重复条目数。</para></param>
/// <param name="MetacharacterWarnings">Regex expressions kept verbatim by the rebase. <para>改基时保持原样的含正则表达式。</para></param>
/// <param name="FsConfigPrefixed">fs_config entries that received the root prefix. <para>被加上根前缀的 fs_config 条目数。</para></param>
/// <param name="FsConfigDropped">fs_config defaults and duplicates removed. <para>被移除的 fs_config 默认与重复条目数。</para></param>
public sealed record HarmonyMetadataRegenerationResult(int FileContextPrefixed, int FileContextDropped,
    IReadOnlyList<string> MetacharacterWarnings, int FsConfigPrefixed, int FsConfigDropped);

/// <summary>
/// Host-side building blocks for HarmonyOS-style conversion at the file level: named logical partitions are
/// read from a super image through the LP surface, a provided init/libfs_mgr file set is staged into an
/// extracted system directory tree, and mergeable filesystem metadata is regenerated through the rebase
/// helpers. No device, QEMU, or end-to-end ROM claim is made by any primitive.
/// <para>HarmonyOS 风格转换在文件层面的主机侧构件：通过 LP 读取面从 super 镜像读取命名逻辑分区，
/// 把给定的 init/libfs_mgr 文件集注入已解包的 system 目录树，并经改基辅助函数重新生成可合并的文件系统元数据。
/// 任何原语都不宣称支持设备、QEMU 或端到端 ROM 转换。</para>
/// </summary>
internal sealed class HarmonyUpdateAppPrimitives
{
    private const int MaximumRequestedPartitions = 64;
    private const int MaximumStagedFiles = 4096;
    private const int MaximumStagedDepth = 16;
    private const long MaximumSidecarBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Extracts the named logical partitions from a super image, tolerating names that the image does not carry
    /// and reporting them separately.
    /// <para>从 super 镜像提取命名逻辑分区，容忍镜像中不存在的名称并单独报告。</para>
    /// </summary>
    /// <param name="superImagePath">
    /// Raw or sparse super image; it is opened read-only. <para>原始或稀疏 super 镜像；以只读方式打开。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory receiving one <c>&lt;name&gt;.img</c> per present partition; existing outputs are never replaced.
    /// <para>为每个存在的分区接收一个 <c>&lt;名称&gt;.img</c> 的目录；绝不替换已有输出。</para>
    /// </param>
    /// <param name="partitionNames">
    /// Requested partition names (for example <c>system</c>, <c>cust</c>, <c>hw_product</c>), matched case-insensitively.
    /// <para>请求的分区名（如 <c>system</c>、<c>cust</c>、<c>hw_product</c>），按不区分大小写匹配。</para>
    /// </param>
    /// <param name="progress">
    /// Optional stage progress forwarded to the LP extractor. <para>转发给 LP 提取器的可选阶段进度。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before reading and throughout extraction; cancellation propagates.
    /// <para>在读取前与整个提取期间检查；取消会向上抛出。</para>
    /// </param>
    /// <returns>
    /// The extracted and missing name lists alongside the output directory. <para>提取与缺失名称列表以及输出目录。</para>
    /// </returns>
    /// <exception cref="ArgumentException">The name list is empty or exceeds 64 entries. <para>名称列表为空或超过 64 项。</para></exception>
    /// <exception cref="InvalidDataException">The super image cannot be parsed or extraction fails.
    /// <para>super 镜像无法解析，或提取失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<HarmonySuperExtractionResult> ExtractSuperPartitionsAsync(string superImagePath,
        string outputDirectory, IReadOnlyCollection<string> partitionNames, IFirmwareProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(superImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(partitionNames);
        if (partitionNames.Count is 0 or > MaximumRequestedPartitions)
            throw new ArgumentException($"Select between 1 and {MaximumRequestedPartitions} partitions.", nameof(partitionNames));
        var requested = partitionNames
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0)
            throw new ArgumentException("At least one non-blank partition name is required.", nameof(partitionNames));
        cancellationToken.ThrowIfCancellationRequested();

        var present = await Task.Run(() => MetadataReader.ReadFromImageFile(superImagePath).PartitionNames,
            cancellationToken).ConfigureAwait(false);
        var presentSet = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        var wanted = requested.Where(name => presentSet.Contains(name)).ToArray();
        var missing = requested.Where(name => !presentSet.Contains(name)).ToArray();
        progress?.Report(wanted.Length == 0 ? 100 : 5,
            wanted.Length == 0 ? "No requested partition is present in the super image" : "Extracting requested partitions");
        if (wanted.Length > 0)
        {
            var result = await new LpFormatHandler().ExtractSelectedAsync(superImagePath, outputDirectory, wanted,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidDataException(result.Summary);
        }

        progress?.Report(100, $"Extracted {wanted.Length} partitions, {missing.Length} absent");
        return new HarmonySuperExtractionResult(wanted, missing, outputDirectory);
    }

    /// <summary>
    /// Stages every file of an init file set into an extracted system directory tree.
    /// <para>把 init 文件集中的全部文件按相对布局注入已解包 system 目录树。</para>
    /// </summary>
    /// <param name="targetSystemDirectory">
    /// Extracted system directory tree receiving the staged files; it must already exist.
    /// <para>接收注入文件的已解包 system 目录树；必须已存在。</para>
    /// </param>
    /// <param name="initFileSetDirectory">
    /// Host directory holding the replacement init and libfs_mgr files. <para>存放待替换 init 与 libfs_mgr 文件的主机目录。</para>
    /// </param>
    /// <param name="relativeTargetDirectory">
    /// Safe relative path inside the system tree (default <c>init</c>). <para>system 树内的安全相对路径（默认 <c>init</c>）。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before every file copy; cancellation leaves already published files intact.
    /// <para>在每个文件拷贝前检查；取消时已发布文件保持不变。</para>
    /// </param>
    /// <returns>
    /// The number of staged files. <para>注入的文件数量。</para>
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">Either directory is missing. <para>任一目录不存在。</para></exception>
    /// <exception cref="InvalidDataException">The relative target escapes the tree, the file set is too large, or a path is unsafe.
    /// <para>相对目标逃逸出目录树、文件集过大，或路径不安全。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<int> StageInitFileSetAsync(string targetSystemDirectory, string initFileSetDirectory,
        string relativeTargetDirectory = "init", CancellationToken cancellationToken = default)
        => Task.Run(() => StageCore(targetSystemDirectory, initFileSetDirectory, relativeTargetDirectory, cancellationToken),
            cancellationToken);

    private static int StageCore(string targetSystemDirectory, string initFileSetDirectory,
        string relativeTargetDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystemDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(initFileSetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeTargetDirectory);
        if (!Directory.Exists(targetSystemDirectory))
            throw new DirectoryNotFoundException($"The extracted system directory tree is missing: {targetSystemDirectory}");
        if (!Directory.Exists(initFileSetDirectory))
            throw new DirectoryNotFoundException($"The init file set directory is missing: {initFileSetDirectory}");
        var stageRoot = FileExtraction.ResolvePath(targetSystemDirectory, relativeTargetDirectory);
        var setRoot = Path.GetFullPath(initFileSetDirectory);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((setRoot, 0));
        var staged = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++staged > MaximumStagedFiles)
                    throw new InvalidDataException($"The init file set exceeds {MaximumStagedFiles} files.");
                var relative = Path.GetRelativePath(setRoot, file);
                var destination = FileExtraction.ResolvePath(stageRoot, relative);
                using (var source = File.OpenRead(file))
                    FileExtraction.WriteAtomic(destination, output =>
                        source.CopyTo(output, 128 * 1024), cancellationToken: cancellationToken);
            }

            if (depth >= MaximumStagedDepth)
                throw new InvalidDataException($"The init file set exceeds {MaximumStagedDepth} directory levels.");
            foreach (var child in Directory.EnumerateDirectories(directory))
                pending.Push((child, depth + 1));
        }

        return staged;
    }

    /// <summary>
    /// Regenerates mergeable filesystem metadata in a configuration directory: <c>file_contexts</c> and
    /// <c>fs_config</c> are rebased through the patch helpers and replaced atomically.
    /// <para>重新生成配置目录中的可合并文件系统元数据：<c>file_contexts</c> 与 <c>fs_config</c> 经补丁辅助函数改基并原子替换。</para>
    /// </summary>
    /// <param name="configDirectory">
    /// Directory holding both sidecar files. <para>同时持有两个附属文件的目录。</para>
    /// </param>
    /// <param name="mountPrefix">
    /// Absolute file_contexts mount prefix, e.g. <c>/system</c>. <para>file_contexts 的绝对挂载前缀，例如 <c>/system</c>。</para>
    /// </param>
    /// <param name="partition">
    /// Partition name appended to both prefixes. <para>追加到两个前缀之后的分区名。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked during each line scan; cancellation propagates. <para>在每次逐行扫描期间检查；取消会向上抛出。</para>
    /// </param>
    /// <returns>
    /// The rebase accounting and regex warnings. <para>改基统计与正则警告列表。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">A sidecar is missing, oversized or malformed.
    /// <para>附属文件缺失、超限或格式错误。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<HarmonyMetadataRegenerationResult> RegenerateMergeMetadataAsync(string configDirectory,
        string mountPrefix, string partition, CancellationToken cancellationToken = default)
        => Task.Run(() => RegenerateCore(configDirectory, mountPrefix, partition, cancellationToken), cancellationToken);

    private static HarmonyMetadataRegenerationResult RegenerateCore(string configDirectory, string mountPrefix,
        string partition, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        var contextsPath = Path.Combine(configDirectory, "file_contexts");
        var fsConfigPath = Path.Combine(configDirectory, "fs_config");
        var contexts = FileContextPatch.Rebase(ReadSidecar(contextsPath), mountPrefix, partition, cancellationToken);
        FileExtraction.WriteAtomic(contextsPath, output =>
            output.Write(Encoding.UTF8.GetBytes(contexts.Text)), cancellationToken: cancellationToken);
        var fsRoot = mountPrefix.Trim('/') + "/";
        if (fsRoot == "/")
            throw new InvalidDataException("The mount prefix must name at least one directory.");
        var fsConfig = FsConfigPatch.Rebase(ReadSidecar(fsConfigPath), fsRoot, partition, cancellationToken);
        FileExtraction.WriteAtomic(fsConfigPath, output =>
            output.Write(Encoding.UTF8.GetBytes(fsConfig.Text)), cancellationToken: cancellationToken);
        return new HarmonyMetadataRegenerationResult(contexts.PrefixedRules, contexts.DroppedRules,
            contexts.MetacharacterWarnings, fsConfig.PrefixedEntries, fsConfig.DroppedEntries);
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