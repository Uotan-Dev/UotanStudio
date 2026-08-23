using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Pac;

/// <summary>
/// <para>SPRD PAC 固件格式的 IFormatHandler 实现。</para>
/// IFormatHandler implementation for the SPRD PAC firmware format.
/// </summary>
public sealed class PacFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "SPRD/UNISOC PAC Firmware";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["pac", "bin"];

    /// <inheritdoc />
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
            fs.Seek(SprdHead.Size - 4, SeekOrigin.Begin);
            return reader.ReadUInt32() == 0xFFFAFFFA;
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = PacExtractor.Extract(filePath, outputDirectory);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} files to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        // PAC packing requires re-generating the full SPRD directory table + CRC; not supported.
        Task.FromResult(FirmwareOpResult.Fail("PAC packing is not supported by this handler."));
}