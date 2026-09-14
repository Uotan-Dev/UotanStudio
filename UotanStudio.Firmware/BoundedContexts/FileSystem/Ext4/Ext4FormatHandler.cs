using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed class Ext4FormatHandler : IFormatHandler
{
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
            var entries = parser.ListFiles();
            var metadata = new Dictionary<string, string>
            {
                ["PartitionSize"] = parser.PartitionSize.ToString(),
                ["BlockSize"] = parser.BlockSize.ToString(),
                ["BlockCount"] = parser.BlockCount.ToString(),
                ["InodeCount"] = parser.InodeCount.ToString(),
                ["FileCount"] = parser.FileCount.ToString(),
                ["DirectoryCount"] = parser.DirectoryCount.ToString(),
            };
            var summary = $"EXT4 image: {parser.BlockCount} blocks x {parser.BlockSize}B = {parser.PartitionSize} bytes, "
                + $"{parser.InodeCount} inodes, {parser.FileCount} files, {parser.DirectoryCount} directories.";
            var components = entries.Select(e => FirmwareComponent.Of(e.Path, e.Size)).ToList();
            return Task.FromResult(FirmwareOpResult.Ok(summary, [.. entries.Select(e => e.Path)], metadata, components));
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
        => UotanStudio.Firmware.SharedKernel.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
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
    {
        try
        {
            if (Directory.Exists(inputPath) && FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
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

            var options = RepackMetadata.OptionsFor(inputPath, FormatName);
            Ext4Builder.Build(inputPath, outputPath, new Ext4Options
            {
                BlockSize = RepackMetadata.IntegerOption(options, "BlockSize", 4096),
                FsConfigPath = RepackMetadata.NullIfEmpty(options, "FsConfigPath"),
                FileContextsPath = RepackMetadata.NullIfEmpty(options, "FileContextsPath"),
            });
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
}