using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Rockchip;

/// <summary>
/// <para>Rockchip RKAF/RKFW 固件镜像的 IFormatHandler 实现（只读解析）。</para>
/// IFormatHandler implementation for Rockchip RKAF/RKFW firmware images (read-only parsing).
/// </summary>
public sealed class RockchipFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Rockchip RKAF/RKFW Firmware";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["rkfw"];

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
            if (fs.Length < RkFwHeader.StructSize)
            {
                return false;
            }
            using var reader = new BinaryReader(fs);
            var tag = reader.ReadUInt32();
            return tag is RkFwHeader.MagicRkfw or RkFwHeader.MagicRkaf;
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
            var entries = RockchipExtractor.List(filePath);
            var summary = $"Rockchip RKAF/RKFW: {entries.Count} entries";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(static e => e.Name)],
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
            var count = RockchipExtractor.Extract(filePath, outputDirectory, ToProgress(options?.Progress));
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} entries to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        // RKAF/RKFW packing would require regenerating the afptool partition directory; not supported.
        Task.FromResult(FirmwareOpResult.Fail("Rockchip RKAF/RKFW packing is not supported by this handler."));

    private static IProgress<double>? ToProgress(IFirmwareProgress? progress)
        => progress is null ? null : new ProgressAdapter(progress);

    private sealed class ProgressAdapter(IFirmwareProgress inner) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * 100, "Extracting Rockchip firmware");
    }
}