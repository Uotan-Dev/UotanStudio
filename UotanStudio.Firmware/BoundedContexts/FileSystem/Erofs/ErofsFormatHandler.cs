using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

/// <summary>
/// Format handler for EROFS read-only filesystem images: detection, metadata reading, unpacking and
/// directory-to-image packing, plus repack metadata capture for the shared rebuilding pipeline.
/// <para>EROFS 只读文件系统镜像的格式处理器：识别、读取元数据、解包，以及把目录打包为镜像，并为共享重建
/// 流水线采集重打包元数据。</para>
/// </summary>
public sealed class ErofsFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    /// <summary>Reads the number of external devices without accessing their data or parsing inodes.
    /// <para>仅读取外部设备数量，不访问其数据也不解析 inode。</para></summary>
    /// <param name="imagePath">
    /// Image file opened read-only; only the superblock window at its fixed offset is parsed.
    /// <para>以只读方式打开的镜像文件；仅解析其固定偏移处的超级块窗口。</para>
    /// </param>
    /// <returns>The extra-device count from the superblock; <c>0</c> for a single-device image.
    /// <para>超级块中的外部设备数；单设备镜像为 <c>0</c>。</para></returns>
    /// <exception cref="InvalidDataException">The superblock is invalid, or it declares extra devices
    /// without a device table. <para>超级块无效，或声明了外部设备却缺少设备表。</para></exception>
    /// <exception cref="EndOfStreamException">The file is shorter than the superblock window.
    /// <para>文件短于超级块窗口。</para></exception>
    public static ushort ReadExtraDeviceCount(string imagePath)
    {
        using var input = File.OpenRead(imagePath);
        input.Seek(ErofsSuperblock.SuperblockOffset, SeekOrigin.Begin);
        Span<byte> superblock = stackalloc byte[ErofsSuperblock.SuperblockSize];
        input.ReadExactly(superblock);
        var parsed = ErofsSuperblock.FromBytes(superblock);
        if (parsed.ExtraDevices > 0 && !parsed.HasDeviceTable)
        {
            throw new InvalidDataException("EROFS declares extra devices without a device table.");
        }
        return parsed.ExtraDevices;
    }

    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);

    /// <summary>Name of the format this handler serves.
    /// <para>本处理器所服务格式的名称。</para></summary>
    /// <value>The constant <c>"EROFS"</c>. <para>常量 <c>"EROFS"</c>。</para></value>
    public string FormatName => "EROFS";

    /// <summary>File extensions this handler registers.
    /// <para>本处理器注册的文件扩展名。</para></summary>
    /// <value>Read-only list <c>img</c> and <c>erofs</c>; extension alone never overrides magic detection.
    /// <para>只读列表 <c>img</c> 与 <c>erofs</c>；仅凭扩展名不会取代魔数检测。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "erofs"];

    /// <summary>
    /// Determines whether this handler can process the given file by reading the 4-byte magic at offset 1024 (little-endian 0xE0F5E1E2), or by a .erofs extension.
    /// <para>判断该处理器能否处理指定文件：读取偏移 1024 处的 4 字节魔数（小端 0xE0F5E1E2）， 或扩展名为 .erofs。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; <see langword="null"/>, blank or nonexistent paths
    /// short-circuit to <see langword="false"/>.
    /// <para>候选文件路径；为 <see langword="null"/>、空白或不存在的路径直接返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the extension matches or the superblock magic is present;
    /// <see langword="false"/> on any I/O failure, which is swallowed.
    /// <para>扩展名匹配或存在超级块魔数时返回 <see langword="true"/>；任何 I/O 失败都返回
    /// <see langword="false"/> 且被吞掉。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (Path.GetExtension(filePath).Equals(".erofs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < ErofsSuperblock.SuperblockOffset + 4)
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            fs.Position = ErofsSuperblock.SuperblockOffset;
            fs.ReadExactly(magic);
            return ErofsSuperblock.IsMagicBytes(magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据而不解包。</para>
    /// </summary>
    /// <param name="filePath">EROFS image file, opened read-only on a worker thread and closed before the
    /// task completes. <para>EROFS 镜像文件；在工作线程上以只读方式打开，任务完成前关闭。</para></param>
    /// <param name="cancellationToken">Token checked before and after parsing.
    /// <para>在解析前后各检查一次的令牌。</para></param>
    /// <returns>An <c>Ok</c> result whose metadata dictionary carries superblock geometry, UUID, feature
    /// flag names and effective compression algorithms; a <c>Fail</c> result with the error message when
    /// the image cannot be read or parsed. <see langword="null"/> entries never occur in the dictionary.
    /// <para><c>Ok</c> 结果的元数据字典包含超级块几何、UUID、特性标志名与实际生效的压缩算法；镜像无法读取或
    /// 解析时返回带错误消息的 <c>Fail</c> 结果。字典中不会出现 <see langword="null"/> 项。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                using var parser = UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation
                    .OpenErofsParser(input, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var sb = parser.Superblock;
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "EROFS",
                    ["BlockSize"] = parser.BlockSize.ToString(),
                    ["Blocks"] = parser.BlockCount.ToString(),
                    ["ImageSize"] = parser.ImageSize.ToString(),
                    ["InodeCount"] = parser.InodeCount.ToString(),
                    ["RootNid"] = parser.RootNid.ToString(),
                    ["VolumeName"] = parser.VolumeName,
                    ["Uuid"] = MetadataDisplay.Uuid(sb.Uuid),
                    ["Checksum"] = $"0x{sb.Checksum:X8}",
                    ["SbExtSlots"] = MetadataDisplay.Number(sb.SbExtSlots),
                    ["BuildTime"] = MetadataDisplay.Number(sb.BuildTime),
                    ["BuildTimeNsec"] = MetadataDisplay.Number(sb.BuildTimeNsec),
                    ["MetaBlkAddr"] = MetadataDisplay.Number(sb.MetaBlkAddr),
                    ["XattrBlkAddr"] = MetadataDisplay.Number(sb.XattrBlkAddr),
                    ["ExtraDevices"] = MetadataDisplay.Number(sb.ExtraDevices),
                    ["DevtSlotoff"] = MetadataDisplay.Number(sb.DevtSlotoff),
                    ["DirBlkBits"] = MetadataDisplay.Number(sb.DirBlkBits),
                    ["XattrPrefixCount"] = MetadataDisplay.Number(sb.XattrPrefixCount),
                    ["XattrPrefixStart"] = MetadataDisplay.Number(sb.XattrPrefixStart),
                    ["PackedNid"] = MetadataDisplay.Number(sb.PackedNid),
                    ["MetaBoxNid"] = MetadataDisplay.Number(sb.MetaBoxNid),
                    ["FeatureCompat"] = MetadataDisplay.Flags(sb.FeatureCompat, (1, "sb_checksum"), (2, "mtime"), (4, "xattr_filter"), (8, "shared_ea_in_metabox"), (16, "plain_xattr_pfx"), (32, "ishare_xattrs")),
                    ["FeatureIncompat"] = MetadataDisplay.Flags(sb.FeatureIncompat, (1, "lz4_0padding"), (2, "compression_cfgs/big_pcluster"), (4, "chunked_file"), (8, "device_table/compress_head2"), (16, "ztailpacking"), (32, "fragments/dedupe"), (64, "xattr_prefixes"), (128, "48bit"), (256, "metabox")),
                    ["AvailableCompressionAlgorithms"] = MetadataDisplay.Flags(sb.EffectiveComprAlgs, (1, "LZ4"), (2, "LZMA"), (4, "DEFLATE"), (8, "Zstandard")),
                };
                return FirmwareOpResult.Ok("EROFS filesystem metadata.", metadata: metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read EROFS metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks the erofs image to the output directory.
    /// <para>将 erofs 镜像解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Source image; extraction runs against a private snapshot copy of it, so a
    /// concurrent change to this file fails the operation instead of mixing content versions.
    /// <para>源镜像；解包针对其私有快照副本进行，因此对该文件的并发修改会使操作失败而不是混用两个版本的内容。</para></param>
    /// <param name="outputDirectory">Destination tree; files are extracted with their image paths.
    /// <para>目标目录树；文件按镜像内路径解出。</para></param>
    /// <param name="options">Unpack options; <see langword="null"/> overwrites existing files and reports
    /// no byte progress. External device images are validated against the snapshot stage.
    /// <para>解包选项；为 <see langword="null"/> 时覆盖已存在文件且不报告字节进度。外部设备镜像会在快照阶段
    /// 一并校验。</para></param>
    /// <param name="cancellationToken">Token checked through snapshot preparation and extraction.
    /// <para>在快照准备与解包全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result listing the extracted file paths, or a <c>Fail</c> result when the
    /// image, snapshot or extraction fails; summary paths refer to the original image file.
    /// <para>返回列出已解出文件路径的 <c>Ok</c> 结果；镜像、快照或解包失败时返回 <c>Fail</c> 结果，
    /// 摘要中的路径指向原始镜像文件。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, frozen => UnpackCoreAsync(frozen, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                using var parser = UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation
                    .OpenErofsParser(input, cancellationToken);
                parser.ExtractToDirectory(outputDirectory, overwrite: options?.Overwrite ?? true, byteProgress: FileSystemByteProgress.ForStage(options?.FileSystemProgress, FileSystemWorkStage.Extracting), cancellationToken: cancellationToken);
                var files = parser.EnumerateFiles();
                return FirmwareOpResult.Ok(
                    $"Extracted {files.Count} files from '{filePath}' to '{outputDirectory}'.",
                    [.. files.Select(file => file.Path)]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack EROFS image '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs a directory into an erofs image.
    /// <para>将目录打包为 erofs 镜像。</para>
    /// </summary>
    /// <param name="inputPath">Unpacked directory tree; when preserved rebuild parameters exist for it,
    /// the original-image reconstruction path is taken first.
    /// <para>已解包的目录树；若存在为其保留的重建参数，会优先走原镜像重建路径。</para></param>
    /// <param name="outputPath">Destination image file, overwritten by a successful build.
    /// <para>目标镜像文件；构建成功后被覆盖。</para></param>
    /// <param name="cancellationToken">Token checked at task start and throughout preserved-image restoration.
    /// <para>在任务开始与保留镜像重建全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result on success, possibly carrying rebuild-report metadata; a <c>Fail</c>
    /// result when the input is not a directory or the build fails.
    /// <para>成功时返回 <c>Ok</c> 结果（可能带有重建报告元数据）；输入不是目录或构建失败时返回 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress: null, cancellationToken);

    /// <summary>
    /// Packs a directory into an erofs image with byte-level progress.
    /// <para>带字节级进度将目录打包为 erofs 镜像。</para>
    /// </summary>
    /// <param name="inputPath">Unpacked directory tree; when preserved rebuild parameters exist for it,
    /// the original-image reconstruction path is taken first.
    /// <para>已解包的目录树；若存在为其保留的重建参数，会优先走原镜像重建路径。</para></param>
    /// <param name="outputPath">Destination image file, overwritten by a successful build.
    /// <para>目标镜像文件；构建成功后被覆盖。</para></param>
    /// <param name="progress">Reporter receiving byte progress for the building stage; <see langword="null"/>
    /// reports nothing. <para>接收构建阶段字节进度的回调；为 <see langword="null"/> 时不报告。</para></param>
    /// <param name="cancellationToken">Token checked at task start and throughout preserved-image restoration.
    /// <para>在任务开始与保留镜像重建全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result on success, possibly carrying rebuild-report metadata; a <c>Fail</c>
    /// result when the input is not a directory or the build fails.
    /// <para>成功时返回 <c>Ok</c> 结果（可能带有重建报告元数据）；输入不是目录或构建失败时返回 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress, cancellationToken);

    private Task<FirmwareOpResult> PackCore(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyDictionary<string, string>? rebuildReport = null;
                if (!RepackMetadata.HasPackingOverrides
                    && Directory.Exists(inputPath)
                    && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName,
                    cancellationToken, report => rebuildReport = report))
                {
                    var summary = "Packed the filesystem using preserved source attributes.";
                    if (rebuildReport is not null && rebuildReport.TryGetValue("FitsSourceFilesystemSize", out var fits))
                        summary += $" Compression sizing fit: {fits}.";
                    return FirmwareOpResult.Ok(summary, metadata: rebuildReport);
                }
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var builder = new ErofsBuilder();
                var options = FileSystemPackingOptions.OptionsFor(inputPath, FormatName);
                var (algorithm, lz4HighCompression) = ErofsAlgorithmSelectors.Parse(
                    options.TryGetValue("ErofsAlgorithm", out var selector) ? selector : null);
                var compressionLevel = FileSystemPackingOptions.IntegerOption(options, "ErofsCompressionLevel",
                    9);
                var legacyCompatibility = algorithm == ErofsCompressionAlgorithm.Lz4
                    && !lz4HighCompression
                    && UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder.ErofsBuildDefaults.LegacyCompatibility;
                var progressSink = progress is null ? null : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
                builder.Build(inputPath, outputPath, new ErofsOptions
                {
                    BlockSize = FileSystemPackingOptions.IntegerOption(options, "BlockSize", 4096),
                    VolumeName = options.TryGetValue("VolumeName", out var volume) ? volume : "erofs",
                    FsConfigPath = FileSystemPackingOptions.NullIfEmpty(options, "FsConfigPath"),
                    FileContextsPath = FileSystemPackingOptions.NullIfEmpty(options, "FileContextsPath"),
                    Algorithm = algorithm,
                    Lz4HighCompression = lz4HighCompression,
                    CompressionLevel = compressionLevel,
                    LegacyCompatibility = legacyCompatibility,
                }, progressSink);

                return FirmwareOpResult.Ok($"Packed '{inputPath}' into '{outputPath}' (EROFS).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack '{inputPath}': {ex.Message}");
            }
        }, cancellationToken);
    }
}