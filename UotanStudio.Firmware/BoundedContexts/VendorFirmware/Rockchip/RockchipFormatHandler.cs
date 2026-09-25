using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Models;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Packing;
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
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            stream.ReadExactly(magic);
            var isRkfw = magic.SequenceEqual("RKFW"u8);
            var (header, entries) = isRkfw
                ? RockchipExtractor.PreviewRkfw(filePath)
                : (null, RockchipExtractor.List(filePath));
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = new Dictionary<string, string>
            {
                ["Container"] = isRkfw ? "RKFW" : "RKAF",
                ["FileCount"] = entries.Count.ToString(CultureInfo.InvariantCulture),
                ["ChecksumStatus"] = isRkfw ? "NotVerified" : "Verified"
            };
            if (header is not null)
            {
                metadata["RkfwVersion"] = $"{header.Version >> 24}.{(header.Version >> 16) & 0xff}.{header.Version & 0xffff}";
                metadata["MergeVersion"] = $"0x{header.MergeVersion:X8}";
                metadata["ChipId"] = $"0x{header.Chip:X8}";
                metadata["LoaderOffset"] = header.BootOffset.ToString(CultureInfo.InvariantCulture);
                metadata["LoaderSize"] = header.BootSize.ToString(CultureInfo.InvariantCulture);
                metadata["ImageOffset"] = header.ImageOffset.ToString(CultureInfo.InvariantCulture);
                metadata["ImageSize"] = header.ImageSize.ToString(CultureInfo.InvariantCulture);
                var time = header.ReleaseTime;
                var year = time[0] | time[1] << 8;
                if (year is >= 1 and <= 9999 && time[2] is >= 1 and <= 12 &&
                    time[3] >= 1 && time[3] <= DateTime.DaysInMonth(year, time[2]) &&
                    time[4] < 24 && time[5] < 60 && time[6] < 60)
                    metadata["ReleaseTime"] = $"{year:D4}-{time[2]:D2}-{time[3]:D2} {time[4]:D2}:{time[5]:D2}:{time[6]:D2}";
            }
            var summary = $"Rockchip {(isRkfw ? "RKFW" : "RKAF")}: {entries.Count} entries";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(static e => e.Name)],
                metadata: metadata,
                components: [.. entries.Select(static e => FirmwareComponent.Of(e.Name, e.Size))]));
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
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
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
    public async Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => await PackAsync(inputPath, outputPath, new RockchipPackOptions(), cancellationToken).ConfigureAwait(false);

    public async Task<FirmwareOpResult> PackAsync(
        string inputPath,
        string outputPath,
        RockchipPackOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RockchipPacker.PackAsync(inputPath, outputPath, options, cancellationToken).ConfigureAwait(false);
            var entries = RockchipExtractor.List(outputPath);
            return FirmwareOpResult.Ok($"Packed Rockchip firmware with {entries.Count} entries.",
                entries: [.. entries.Select(static e => e.Name)],
                metadata: new Dictionary<string, string> { ["FileCount"] = entries.Count.ToString() });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    private static IProgress<double>? ToProgress(IFirmwareProgress? progress)
        => progress is null ? null : new ProgressAdapter(progress);

    private sealed class ProgressAdapter(IFirmwareProgress inner) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * 100, "Extracting Rockchip firmware");
    }
}