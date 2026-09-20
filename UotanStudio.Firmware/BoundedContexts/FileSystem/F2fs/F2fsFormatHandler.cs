using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
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
                var files = parser.ListFiles();
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "F2FS",
                    ["BlockSize"] = parser.BlockSize.ToString(),
                    ["SegmentSize"] = parser.SegmentSize.ToString(),
                    ["BlockCount"] = parser.BlockCount.ToString(),
                    ["ImageSize"] = parser.ImageSize.ToString(),
                    ["InodeCount"] = parser.InodeCount.ToString(),
                    ["FileCount"] = parser.FileCount.ToString(),
                    ["DirectoryCount"] = parser.DirectoryCount.ToString(),
                    ["VolumeName"] = parser.VolumeName,
                };
                var components = files.Select(file => FirmwareComponent.Of(file.Path, file.Size)).ToList();
                return FirmwareOpResult.Ok(
                    $"{files.Count} entries in F2FS image '{filePath}'.",
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
                if (Directory.Exists(inputPath) && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
                    return FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes.");
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var options = FileSystemPackingOptions.OptionsFor(inputPath, FormatName);
                F2fsBuilder.Build(inputPath, outputPath, new F2fsOptions
                {
                    BlockSize = FileSystemPackingOptions.IntegerOption(options, "BlockSize", 4096),
                    SegmentSize = FileSystemPackingOptions.IntegerOption(options, "SegmentSize", 2 * 1024 * 1024),
                    VolumeName = options.TryGetValue("VolumeName", out var volume) ? volume : "f2fs",
                    FsConfigPath = FileSystemPackingOptions.NullIfEmpty(options, "FsConfigPath"),
                });

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
}