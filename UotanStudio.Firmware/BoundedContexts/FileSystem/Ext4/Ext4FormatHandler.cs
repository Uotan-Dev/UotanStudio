using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

/// <summary>
/// Format handler that exposes raw EXT4 images to the generic unpack/pack pipeline, bridging
/// <see cref="Ext4Parser"/> and <see cref="Ext4Builder"/>. Parse and build failures are reported as
/// failed <see cref="FirmwareOpResult"/> values; only cancellation propagates as an exception.
/// <para>将 ext4 原始镜像接入通用解包/打包流水线的格式处理器，桥接 <see cref="Ext4Parser"/> 与
/// <see cref="Ext4Builder"/>。解析与构建失败以失败的 <see cref="FirmwareOpResult"/> 返回；
/// 仅取消会以异常传播。</para>
/// </summary>
public sealed class Ext4FormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);

    /// <summary>The stable format key used by the packing pipeline.
    /// <para>打包流水线使用的稳定格式键。</para></summary>
    /// <value>Always <c>"EXT4"</c>; it also identifies preserved repack metadata.
    /// <para>恒为 <c>"EXT4"</c>；同时用于标识保留的重打包元数据。</para></value>
    public string FormatName => "EXT4";

    /// <summary>Candidate lowercase file-name extensions for this format.
    /// <para>本格式的候选小写文件名扩展。</para></summary>
    /// <value><c>"img"</c> and <c>"ext4"</c>; extension matching never replaces the content probing in <see cref="CanHandle"/>.
    /// <para><c>"img"</c> 与 <c>"ext4"</c>；扩展名匹配不能替代 <see cref="CanHandle"/> 的内容探测。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "ext4"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path probed for an EXT4 superblock magic. <para>探测是否存在 EXT4 超级块魔数的路径。</para></param>
    /// <returns><see langword="true"/> only when the file exists and carries magic <c>0xEF53</c> at offset 1080; blank paths, short files and any read error yield <see langword="false"/>.
    /// <para>仅当文件存在且偏移 1080 处携带魔数 <c>0xEF53</c> 时返回 <see langword="true"/>；空白路径、过短文件与任何读取错误均返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);


            if (fs.Length < 1082)
            {
                return false;
            }

            fs.Position = 1080;
            Span<byte> magic = stackalloc byte[2];
            if (fs.Read(magic) != 2)
            {
                return false;
            }

            return (magic[0] | (magic[1] << 8)) == 0xEF53;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">Image file opened and parsed for its superblock. <para>为读取超级块而打开并解析的镜像文件。</para></param>
    /// <param name="cancellationToken">Checked before and after parsing; cancellation propagates as <see cref="OperationCanceledException"/>. <para>在解析前后分别检查；取消以 <see cref="OperationCanceledException"/> 传播。</para></param>
    /// <returns>A completed task whose result is <c>Ok</c> with a superblock-derived key/value metadata set, or <c>Fail</c> carrying the parse error message. <para>已完成的任务：结果为 <c>Ok</c>（携带由超级块导出的键值元数据）或 <c>Fail</c>（含解析错误信息）。</para></returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var parser = new Ext4Parser(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            var sb = parser.Superblock;
            var metadata = new Dictionary<string, string>
            {
                ["PartitionSize"] = parser.PartitionSize.ToString(),
                ["BlockSize"] = parser.BlockSize.ToString(),
                ["BlockCount"] = parser.BlockCount.ToString(),
                ["InodeCount"] = parser.InodeCount.ToString(),
                ["Uuid"] = MetadataDisplay.Uuid(sb.Uuid),
                ["VolumeName"] = sb.VolumeName,
                ["FreeBlocksCount"] = MetadataDisplay.Number(sb.FreeBlocksCount),
                ["FreeInodesCount"] = MetadataDisplay.Number(sb.FreeInodesCount),
                ["FirstDataBlock"] = MetadataDisplay.Number(sb.FirstDataBlock),
                ["BlocksPerGroup"] = MetadataDisplay.Number(sb.BlocksPerGroup),
                ["InodesPerGroup"] = MetadataDisplay.Number(sb.InodesPerGroup),
                ["RevLevel"] = MetadataDisplay.Number(sb.RevLevel),
                ["InodeSize"] = MetadataDisplay.Number(sb.InodeSize),
                ["EffectiveDescSize"] = MetadataDisplay.Number(sb.EffectiveDescSize),
                ["DefHashVersion"] = MetadataDisplay.Number(sb.DefHashVersion),
                ["JournalInum"] = MetadataDisplay.Number(sb.JournalInum),
                ["LastOrphan"] = MetadataDisplay.Number(sb.LastOrphan),
                ["Checksum"] = $"0x{sb.Checksum:X8}",
                ["FeatureCompat"] = MetadataDisplay.Flags(sb.FeatureCompat, (1, "dir_prealloc"), (2, "imagic_inodes"), (4, "has_journal"), (8, "ext_attr"), (16, "resize_inode"), (32, "dir_index"), (64, "lazy_bg"), (128, "exclude_inode"), (256, "exclude_bitmap"), (512, "sparse_super2"), (1024, "fast_commit"), (2048, "stable_inodes"), (4096, "orphan_file")),
                ["FeatureIncompat"] = MetadataDisplay.Flags(sb.FeatureIncompat, (1, "compression"), (2, "filetype"), (4, "needs_recovery"), (8, "journal_dev"), (16, "meta_bg"), (64, "extents"), (128, "64bit"), (256, "mmp"), (512, "flex_bg"), (1024, "ea_inode"), (4096, "dirdata"), (8192, "csum_seed"), (16384, "largedir"), (32768, "inline_data"), (65536, "encrypt"), (131072, "casefold")),
                ["FeatureRoCompat"] = MetadataDisplay.Flags(sb.FeatureRoCompat, (1, "sparse_super"), (2, "large_file"), (4, "btree_dir"), (8, "huge_file"), (16, "gdt_csum"), (32, "dir_nlink"), (64, "extra_isize"), (128, "has_snapshot"), (256, "quota"), (512, "bigalloc"), (1024, "metadata_csum"), (2048, "replica"), (4096, "readonly"), (8192, "project"), (16384, "shared_blocks"), (32768, "verity"), (65536, "orphan_present")),
                ["State"] = MetadataDisplay.Flags(sb.State, (1, "clean"), (2, "errors"), (4, "orphan_recovery")),
            };
            var summary = $"EXT4 image: {parser.BlockCount} blocks x {parser.BlockSize}B = {parser.PartitionSize} bytes, {parser.InodeCount} inodes.";
            return Task.FromResult(FirmwareOpResult.Ok(summary, metadata: metadata));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"Failed to read EXT4 metadata from '{filePath}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">EXT4 image to extract. <para>待提取的 ext4 镜像。</para></param>
    /// <param name="outputDirectory">Host directory receiving the tree; a blank value fails the result. <para>接收目录树的主机目录；为空白时结果失败。</para></param>
    /// <param name="options">Extraction options; <c>Overwrite</c> defaults to <c>true</c> and byte progress routes to the extraction stage. <see langword="null"/> uses defaults. <para>解包选项；<c>Overwrite</c> 默认 <c>true</c>，字节进度归入提取阶段。<see langword="null"/> 使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels parsing and writes; cancellation propagates. <para>取消解析与写入；取消会向上抛出。</para></param>
    /// <returns>A completed task: <c>Ok</c> listing every extracted entry path, or <c>Fail</c> with the error message. The source is frozen through the preservation layer before extraction. <para>已完成任务：提取前先经保留层冻结源；结果为 <c>Ok</c>（列出全部提取条目路径）或 <c>Fail</c>（含错误信息）。</para></returns>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, frozen => UnpackCoreAsync(frozen, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output directory is empty"));
            }

            using var parser = new Ext4Parser(filePath);
            parser.ExtractToDirectory(outputDirectory, overwrite: options?.Overwrite ?? true, byteProgress: FileSystemByteProgress.ForStage(options?.FileSystemProgress, FileSystemWorkStage.Extracting), cancellationToken: cancellationToken);
            var entries = parser.ListFiles();
            return Task.FromResult(FirmwareOpResult.Ok(
                $"Extracted {entries.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. entries.Select(e => e.Path)]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"Failed to unpack EXT4 image '{filePath}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">Source directory to pack; a preserved extraction is restored instead of rebuilt. <para>待打包的源目录；若为已保留的解包结果则直接恢复而非重建。</para></param>
    /// <param name="outputPath">Target image path; a blank value fails the result. <para>目标镜像路径；为空白时结果失败。</para></param>
    /// <param name="cancellationToken">Cancellation propagates as an exception; other failures return <c>Fail</c>. <para>取消以异常传播；其他失败返回 <c>Fail</c>。</para></param>
    /// <returns>A completed task: <c>Ok</c> listing the written image path, or <c>Fail</c> with the reason; explicit overrides on a preserved extraction are refused as <c>Fail</c>. <para>已完成任务：<c>Ok</c> 列出写出的镜像路径，或 <c>Fail</c> 给出原因；对已保留解包结果使用显式覆盖会按 <c>Fail</c> 拒绝。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress: null, cancellationToken);

    /// <summary>
    /// Packs the input into firmware with byte-level progress.
    /// <para>带字节级进度异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">Source directory to pack; a preserved extraction is restored instead of rebuilt. <para>待打包的源目录；若为已保留的解包结果则直接恢复而非重建。</para></param>
    /// <param name="outputPath">Target image path; a blank value fails the result. <para>目标镜像路径；为空白时结果失败。</para></param>
    /// <param name="progress">Byte-level build progress sink; <see langword="null"/> disables reporting. <para>字节级构建进度回调；<see langword="null"/> 表示不报告。</para></param>
    /// <param name="cancellationToken">Cancellation propagates as an exception; other failures return <c>Fail</c>. <para>取消以异常传播；其他失败返回 <c>Fail</c>。</para></param>
    /// <returns>A completed task: <c>Ok</c> listing the written image path, or <c>Fail</c> with the reason. <para>已完成任务：<c>Ok</c> 列出写出的镜像路径，或 <c>Fail</c> 给出原因。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress, cancellationToken);

    private Task<FirmwareOpResult> PackCore(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(inputPath)
                && RepackMetadata.Read(inputPath)?.FormatName == FormatName
                && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
            {
                return Task.FromResult(FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes."));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output path is empty"));
            }

            if (!Directory.Exists(inputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'."));
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = FileSystemPackingOptions.OptionsFor(inputPath, FormatName);
            if (RepackMetadata.HasPackingOverrides
                && RepackMetadata.Read(inputPath)?.FormatName == FormatName)
            {
                return Task.FromResult(FirmwareOpResult.Fail(
                    "Explicit EXT4 overrides for a preserved extraction are not supported yet; refusing a fresh rebuild that could discard source metadata."));
            }
            var capacityPolicy = ParseCapacityPolicy(options);
            var targetSize = options.TryGetValue("TargetSizeBytes", out var targetValue) && targetValue.Length > 0
                ? long.Parse(targetValue, System.Globalization.CultureInfo.InvariantCulture)
                : (long?)null;
            if (capacityPolicy == Ext4PackingCapacityPolicy.Explicit && targetSize is null)
            {
                return Task.FromResult(FirmwareOpResult.Fail("EXT4 explicit capacity requires a target size in bytes."));
            }
            var progressSink = progress is null ? null : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
            Ext4Builder.Build(inputPath, outputPath, new Ext4Options
            {
                BlockSize = FileSystemPackingOptions.IntegerOption(options, "BlockSize", 4096),
                FsConfigPath = FileSystemPackingOptions.NullIfEmpty(options, "FsConfigPath"),
                FileContextsPath = FileSystemPackingOptions.NullIfEmpty(options, "FileContextsPath"),
                EnableSparse = IsTrue(options, "EnableSparse"),
                TargetSizeBytes = capacityPolicy == Ext4PackingCapacityPolicy.Explicit ? targetSize : null,
                CapacityMode = capacityPolicy == Ext4PackingCapacityPolicy.Minimize
                    ? Ext4CapacityMode.Minimize
                    : Ext4CapacityMode.Default,
                ReadOnly = IsTrue(options, "ReadOnly"),
                BlockSharingMode = ParseBlockSharingMode(options),
            }, progressSink);
            return Task.FromResult(FirmwareOpResult.Ok(
                $"Packed EXT4 image '{inputPath}' -> '{outputPath}' ({new FileInfo(outputPath).Length} bytes).",
                [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"Failed to pack EXT4 image '{inputPath}': {ex.Message}"));
        }
    }

    /// <summary>Parses a boolean packing option, treating a missing value as <c>false</c>.
    /// <para>解析布尔打包选项，缺失值视为 <c>false</c>。</para></summary>
    private static bool IsTrue(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && value is "1" or "true" or "True";

    private static Ext4PackingCapacityPolicy ParseCapacityPolicy(IReadOnlyDictionary<string, string> options)
    {
        var value = options.GetValueOrDefault("CapacityPolicy", "Inherit");
        return Enum.TryParse<Ext4PackingCapacityPolicy>(value, ignoreCase: true, out var policy)
            && Enum.IsDefined(policy)
                ? policy
                : throw new InvalidDataException($"Unsupported EXT4 capacity policy '{value}'.");
    }

    private static Ext4BlockSharingMode ParseBlockSharingMode(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("BlockSharingMode", out var value))
        {
            return Ext4BlockSharingMode.Independent;
        }

        return Enum.TryParse<Ext4BlockSharingMode>(value, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : throw new InvalidDataException($"Unsupported EXT4 block sharing mode '{value}'.");
    }

    private enum Ext4PackingCapacityPolicy
    {
        Inherit,
        Explicit,
        Minimize,
    }
}