using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip;

public sealed class RockchipFormatHandler : IFormatHandler
{
    public string FormatName => "Rockchip RKAF/RKFW Firmware";

    public IReadOnlyList<string> FileExtensions => ["rkfw", "rkaf"];

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

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
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
        catch (OperationCanceledException) { throw; }
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
            var count = RockchipExtractor.Extract(filePath, outputDirectory, ToProgress(options?.Progress),
                options?.Overwrite ?? true, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} entries to {outputDirectory}"));
        }
        catch (OperationCanceledException) { throw; }
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

        Task.FromResult(FirmwareOpResult.Fail("Rockchip RKAF/RKFW packing is not supported by this handler."));

    private static IProgress<double>? ToProgress(IFirmwareProgress? progress)
        => progress is null ? null : new ProgressAdapter(progress);

    private sealed class ProgressAdapter(IFirmwareProgress inner) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * 100, "Extracting Rockchip firmware");
    }
}
