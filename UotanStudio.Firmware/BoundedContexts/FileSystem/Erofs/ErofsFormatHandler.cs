using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed class ErofsFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    /// <summary>Reads the number of external devices without accessing their data or parsing inodes.</summary>
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

    public string FormatName => "EROFS";

    public IReadOnlyList<string> FileExtensions { get; } = ["img", "erofs"];

    /// <summary>
    /// Determines whether this handler can process the given file by reading the 4-byte magic at offset 1024 (little-endian 0xE0F5E1E2), or by a .erofs extension.
    /// <para>判断该处理器能否处理指定文件：读取偏移 1024 处的 4 字节魔数（小端 0xE0F5E1E2）， 或扩展名为 .erofs。</para>
    /// </summary>
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
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress: null, cancellationToken);

    /// <summary>
    /// Packs a directory into an erofs image with byte-level progress.
    /// <para>带字节级进度将目录打包为 erofs 镜像。</para>
    /// </summary>
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
                // The panel selects the codec through this override key; a preserved source image (no
                // overrides) is rebuilt earlier by TryRestore with its own algorithms, so this builder
                // path only runs for explicit user selection or a fresh tree.
                // 面板通过该覆盖键选择编解码器；保留的源镜像（无覆盖）在更早的 TryRestore 分支按其自身算法
                // 重建，因此本构建路径只在用户显式选择或全新目录时运行。
                var (algorithm, lz4HighCompression) = ErofsAlgorithmSelectors.Parse(
                    options.TryGetValue("ErofsAlgorithm", out var selector) ? selector : null);
                var compressionLevel = FileSystemPackingOptions.IntegerOption(options, "ErofsCompressionLevel",
                    9);
                // A modern codec selection implies the modern layout: legacy (pre-5.13) EROFS cannot
                // encode LZMA/DEFLATE/ZSTD, so honouring the picker requires dropping the user's legacy
                // flag rather than silently rebuilding as LZ4. HC shares the same requirement from a
                // density angle: legacy caps every pcluster at one physical block, so the HC search
                // saves bytes that always round up to the same block count. Forcing modern lets HC
                // actually shrink the image, matching upstream -zlz4hc semantics.
                // 选择现代编解码器即隐含现代布局：legacy（pre-5.13）erofs 无法编码 LZMA/DEFLATE/ZSTD，
                // 要遵循选择器就必须放弃用户的 legacy 标志，而不是静默改回 LZ4 重建。HC 从密度角度
                // 也有同样要求：legacy 把每个 pcluster 限死在一个物理块，HC 搜索省下的字节一定会被
                // 舍入到同样的块数。强制现代布局才能让 HC 实际缩小镜像，与上游 -zlz4hc 的语义一致。
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
                    // Repack metadata carries no legacy flag of its own; the app-level setting routes
                    // through the shared default so rebuilt images honor the user's kernel choice.
                    // 重打包元数据不记录 legacy 标志；应用层设置经由共享默认值传递，使重建镜像
                    // 遵循用户对内核的选择。
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