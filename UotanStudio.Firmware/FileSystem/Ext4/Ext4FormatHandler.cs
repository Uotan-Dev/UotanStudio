using System.Buffers.Binary;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Ext4;

/// <summary>
/// Format handler that exposes raw EXT4 images to the generic unpack/pack pipeline, bridging
/// <see cref="Ext4Parser"/> and <see cref="Ext4Builder"/>, and additionally publishes the
/// <see cref="IStreamUnpackableFormatHandler"/> capability so container formats can cascade-unpack an
/// EXT4 payload from an already-resolved logical stream. File-path parse and build failures are reported
/// as failed <see cref="FirmwareOpResult"/> values with only cancellation propagating; the container-facing
/// stream path propagates every error to the container's own failure channel.
/// <para>将 ext4 原始镜像接入通用解包/打包流水线的格式处理器，桥接 <see cref="Ext4Parser"/> 与
/// <see cref="Ext4Builder"/>，并发布 <see cref="IStreamUnpackableFormatHandler"/> 能力，供容器格式对
/// 已解析逻辑流级联解包 ext4 载荷。文件路径操作的解析与构建失败以失败的 <see cref="FirmwareOpResult"/>
/// 返回、仅取消会抛出；面向容器的流路径则把所有错误交由容器自身的失败通道报告。</para>
/// </summary>
public sealed class Ext4FormatHandler : IFormatHandler, IRepackMetadataProvider, IStreamUnpackableFormatHandler
{
    /// <summary>Metadata key carrying the free-block count of a packed image that has too little room left for read-write use.
    /// <para>当打包出的镜像剩余空间不足以支撑读写使用时，记录其空闲块数的元数据键。</para></summary>
    private const string LowWriteHeadroomMetadataKey = "LowWriteHeadroomBlocks";

