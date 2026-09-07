using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;

public sealed class Nb0FormatHandler : IFormatHandler
{
    public string FormatName => "FIH Mobile NB0 Firmware";

    public IReadOnlyList<string> FileExtensions => ["nb0", "bin"];

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
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "nb0")
        {
            return true;
        }
        try
        {

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

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
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

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
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
