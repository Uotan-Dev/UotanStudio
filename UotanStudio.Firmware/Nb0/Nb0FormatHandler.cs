using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>FIH Mobile NB0 固件格式的 IFormatHandler 实现。</para>
/// IFormatHandler implementation for the FIH Mobile NB0 firmware format.
/// </summary>
public sealed class Nb0FormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "FIH Mobile NB0 Firmware";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["nb0", "bin"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "nb0")
        {
            return true;
        }
        try
        {
            // NB0 has no global magic; probe the declared entry count vs file size.
            using var stream = File.OpenRead(filePath);
            Span<byte> countBytes = stackalloc byte[4];
            if (stream.Read(countBytes) != 4)
            {
                return false;
            }
            var count = BitConverter.ToUInt32(countBytes);
            if (count is 0 or > 1024)
            {
                return false;
            }
            var required = 4L + (count * Nb0EntryHeader.StructSize);
            return required <= stream.Length;
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
            var metadata = Nb0Parser.Parse(filePath);
            var summary = $"NB0 firmware: {metadata.Entries.Count} entries, {metadata.TotalSize} bytes" +
                          (string.IsNullOrEmpty(metadata.InferredFirmwareType) ? "" : $", type: {metadata.InferredFirmwareType}") +
                          (metadata.HasMd5Records ? $", {metadata.Md5Records.Count} MD5 records" : "");
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. metadata.Entries.Select(e => e.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["EntryCount"] = metadata.Entries.Count.ToString(),
                    ["TotalSize"] = metadata.TotalSize.ToString(),
                    ["InferredFirmwareType"] = metadata.InferredFirmwareType ?? string.Empty,
                    ["Md5RecordCount"] = metadata.Md5Records.Count.ToString()
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var extractor = new Nb0Extractor();
            var result = extractor.Extract(filePath, outputDirectory, new ExtractionOptions
            {
                Overwrite = options?.Overwrite ?? true
            });
            return !result.Success
                ? Task.FromResult(FirmwareOpResult.Fail(string.Join("; ", result.Errors)))
                : Task.FromResult(FirmwareOpResult.Ok(
                $"Extracted {result.ExtractedFileCount} entries to {outputDirectory}" +
                (result.Warnings.Count > 0 ? $" ({result.Warnings.Count} warnings)" : "")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(inputPath))
            {
                Nb0Packer.PackFromDirectory(inputPath, outputPath);
                var count = Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories).Count();
                return Task.FromResult(FirmwareOpResult.Ok($"Packed {count} files to {outputPath}"));
            }
            if (File.Exists(inputPath))
            {
                var builder = Nb0Packer.CreateBuilder();
                builder.AddFile(inputPath);
                builder.Build(outputPath);
                return Task.FromResult(FirmwareOpResult.Ok($"Packed {Path.GetFileName(inputPath)} to {outputPath}"));
            }
            return Task.FromResult(FirmwareOpResult.Fail("Input path does not exist."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }
}