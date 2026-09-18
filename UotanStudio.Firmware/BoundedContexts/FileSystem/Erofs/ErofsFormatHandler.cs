using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed class ErofsFormatHandler : IFormatHandler
{
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
                using var parser = new ErofsParser(filePath);
                var files = parser.EnumerateFiles();
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "EROFS",
                    ["BlockSize"] = parser.BlockSize.ToString(),
                    ["Blocks"] = parser.BlockCount.ToString(),
                    ["ImageSize"] = parser.ImageSize.ToString(),
                    ["InodeCount"] = parser.InodeCount.ToString(),
                    ["FileCount"] = files.Count.ToString(),
                    ["RootNid"] = parser.RootNid.ToString(),
                    ["VolumeName"] = parser.VolumeName,
                };
                var components = files.Select(file => FirmwareComponent.Of(file.Path, file.Size)).ToList();
                return FirmwareOpResult.Ok(
                    $"{files.Count} files in EROFS image '{filePath}'.",
                    [.. files.Select(file => file.Path)],
                    metadata,
                    components);
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
        => UotanStudio.Firmware.SharedKernel.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, frozen => UnpackCoreAsync(frozen, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parser = new ErofsParser(filePath);
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
                var options = RepackMetadata.OptionsFor(inputPath, FormatName);
                // The panel selects the LZ4 block encoder through this override key; EROFS itself stores
                // no codec enum for it, so the on-disk algorithm stays "lz4".
                // 面板通过该覆盖键选择 LZ4 块编码器；EROFS 本身不存对应编解码枚举，磁盘算法仍为 "lz4"。
                var lz4hc = string.Equals(options.TryGetValue("ErofsAlgorithm", out var algorithm) ? algorithm : "lz4",
                    "lz4hc", StringComparison.OrdinalIgnoreCase);
                var compressionLevel = RepackMetadata.IntegerOption(options, "ErofsCompressionLevel",
                    9);
                var progressSink = progress is null ? null : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
                builder.Build(inputPath, outputPath, new ErofsOptions
                {
                    BlockSize = RepackMetadata.IntegerOption(options, "BlockSize", 4096),
                    VolumeName = options.TryGetValue("VolumeName", out var volume) ? volume : "erofs",
                    FsConfigPath = RepackMetadata.NullIfEmpty(options, "FsConfigPath"),
                    FileContextsPath = RepackMetadata.NullIfEmpty(options, "FileContextsPath"),
                    Algorithm = ErofsCompressionAlgorithm.Lz4,
                    Lz4HighCompression = lz4hc,
                    CompressionLevel = compressionLevel,
                    // Repack metadata carries no legacy flag of its own; the app-level setting routes
                    // through the shared default so rebuilt images honor the user's kernel choice.
                    // 重打包元数据不记录 legacy 标志；应用层设置经由共享默认值传递，使重建镜像
                    // 遵循用户对内核的选择。
                    LegacyCompatibility = SharedKernel.ErofsBuildDefaults.LegacyCompatibility,
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