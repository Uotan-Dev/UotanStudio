using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi;

public sealed class NtpiFormatHandler : IFormatHandler
{
    public string FormatName => "NTPI Firmware Package";

    public IReadOnlyList<string> FileExtensions => ["ntpi", "bin", "fw"];

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
        if (ext == "ntpi")
        {
            return true;
        }
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) == 4 && magic.SequenceEqual("NTPI"u8);
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
            using var reader = NtpiReader.CreateDefault();
            var info = reader.ReadInfo(filePath);
            var summary = $"NTPI v{info.MajorVersion}.{info.MinorVersion}.{info.PatchVersion}: " +
                          $"{info.Regions.Count} regions, {info.FileEntries.Count} files";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. info.FileEntries.Select(e => e.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["MajorVersion"] = info.MajorVersion.ToString(),
                    ["MinorVersion"] = info.MinorVersion.ToString(),
                    ["PatchVersion"] = info.PatchVersion.ToString(),
                    ["RegionCount"] = info.Regions.Count.ToString(),
                    ["FileCount"] = info.FileEntries.Count.ToString()
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
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var reader = NtpiReader.CreateDefault();
            var progress = options?.Progress is null ? null : new Progress<NtpiExtractionProgress>(p => options.Progress.Report(p.CompletedFiles * 100.0 / Math.Max(1, p.TotalFiles), p.CurrentFileName));
            reader.Unpack(filePath, outputDirectory, progress, fileFilter: null);
            var count = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Count();
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
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(inputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Input path must be a directory for NTPI packing."));
            }
            using var writer = NtpiWriter.CreateDefault();
            writer.Pack(inputPath, outputPath);
            var count = Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories).Count();
            return Task.FromResult(FirmwareOpResult.Ok($"Packed {count} files to {outputPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }
}
