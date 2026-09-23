using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

public sealed class F2fsFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);

    public string FormatName => "F2FS";

    public IReadOnlyList<string> FileExtensions { get; } = ["img", "f2fs"];

    /// <summary>
    /// Determines whether this handler can process the given file by reading the 4-byte magic at offset 1024 (little-endian 0xF2F52010), or by a .f2fs extension.
    /// <para>判断该处理器能否处理指定文件：读取偏移 1024 处的 4 字节魔数（小端 0xF2F52010）， 或扩展名为 .f2fs。</para>
    /// </summary>
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

        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < F2fsSuperblock.SuperblockOffset + 4)
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            fs.Position = F2fsSuperblock.SuperblockOffset;
            fs.ReadExactly(magic);
            return F2fsSuperblock.IsMagicBytes(magic);
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
                    ["Version"] = System.Text.Encoding.UTF8.GetString(sb.Version).TrimEnd('\0'),
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

    /// <summary>
    /// Unpacks the f2fs image to the output directory.
    /// <para>将 f2fs 镜像解包到输出目录。</para>
    /// </summary>
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

    /// <summary>
    /// Packs a directory into an f2fs image.
    /// <para>将目录打包为 f2fs 镜像。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
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
                if (Directory.Exists(inputPath)
                    && RepackMetadata.Read(inputPath)?.FormatName == FormatName
                    && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
                    return FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes.");
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
                    F2fsBuilder.Build(inputPath, rawOutput, new F2fsOptions
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
                    });
                    if (sparseOutput)
                    {
                        SparseImageConverter.ConvertRawToSparseAtomic(rawOutput, fullOutputPath, cancellationToken: cancellationToken);
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
            : throw new InvalidDataException($"Unsupported F2FS compression algorithm '{value}'.");
    }

    private static bool IsTrue(string value)
        => value is "1" || bool.TryParse(value, out var enabled) && enabled;
}