using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// Format handler for EROFS read-only filesystem images: detection, metadata reading, unpacking and
/// directory-to-image packing, repack metadata capture for the shared rebuilding pipeline, and the
/// <see cref="IStreamUnpackableFormatHandler"/> capability that lets container formats cascade-unpack an
/// EROFS payload from an already-resolved logical stream.
/// <para>EROFS 只读文件系统镜像的格式处理器：识别、读取元数据、解包，以及把目录打包为镜像，为共享重建流水线
/// 采集重打包元数据，并发布 <see cref="IStreamUnpackableFormatHandler"/> 能力，供容器格式对已解析逻辑流
/// 级联解包 EROFS 载荷。</para>
/// </summary>
public sealed class ErofsFormatHandler : IFormatHandler, IRepackMetadataProvider, IStreamUnpackableFormatHandler
{
    /// <summary>Reads the number of external devices without accessing their data or parsing inodes.
    /// <para>仅读取外部设备数量，不访问其数据也不解析 inode。</para></summary>
    /// <param name="imagePath">
    /// Image file opened read-only; only the superblock window at its fixed offset is parsed.
    /// <para>以只读方式打开的镜像文件；仅解析其固定偏移处的超级块窗口。</para>
    /// </param>
    /// <returns>The extra-device count from the superblock; <c>0</c> for a single-device image.
    /// <para>超级块中的外部设备数；单设备镜像为 <c>0</c>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The superblock is invalid, or it declares extra devices
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
            throw new FileSystemCorruptedException("EROFS declares extra devices without a device table.");
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

