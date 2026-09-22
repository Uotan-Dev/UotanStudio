using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed class Ext4FormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);

    public string FormatName => "EXT4";

    public IReadOnlyList<string> FileExtensions { get; } = ["img", "ext4"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
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
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress: null, cancellationToken);

    /// <summary>
    /// Packs the input into firmware with byte-level progress.
    /// <para>带字节级进度异步将输入打包为固件。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, progress, cancellationToken);

    private Task<FirmwareOpResult> PackCore(string inputPath, string outputPath, IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            if (!RepackMetadata.HasPackingOverrides
                && Directory.Exists(inputPath)
                && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
                return Task.FromResult(FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes."));
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
            var progressSink = progress is null ? null : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
            Ext4Builder.Build(inputPath, outputPath, new Ext4Options
            {
                BlockSize = FileSystemPackingOptions.IntegerOption(options, "BlockSize", 4096),
                FsConfigPath = FileSystemPackingOptions.NullIfEmpty(options, "FsConfigPath"),
                FileContextsPath = FileSystemPackingOptions.NullIfEmpty(options, "FileContextsPath"),
                EnableSparse = IsTrue(options, "EnableSparse"),
                Ext4Compress = IsTrue(options, "Ext4Compress"),
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
}