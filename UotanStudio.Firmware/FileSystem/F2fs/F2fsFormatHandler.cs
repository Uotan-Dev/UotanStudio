using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.F2fs;

/// <summary>
/// Format-handler entry point that maps F2FS images onto the generic read-metadata, unpack and
/// pack pipeline, delegating parsing to <c>F2fsParser</c> and building to <c>F2fsBuilder</c>.
/// <para>格式处理器入口：把 F2FS 镜像接入通用的读取元数据、解包与打包流水线，解析委托给
/// <c>F2fsParser</c>，构建委托给 <c>F2fsBuilder</c>。</para>
/// </summary>
public sealed class F2fsFormatHandler : IFormatHandler, IRepackMetadataProvider, IStreamUnpackableFormatHandler
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);

    /// <summary>
    /// Display name of the handled format.
    /// <para>所处理格式的显示名称。</para>
    /// </summary>
    /// <value>The constant string <c>"F2FS"</c>.
    /// <para>固定字符串 <c>"F2FS"</c>。</para></value>
    public string FormatName => "F2FS";

    /// <summary>
    /// Conventional file extensions of F2FS image files, without leading dots.
    /// <para>F2FS 镜像文件的惯用扩展名，不含开头的点。</para>
    /// </summary>
    /// <value><c>"img"</c> and <c>"f2fs"</c>; presence of an extension alone does not decide
    /// <see cref="CanHandle"/>, which also accepts a superblock magic match.
    /// <para>为 <c>"img"</c> 与 <c>"f2fs"</c>；仅凭扩展名不足以判定 <see cref="CanHandle"/>，魔数匹配同样可通过。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "f2fs"];

    /// <summary>
    /// Determines whether this handler can process the given file by reading the 4-byte magic at offset 1024 (little-endian 0xF2F52010), or by a .f2fs extension.
    /// <para>判断该处理器能否处理指定文件：读取偏移 1024 处的 4 字节魔数（小端 0xF2F52010）， 或扩展名为 .f2fs。</para>
    /// </summary>
    /// <param name="filePath">Path to probe; <see langword="null"/>, blank or missing files are
    /// reported as unhandled without throwing, and read errors are swallowed the same way.
    /// <para>待探测的路径；为 <see langword="null"/>、空白或文件不存在时直接判定为不可处理而不抛出，读取错误同样被吞掉。</para></param>
    /// <returns><see langword="true"/> when the extension is <c>.f2fs</c> (case-insensitive) or the
    /// superblock magic matches; <see langword="false"/> otherwise.
    /// <para>扩展名为 <c>.f2fs</c>（不区分大小写）或超级块魔数匹配时返回 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (Path.GetExtension(filePath).Equals(".f2fs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HeaderProbe.ProbeMagic(filePath,
            F2fsSuperblock.SuperblockOffset + 4,
            F2fsSuperblock.SuperblockOffset,
            F2fsSuperblock.Magic);
    }

    /// <summary>Reads format metadata without unpacking.<para>读取格式元数据而不解包。</para></summary>
    /// <param name="filePath">F2FS image file to open and parse.<para>要打开并解析的 F2FS 镜像文件。</para></param>
    /// <param name="cancellationToken">Cancels before parsing starts and at checkpoints inside it.
    /// <para>在解析开始前及解析过程中的检查点处取消。</para></param>
    /// <returns>A task whose result is <c>Ok</c> with a string dictionary describing geometry, segment addresses, feature/checkpoint flags and checkpoint counters, or <c>Fail</c> carrying the parse error message when the image is not a valid F2FS filesystem.
    /// <para>任务结果：有效镜像时为 <c>Ok</c>，附字符串字典描述几何信息、段地址、特性/检查点标志与 检查点计数；镜像非法时为 <c>Fail</c> 并携带解析错误消息。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during the read.
    /// <para>读取开始前或进行中令牌被取消。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parser = new F2fsParser(filePath);
                cancellationToken.ThrowIfCancellationRequested();
                var sb = parser.Superblock;
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "F2FS",
                    ["BlockSize"] = parser.BlockSize.ToString(),
                    ["SegmentSize"] = parser.SegmentSize.ToString(),
                    ["BlockCount"] = parser.BlockCount.ToString(),
                    ["ImageSize"] = parser.ImageSize.ToString(),
                    ["InodeCount"] = parser.InodeCount.ToString(),
                    ["VolumeName"] = parser.VolumeName,
                    ["Uuid"] = MetadataDisplay.Uuid(sb.Uuid),
                    ["MajorVersion"] = MetadataDisplay.Number(sb.MajorVersion),
                    ["MinorVersion"] = MetadataDisplay.Number(sb.MinorVersion),
                    ["SectorSize"] = MetadataDisplay.Number(sb.SectorSize),
                    ["SectorsPerBlock"] = MetadataDisplay.Number(sb.SectorsPerBlock),
                    ["BlocksPerSeg"] = MetadataDisplay.Number(sb.BlocksPerSeg),
                    ["SegsPerSec"] = MetadataDisplay.Number(sb.SegsPerSec),
                    ["SecsPerZone"] = MetadataDisplay.Number(sb.SecsPerZone),
                    ["ChecksumOffset"] = MetadataDisplay.Number(sb.ChecksumOffset),
                    ["SectionCount"] = MetadataDisplay.Number(sb.SectionCount),
                    ["SegmentCount"] = MetadataDisplay.Number(sb.SegmentCount),
                    ["SegmentCountCkpt"] = MetadataDisplay.Number(sb.SegmentCountCkpt),
                    ["SegmentCountSit"] = MetadataDisplay.Number(sb.SegmentCountSit),
                    ["SegmentCountNat"] = MetadataDisplay.Number(sb.SegmentCountNat),
                    ["SegmentCountSsa"] = MetadataDisplay.Number(sb.SegmentCountSsa),
                    ["SegmentCountMain"] = MetadataDisplay.Number(sb.SegmentCountMain),
                    ["Segment0Blkaddr"] = MetadataDisplay.Number(sb.Segment0Blkaddr),
                    ["CpBlkaddr"] = MetadataDisplay.Number(sb.CpBlkaddr),
                    ["SitBlkaddr"] = MetadataDisplay.Number(sb.SitBlkaddr),
                    ["NatBlkaddr"] = MetadataDisplay.Number(sb.NatBlkaddr),
                    ["SsaBlkaddr"] = MetadataDisplay.Number(sb.SsaBlkaddr),
                    ["MainBlkaddr"] = MetadataDisplay.Number(sb.MainBlkaddr),
                    ["RootIno"] = MetadataDisplay.Number(sb.RootIno),
                    ["NodeIno"] = MetadataDisplay.Number(sb.NodeIno),
                    ["MetaIno"] = MetadataDisplay.Number(sb.MetaIno),
                    ["CpPayload"] = MetadataDisplay.Number(sb.CpPayload),
                    ["Feature"] = MetadataDisplay.Flags(sb.Feature, (1, "encryption"), (2, "blkzoned"), (4, "atomic_write"), (8, "extra_attr"), (16, "project_quota"), (32, "inode_checksum"), (64, "flexible_inline_xattr"), (128, "quota_ino"), (256, "inode_crtime"), (512, "lost_found"), (1024, "verity"), (2048, "sb_checksum"), (4096, "casefold"), (8192, "compression"), (16384, "readonly"), (32768, "device_alias")),
                    ["Version"] = System.Text.Encoding.UTF8.GetStringTrimNul(sb.Version),
                    ["CheckpointVer"] = MetadataDisplay.Number(parser.Checkpoint.CheckpointVer),
                    ["UserBlockCount"] = MetadataDisplay.Number(parser.Checkpoint.UserBlockCount),
                    ["ValidBlockCount"] = MetadataDisplay.Number(parser.Checkpoint.ValidBlockCount),
                    ["RsvdSegmentCount"] = MetadataDisplay.Number(parser.Checkpoint.RsvdSegmentCount),
                    ["OverprovSegmentCount"] = MetadataDisplay.Number(parser.Checkpoint.OverprovSegmentCount),
                    ["FreeSegmentCount"] = MetadataDisplay.Number(parser.Checkpoint.FreeSegmentCount),
                    ["ValidNodeCount"] = MetadataDisplay.Number(parser.Checkpoint.ValidNodeCount),
                    ["NextFreeNid"] = MetadataDisplay.Number(parser.Checkpoint.NextFreeNid),
                    ["ElapsedTime"] = MetadataDisplay.Number(parser.Checkpoint.ElapsedTime),
                    ["CheckpointFlags"] = MetadataDisplay.Flags(parser.Checkpoint.CkptFlags, (1, "unmount"), (2, "orphan_present"), (4, "compact_summary"), (8, "error"), (16, "fsck_needed"), (32, "fastboot"), (64, "crc_recovery"), (128, "nat_bits"), (256, "trimmed"), (512, "no_crc_recovery"), (1024, "large_nat_bitmap"), (2048, "checkpoint_disabled"), (4096, "quota_needs_fsck"), (8192, "checkpoint_disabled_quick"), (16384, "resizefs")),
                };
                return FirmwareOpResult.Ok("F2FS filesystem metadata.", metadata: metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read F2FS metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>Unpacks the f2fs image to the output directory.<para>将 f2fs 镜像解包到输出目录。</para></summary>
    /// <param name="filePath">Source F2FS image; a frozen snapshot of it is extracted so concurrent writes to the original file cannot corrupt the output.
    /// <para>源 F2FS 镜像；解包基于其冻结快照，原文件上的并发写入不会破坏输出。</para></param>
    /// <param name="outputDirectory">Destination directory; created when missing.
    /// <para>目标目录；不存在时自动创建。</para></param>
    /// <param name="options">Unpack options such as overwrite and progress reporting; <see langword="null"/> applies the defaults (overwrite enabled, no progress).
    /// <para>解包选项（覆盖策略与进度上报等）；为 <see langword="null"/> 时使用默认值（允许覆盖、无进度）。</para></param>
    /// <param name="cancellationToken">Cancels the extraction, including writes inside a large file.
    /// <para>取消提取过程，包括大文件内部的写入。</para></param>
    /// <returns>A task whose result is <c>Ok</c> listing the image-relative paths of all extracted entries, or <c>Fail</c> carrying the extraction error message.
    /// <para>任务结果：成功时为 <c>Ok</c> 并列出全部解包条目的镜像内相对路径；失败时为 <c>Fail</c> 并携带错误消息。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled during unpacking.
    /// <para>解包过程中令牌被取消。</para></exception>
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
                using var parser = new F2fsParser(filePath);
                parser.ExtractToDirectory(outputDirectory, overwrite: options?.Overwrite ?? true, byteProgress: FileSystemByteProgress.ForStage(options?.FileSystemProgress, FileSystemWorkStage.Extracting), cancellationToken: cancellationToken);
                var files = parser.ListFiles();
                return FirmwareOpResult.Ok(
                    $"Extracted {files.Count} entries from '{filePath}' to '{outputDirectory}'.",
                    [.. files.Select(file => file.Path)]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack F2FS image '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>Packs a directory into an f2fs image.<para>将目录打包为 f2fs 镜像。</para></summary>
    /// <param name="inputPath">Directory to pack; when it carries repack metadata for this format, preserved source attributes are restored directly, otherwise a fresh build is produced from the packing options. A native Android profile refuses to rebuild an extracted tree that has preserved source metadata.
    /// <para>待打包目录；若目录携带本格式的 repack 元数据，则直接还原保留的源属性，否则按打包选项 全新构建。Android 原生配置拒绝重建带有源元数据保留的已解包目录树。</para></param>
    /// <param name="outputPath">Destination image path; the parent directory is created and the file is replaced. With the sparse-container option the raw image is built to a temporary file and converted atomically, and the temporary file is deleted either way.
    /// <para>目标镜像路径；自动创建父目录并覆盖目标文件。启用稀疏容器选项时，先写入临时原始镜像再 原子转换，临时文件无论成败都会被删除。</para></param>
    /// <param name="cancellationToken">Cancels the build before or during output.
    /// <para>在输出开始前或输出过程中取消构建。</para></param>
    /// <returns>A task whose result is <c>Ok</c> on success, carrying rebuild equivalence metadata for a preserved restore, or <c>Fail</c> carrying the build error message (missing input directory, invalid options, native-tool failure and similar); a failed pack leaves the previous output untouched where the atomic paths are taken.
    /// <para>任务结果：成功为 <c>Ok</c> 并附带受保留重建的等价元数据；失败为 <c>Fail</c> 并携带构建错误消息（输入目录缺失、 选项非法、原生工具失败等）；走原子路径时，打包失败不会动既有输出。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled during packing.
    /// <para>打包过程中令牌被取消。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, null, cancellationToken);

    Task<FirmwareOpResult> IWritableFormatHandler.PackAsync(string inputPath, string outputPath,
        IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
        => PackCore(inputPath, outputPath, progress, cancellationToken);

    private Task<FirmwareOpResult> PackCore(string inputPath, string outputPath,
        IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var requestedOptions = FileSystemPackingOptions.OptionsFor(inputPath, FormatName);
                if (requestedOptions.TryGetValue("AndroidNativeProfile", out var nativeProfile)
                    && IsTrue(nativeProfile) && RepackMetadata.Read(inputPath) is not null)
                {
                    return FirmwareOpResult.Fail(
                        "Native Android F2FS cannot rebuild an extracted image with preserved source metadata; use a new directory with explicit fs_config and file_contexts.");
                }
                IReadOnlyDictionary<string, string>? rebuildReport = null;
                if (Directory.Exists(inputPath)
                    && RepackMetadata.Read(inputPath)?.FormatName == FormatName
                    && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken,
                        report => rebuildReport = report, progress: progress))
                    return FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes.",
                        metadata: rebuildReport);
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var options = FileSystemPackingOptions.OptionsFor(inputPath, FormatName);
                var sparseOutput = options.TryGetValue("OutputSparseContainer", out var outerSparse) && IsTrue(outerSparse);
                var fullOutputPath = Path.GetFullPath(outputPath);
                var rawOutput = sparseOutput
                    ? Path.Combine(Path.GetDirectoryName(fullOutputPath)!, "." + Path.GetFileName(fullOutputPath) + "." + Guid.NewGuid().ToString("N") + ".raw.tmp")
                    : fullOutputPath;
                Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                try
                {
                    F2fsBuilder.BuildWithProgress(inputPath, rawOutput, new F2fsOptions
                    {
                        BlockSize = FileSystemPackingOptions.IntegerOption(options, "BlockSize", 4096),
                        SegmentSize = FileSystemPackingOptions.IntegerOption(options, "SegmentSize", 2 * 1024 * 1024),
                        VolumeName = options.TryGetValue("VolumeName", out var volume) ? volume : "f2fs",
                        EnableSparse = !options.TryGetValue("EnableSparse", out var sparse) || IsTrue(sparse),
                        EnableCompression = options.TryGetValue("EnableCompression", out var compression) && IsTrue(compression),
                        CompressionAlgorithm = ParseCompressionAlgorithm(options),
                        CompressionLevel = FileSystemPackingOptions.IntegerOption(options, "CompressionLevel", 0),
                        AndroidNativeProfile = options.TryGetValue("AndroidNativeProfile", out var androidProfile) && IsTrue(androidProfile),
                        AndroidImageSizeBytes = options.TryGetValue("AndroidImageSizeBytes", out var imageSize)
                            && long.TryParse(imageSize, out var parsedSize) ? parsedSize : null,
                        NativeWslDistribution = FileSystemPackingOptions.NullIfEmpty(options, "NativeWslDistribution"),
                        FsConfigPath = FileSystemPackingOptions.NullIfEmpty(options, "FsConfigPath"),
                        FileContextsPath = FileSystemPackingOptions.NullIfEmpty(options, "FileContextsPath"),
                        CancellationToken = cancellationToken,
                    }, progress is null
                        ? null
                        : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage));
                    if (sparseOutput)
                    {
                        SparseContainerAccess.Resolve().ConvertRawToSparseAtomic(rawOutput, fullOutputPath, cancellationToken);
                    }
                }
                finally
                {
                    if (sparseOutput)
                    {
                        File.Delete(rawOutput);
                    }
                }

                return FirmwareOpResult.Ok($"Packed '{inputPath}' into '{outputPath}' (F2FS).");
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

    private static CompressionAlgorithm ParseCompressionAlgorithm(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("CompressionAlgorithm", out var value))
        {
            return CompressionAlgorithm.Lz4;
        }

        return Enum.TryParse<CompressionAlgorithm>(value, ignoreCase: true, out var algorithm)
            && Enum.IsDefined(algorithm)
            ? algorithm
            : throw new FileSystemUnsupportedFeatureException($"Unsupported F2FS compression algorithm '{value}'.");
    }

    private static bool IsTrue(string value)
        => value is "1" || bool.TryParse(value, out var enabled) && enabled;

    /// <summary>
    /// Recognises an F2FS image carried by an already-resolved logical stream by probing the superblock magic at
    /// the format's own offset; the stream position is restored and unreadable input yields <see langword="false"/>.
    /// <para>通过在 F2FS 自身偏移处探测超级块魔数来识别已解析逻辑流承载的 F2FS 镜像；恢复流位置，不可读输入返回
    /// <see langword="false"/>。</para>
    /// </summary>
    /// <param name="logical">Seekable logical stream owned by the container caller.
    /// <para>由容器调用方持有的可寻址逻辑流。</para></param>
    /// <returns><see langword="true"/> when this handler owns the stream content.
    /// <para>该逻辑流由本处理器负责时返回 <see langword="true"/>。</para></returns>
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
            if (logical.Length < F2fsSuperblock.SuperblockOffset + 4)
            {
                return false;
            }

            logical.Position = F2fsSuperblock.SuperblockOffset;
            Span<byte> magic = stackalloc byte[4];
            return logical.Read(magic) == 4 && F2fsSuperblock.IsMagicBytes(magic);
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

    /// <summary>Unpacks an F2FS image carried by an already-resolved logical stream into the output directory.
    /// <para>把已解析逻辑流承载的 F2FS 镜像解包到输出目录。</para></summary>
    /// <param name="logical">Seekable logical stream positioned at the start of the image; ownership stays with the caller.
    /// <para>定位到镜像起始处的可寻址逻辑流；所有权保留在调用方。</para></param>
    /// <param name="outputDirectory">Host directory receiving the extracted tree.
    /// <para>接收解出目录树的主机目录。</para></param>
    /// <param name="byteProgress">Optional byte-level progress routed to the extraction stage.
    /// <para>可选的字节级进度，归入提取阶段。</para></param>
    /// <param name="overwrite">Whether existing output files may be replaced.
    /// <para>是否允许替换已存在的输出文件。</para></param>
    /// <param name="cancellationToken">Cancels parsing and writes; cancellation propagates.
    /// <para>取消解析与写入；取消会向上抛出。</para></param>
    /// <returns>An <c>Ok</c> result listing every extracted entry path with the inner-format metadata; its summary is composed by the container caller, which alone knows the container file path.
    /// <para><c>Ok</c> 结果列出全部解出的条目路径并附内部格式元数据；摘要由容器调用方组织，因为只有它知道容器 文件路径。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackLogicalStreamAsync(Stream logical, string outputDirectory,
        IProgress<FileSystemByteProgress>? byteProgress, bool overwrite, CancellationToken cancellationToken)
    {
        using var parser = new F2fsParser(logical);
        parser.ExtractToDirectory(outputDirectory, FileSystemByteProgress.ForStage(byteProgress, FileSystemWorkStage.Extracting), cancellationToken, overwrite);
        var files = parser.ListFiles();
        return Task.FromResult(FirmwareOpResult.Ok(
            $"Extracted {files.Count} entries to '{outputDirectory}'.",
            [.. files.Select(file => file.Path)],
            metadata: new Dictionary<string, string>
            {
                ["InnerFormat"] = "F2FS",
                ["FilesystemBlockSize"] = parser.BlockSize.ToString(),
                ["VolumeName"] = parser.VolumeName
            }));
    }
}