        return HeaderProbe.ProbeMagic(filePath,
            ErofsSuperblock.SuperblockOffset + 4,
            ErofsSuperblock.SuperblockOffset,
            ErofsSuperblock.Magic);
    }

    /// <summary>
    /// Determines whether a logical stream carries an EROFS image by probing the superblock magic.
    /// <para>通过探测超级块魔数判断逻辑流是否承载 EROFS 镜像。</para>
    /// </summary>
    /// <param name="logical">Seekable logical stream positioned by the container caller; read errors are
    /// swallowed and reported as a non-match. <para>由容器调用方定位好的可寻址逻辑流；读取错误被吞掉并按不匹配处理。</para></param>
    /// <returns><see langword="true"/> when the magic matches; <see langword="false"/> for unseekable streams,
    /// short streams and any read error.
    /// <para>魔数匹配时为 <see langword="true"/>；不可寻址、过短的流与任何读取错误时为 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="logical"/> is <see langword="null"/>.
    /// <para><paramref name="logical"/> 为 <see langword="null"/>。</para></exception>
    public bool CanHandleLogicalStream(Stream logical)
    {
        ArgumentNullException.ThrowIfNull(logical);
        if (!logical.CanSeek)
        {
            return false;
        }

        var origin = logical.Position;
        try
        {
            if (logical.Length < ErofsSuperblock.SuperblockOffset + 4)
            {
                return false;
            }

            logical.Position = ErofsSuperblock.SuperblockOffset;
            Span<byte> magic = stackalloc byte[4];
            logical.ReadExactly(magic);
            return ErofsSuperblock.IsMagicBytes(magic);
        }
        catch
        {
            return false;
        }
        finally
        {
            logical.Position = origin;
        }
    }

    /// <summary>
    /// Unpacks an EROFS image carried by an already-resolved logical stream into the output directory.
    /// <para>把已解析逻辑流承载的 EROFS 镜像解包到输出目录。</para>
    /// </summary>
    /// <param name="logical">Seekable logical stream positioned at the start of the primary device image;
    /// ownership stays with the caller, which disposes it after the returned task completes.
    /// <para>定位到主设备镜像起始处的可寻址逻辑流；所有权保留在调用方，由调用方在返回任务完成后释放。</para></param>
    /// <param name="outputDirectory">Host directory receiving the extracted tree. <para>接收解出目录树的主机目录。</para></param>
    /// <param name="byteProgress">Optional byte-level progress routed to the extraction stage. <see langword="null"/> reports nothing.
    /// <para>可选的字节级进度，归入提取阶段；<see langword="null"/> 表示不报告。</para></param>
    /// <param name="overwrite">Whether existing output files may be replaced. <para>是否允许替换已存在的输出文件。</para></param>
    /// <param name="cancellationToken">Cancels parsing and writes; cancellation propagates. <para>取消解析与写入；取消会向上抛出。</para></param>
    /// <returns>An <c>Ok</c> result listing every extracted file path with the inner-format metadata; its
    /// summary is composed by the container caller, which alone knows the container file path.
    /// <para><c>Ok</c> 结果列出全部解出的文件路径并附内部格式元数据；其摘要由容器调用方组织，因为只有它知道容器文件路径。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackLogicalStreamAsync(Stream logical, string outputDirectory,
        IProgress<FileSystemByteProgress>? byteProgress, bool overwrite, CancellationToken cancellationToken)
    {
        using var parser = new ErofsParser(logical);
        parser.ExtractToDirectory(outputDirectory, FileSystemByteProgress.ForStage(byteProgress, FileSystemWorkStage.Extracting), cancellationToken, overwrite);
        var files = parser.EnumerateFiles();
        return Task.FromResult(FirmwareOpResult.Ok(
            $"Extracted {files.Count} entries to '{outputDirectory}'.",
            [.. files.Select(file => file.Path)],
            metadata: new Dictionary<string, string>
            {
                ["InnerFormat"] = "EROFS",
                ["FilesystemBlockSize"] = parser.BlockSize.ToString(),
                ["VolumeName"] = parser.VolumeName
            }));
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
                using var parser = FileSystemPreservation
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
                    ["FeatureCompat"] = ErofsSuperblock.DescribeCompat(sb.FeatureCompat),
                    ["FeatureIncompat"] = ErofsSuperblock.DescribeIncompat(sb.FeatureIncompat),
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
        => FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, frozen => UnpackCoreAsync(frozen, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                using var parser = FileSystemPreservation
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
    /// <param name="cancellationToken">Token checked at task start, throughout preserved-image restoration and during the image build.
    /// <para>在任务开始、保留镜像重建与镜像构建全程检查的令牌。</para></param>
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
    /// <param name="cancellationToken">Token checked at task start, throughout preserved-image restoration and during the image build.
    /// <para>在任务开始、保留镜像重建与镜像构建全程检查的令牌。</para></param>
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
                if (Directory.Exists(inputPath)
                    && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName,
                    cancellationToken, report => rebuildReport = report, progress: progress))
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
                var progressSink = progress is null ? null : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
                var erofsOptions = new ErofsOptions
                {
                    BlockSize = FileSystemPackingOptions.IntegerOption(options, "BlockSize", 4096),
                    VolumeName = options.TryGetValue("VolumeName", out var volume) ? volume : "erofs",
                    Algorithm = algorithm,
                    Lz4HighCompression = lz4HighCompression,
                    CompressionLevel = compressionLevel,
                    CancellationToken = cancellationToken,
                };
                if (FileSystemPackingOptions.TryReadPreservedTables(inputPath) is { } preservedTables)
                {
                    erofsOptions.FsConfig = preservedTables.FsConfig;
                    erofsOptions.FileContexts = preservedTables.FileContexts;
                }
                else
                {
                    erofsOptions.FsConfigPath = FileSystemPackingOptions.NullIfEmpty(options, "FsConfigPath");
                    erofsOptions.FileContextsPath = FileSystemPackingOptions.NullIfEmpty(options, "FileContextsPath");
                }

                FileSystemPackingOptions.ApplyErofsFeatureKeys(options, erofsOptions, inheritedWords: null);

                builder.Build(inputPath, outputPath, erofsOptions, progressSink);

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