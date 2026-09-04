using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Ext4;

/// <summary>
/// IFormatHandler implementation for ext4 filesystem images.
/// <para>ext4 文件系统镜像的 IFormatHandler 实现。</para>
/// </summary>
public sealed class Ext4FormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "EXT4";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "ext4"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            // ext4 magic 0xEF53 位于 superblock（偏移 1024）内偏移 56 处，即文件偏移 1080。
            // The ext4 magic 0xEF53 lives at superblock offset 56 (file offset 1024 + 56 = 1080).
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output directory is empty"));
            }

            using var parser = new Ext4Parser(filePath);
            parser.ExtractToDirectory(outputDirectory, options?.Overwrite ?? true);
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
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

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Ext4Builder.Build(inputPath, fs);
            return Task.FromResult(FirmwareOpResult.Ok(
                $"Packed EXT4 image '{inputPath}' -> '{outputPath}' ({fs.Length} bytes).",
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