    /// <summary>
    /// Free-block floor below which a packed image is reported as short of write headroom. A freshly built image
    /// reserves room for directory blocks, extent-tree growth and journal records before the first file lands, and
    /// a default-capacity build routinely finishes with only a couple of dozen free blocks, where Linux already
    /// fails the very first creation with ENOSPC; 256 blocks (1 MiB at the default 4 KiB block size, 64 KiB at the
    /// 1 KiB minimum) is the smallest margin that still covers a directory split plus a few extents.
    /// <para>低于该空闲块数时，打包结果会被标记为写余量不足。新建镜像在第一个文件落地之前还要为目录块、extent
    /// 树增长和日志记录留空间，而默认容量构建常常只剩几十个空闲块，此时 Linux 挂载后第一次创建就会返回
    /// ENOSPC；256 块（默认 4 KiB 块大小时为 1 MiB，1 KiB 最小块大小时为 64 KiB）是能覆盖一次目录分裂与若干
    /// extent 的最小余量。</para>
    /// </summary>
    private const ulong LowWriteHeadroomThresholdBlocks = 256;

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
        return HeaderProbe.ProbeMagic(filePath, 1082, 1080, [0x53, 0xEF]);
    }

    /// <summary>
    /// Determines whether a logical stream carries a raw EXT4 image by probing only the layout-defined
    /// superblock magic: the two little-endian bytes at logical offset 1080 must equal <c>0xEF53</c> within a
    /// stream of at least 1082 bytes. The stream position is restored before returning.
    /// <para>仅按布局定义的超级块魔数判断逻辑流是否承载 ext4 原始镜像：长度至少 1082 字节的流中，逻辑偏移
    /// 1080 处的两个小端字节必须等于 <c>0xEF53</c>。返回前会恢复流位置。</para>
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
            if (logical.Length < 1082)
            {
                return false;
            }

            logical.Position = 1080;
            Span<byte> magic = stackalloc byte[2];
            return logical.Read(magic) == 2 && BinaryPrimitives.ReadUInt16LittleEndian(magic) == 0xEF53;
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
    /// Unpacks a raw EXT4 image carried by an already-resolved logical stream into the output directory,
    /// the container-facing counterpart of <see cref="UnpackAsync"/>. Parse, extraction and cancellation
    /// errors propagate so the container caller reports them through its own failure channel; the result
    /// carries the extracted entry paths plus the <c>InnerFormat</c> and <c>FilesystemBlockSize</c> metadata
    /// the sparse container cascade expects (EXT4 emits no <c>VolumeName</c> key on this path).
    /// <para>把已解析逻辑流承载的 ext4 原始镜像解包到输出目录，是 <see cref="UnpackAsync"/> 的面向容器版本。
    /// 解析、提取与取消错误向上传播，由容器调用方经其自身的失败通道报告；结果携带解出的条目路径，以及稀疏容器
    /// 级联解包所期望的 <c>InnerFormat</c> 与 <c>FilesystemBlockSize</c> 元数据（此路径下 EXT4 不输出
    /// <c>VolumeName</c> 键）。</para>
    /// </summary>
    /// <param name="logical">Seekable logical stream positioned at the start of the image; ownership stays with
    /// the caller, which disposes it after the returned task completes.
    /// <para>定位到镜像起始处的可寻址逻辑流；所有权保留在调用方，由调用方在返回任务完成后释放。</para></param>
    /// <param name="outputDirectory">Host directory receiving the extracted tree. <para>接收解出目录树的主机目录。</para></param>
    /// <param name="byteProgress">Optional byte-level progress routed to the extraction stage. <see langword="null"/> reports nothing.
    /// <para>可选的字节级进度，归入提取阶段；<see langword="null"/> 表示不报告。</para></param>
    /// <param name="overwrite">Whether existing output files may be replaced. <para>是否允许替换已存在的输出文件。</para></param>
    /// <param name="cancellationToken">Cancels parsing and writes; cancellation propagates. <para>取消解析与写入；取消会向上抛出。</para></param>
    /// <returns>An <c>Ok</c> result listing every extracted entry path with the inner-format metadata; its
    /// summary is composed by the container caller, which alone knows the container file path.
    /// <para><c>Ok</c> 结果列出全部解出的条目路径并附内部格式元数据；其摘要由容器调用方组织，因为只有它知道容器文件路径。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackLogicalStreamAsync(Stream logical, string outputDirectory,
        IProgress<FileSystemByteProgress>? byteProgress, bool overwrite, CancellationToken cancellationToken)
    {
        using var parser = new Ext4Parser(logical);
        parser.ExtractToDirectory(outputDirectory, FileSystemByteProgress.ForStage(byteProgress, FileSystemWorkStage.Extracting), cancellationToken, overwrite);
        var entries = parser.ListFiles();
        return Task.FromResult(FirmwareOpResult.Ok(
            $"Extracted {entries.Count} entries to '{outputDirectory}'.",
            [.. entries.Select(entry => entry.Path)],
            metadata: new Dictionary<string, string>
            {
                ["InnerFormat"] = "EXT4",
                ["FilesystemBlockSize"] = parser.BlockSize.ToString()
            }));
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
            if (sb.HasSparseSuper2)
            {
                metadata["BackupGroups"] = $"{sb.BackupGroup1}, {sb.BackupGroup2}";
            }
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
        => UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
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
    /// <returns>A completed task: <c>Ok</c> listing the written image path and carrying rebuild equivalence metadata
    /// for a preserved restore plus the <c>LowWriteHeadroomBlocks</c> key when the produced image leaves too few
    /// free blocks for read-write use, or <c>Fail</c> with the reason; explicit overrides on a preserved extraction
    /// are refused as <c>Fail</c>.
    /// <para>已完成任务：<c>Ok</c> 列出写出的镜像路径、附带受保留重建的等价元数据，并在产出镜像的空闲块不足以
    /// 读写使用时附加 <c>LowWriteHeadroomBlocks</c> 键，或 <c>Fail</c> 给出原因；对已保留解包结果使用显式覆盖会按
    /// <c>Fail</c> 拒绝。</para></returns>
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
    /// <returns>A completed task: <c>Ok</c> listing the written image path and carrying rebuild equivalence metadata
    /// for a preserved restore plus the <c>LowWriteHeadroomBlocks</c> key when the produced image leaves too few
    /// free blocks for read-write use, or <c>Fail</c> with the reason.
    /// <para>已完成任务：<c>Ok</c> 列出写出的镜像路径、附带受保留重建的等价元数据，并在产出镜像的空闲块不足以
    /// 读写使用时附加 <c>LowWriteHeadroomBlocks</c> 键，或 <c>Fail</c> 给出原因。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress, cancellationToken);

    private Task<FirmwareOpResult> PackCore(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyDictionary<string, string>? rebuildReport = null;
            if (Directory.Exists(inputPath)
                && RepackMetadata.Read(inputPath)?.FormatName == FormatName
                && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken,
                    report => rebuildReport = report, progress: progress))
            {
                return Task.FromResult(WithWriteHeadroomWarning(
                    FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes.",
                        metadata: rebuildReport), outputPath));
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
                CancellationToken = cancellationToken,
            }, progressSink);
            return Task.FromResult(WithWriteHeadroomWarning(
                FirmwareOpResult.Ok(
                    $"Packed EXT4 image '{inputPath}' -> '{outputPath}' ({new FileInfo(outputPath).Length} bytes).",
                    [outputPath]), outputPath));
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

    /// <summary>
    /// Annotates a successful pack result with the <c>LowWriteHeadroomBlocks</c> key when the image that was just
    /// written holds fewer free blocks than <see cref="LowWriteHeadroomThresholdBlocks"/>, because such an image
    /// mounts read-write yet cannot survive even a first directory creation. The count is read back from the bytes
    /// on disk rather than threaded out of the builder, so preserved restores and fresh builds are described by the
    /// same measurement, and the produced image keeps its size because the warning never resizes or rewrites it.
    /// An image that cannot be parsed stays unannotated: reporting headroom is advisory and must not turn a
    /// completed pack into a failure.
    /// <para>当刚刚写出的镜像空闲块数低于 <see cref="LowWriteHeadroomThresholdBlocks"/> 时，为成功的打包结果附加
    /// <c>LowWriteHeadroomBlocks</c> 键：这类镜像虽能读写挂载，却连第一次创建目录都会失败。空闲块数从磁盘上
    /// 真正写出的字节回读，而不是由构建器透传，因此保留重建与全新构建都用同一个度量描述，且警告不会改变
    /// 镜像大小或重写镜像。无法解析的镜像保持不加标注：报告写余量只是提示，绝不能把已完成的打包变成失败。</para>
    /// </summary>
    /// <param name="result">Result of the pack operation; a failure is returned untouched. <para>打包操作的结果；失败结果原样返回。</para></param>
    /// <param name="imagePath">Image the pack just published, read back for its superblock free-block count. <para>打包刚发布的镜像，为其超级块空闲块数而重新读取。</para></param>
    /// <returns>The same result when no annotation applies, otherwise a result with the same summary, entries and components plus the headroom key.
    /// <para>无需标注时返回同一结果；否则返回摘要、明细行与组件保持不变并附加写余量键的结果。</para></returns>
    private static FirmwareOpResult WithWriteHeadroomWarning(FirmwareOpResult result, string imagePath)
    {
        if (!result.Success)
        {
            return result;
        }

        ulong freeBlocks;
        try
        {
            using var parser = new Ext4Parser(imagePath);
            freeBlocks = parser.Superblock.FreeBlocksCount;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FileSystemCorruptedException)
        {
            return result;
        }

        if (freeBlocks >= LowWriteHeadroomThresholdBlocks)
        {
            return result;
        }

        var metadata = new Dictionary<string, string>(result.Metadata, StringComparer.Ordinal);
        metadata[LowWriteHeadroomMetadataKey] = MetadataDisplay.Number(freeBlocks);
        return new FirmwareOpResult
        {
            Success = true,
            Summary = result.Summary,
            Entries = result.Entries,
            Metadata = metadata,
            Components = result.Components,
        };
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
                : throw new FileSystemUnsupportedFeatureException($"Unsupported EXT4 capacity policy '{value}'.");
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
                : throw new FileSystemUnsupportedFeatureException($"Unsupported EXT4 block sharing mode '{value}'.");
    }

    private enum Ext4PackingCapacityPolicy
    {
        Inherit,
        Explicit,
        Minimize,
    }
}