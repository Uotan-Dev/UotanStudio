using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac;

public sealed class PacFormatHandler : IFormatHandler
{
    public string FormatName => "SPRD/UNISOC PAC Firmware";

    public IReadOnlyList<string> FileExtensions => ["pac", "bin"];

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
        if (ext == "pac")
        {
            return true;
        }
        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < SprdHead.Size)
            {
                return false;
            }
            using var reader = new BinaryReader(fs);


            fs.Seek(SprdHead.Size - 8, SeekOrigin.Begin);
            return reader.ReadUInt32() == 0xFFFAFFFA;
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
            var entries = PacExtractor.List(filePath);
            var summary = $"SPRD PAC: {entries.Count} files";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n))],
                metadata: new Dictionary<string, string> { ["FileCount"] = entries.Count.ToString() }));
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
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options?.VerifyChecksums == true)
            {
                var (headOk, dataOk) = PacExtractor.Check(filePath, cancellationToken);
                if (!headOk || !dataOk)
                {
                    return Task.FromResult(FirmwareOpResult.Fail(
                        $"PAC checksum verification failed (header: {headOk}, data: {dataOk})."));
                }
            }

            var selected = options?.CreateSelectedEntrySet();
            var count = PacExtractor.Extract(filePath, outputDirectory,
                selected is null ? null : selected.Contains, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} files to {outputDirectory}"));
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
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>

        Task.FromResult(FirmwareOpResult.Fail("PAC packing is not supported by this handler."));
}
