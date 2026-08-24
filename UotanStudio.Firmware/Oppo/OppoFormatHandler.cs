using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>Oppo 固件格式（OFP QC/OFP MTK/OPS/OZip）的统一格式处理器。</para>
/// Unified format handler for Oppo firmware formats (OFP QC/OFP MTK/OPS/OZip).
/// </summary>
public sealed class OppoFormatHandler : IFormatHandler
{
    private readonly OppReader _reader = new();

    /// <summary>
    /// <para>获取人类可读的格式名称。</para>
    /// Gets the human-readable format name.
    /// </summary>
    public string FormatName => "Oppo Firmware (OFP/OPS/OZIP)";

    /// <summary>
    /// <para>获取典型文件扩展名（不带点，小写）。</para>
    /// Gets the typical file extensions (without dot, lowercase).
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; } = ["ofp", "ops", "ozip", "bin"];

    /// <summary>
    /// <para>判断该处理器能否处理指定文件。</para>
    /// Determines whether this handler can process the given file.
    /// Detection is based on the extension (.ofp/.ops/.ozip/.bin) or the magic numbers.
    /// <para>依据扩展名（.ofp/.ops/.ozip/.bin）或魔数进行判断。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        var extensionMatch = extension.Equals(".ofp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ops", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ozip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var fs = File.OpenRead(filePath);
            var format = Parsers.FormatDetector.DetectFormatFromStream(fs);
            if (format != OppFormat.Unknown)
            {
                return true;
            }
        }
        catch
        {
            return extensionMatch;
        }

        return extensionMatch;
    }

    /// <summary>
    /// <para>读取格式元数据（不解包）。</para>
    /// Reads format metadata without unpacking.
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var archive = await _reader.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
                var metadata = archive.Metadata;
                cancellationToken.ThrowIfCancellationRequested();

                var meta = new Dictionary<string, string>
                {
                    ["Format"] = metadata.FormatName,
                    ["FormatCode"] = metadata.Format.ToString(),
                    ["FileSize"] = metadata.FileSize.ToString(),
                    ["EntryCount"] = metadata.EntryCount.ToString(),
                };

                if (metadata.ProjectName is not null)
                {
                    meta["ProjectName"] = metadata.ProjectName;
                }

                if (metadata.CpuModel is not null)
                {
                    meta["CpuModel"] = metadata.CpuModel;
                }

                if (metadata.FlashType is not null)
                {
                    meta["FlashType"] = metadata.FlashType;
                }

                if (metadata.FirmwareName is not null)
                {
                    meta["FirmwareName"] = metadata.FirmwareName;
                }

                if (metadata.ProjectId is not null)
                {
                    meta["ProjectId"] = metadata.ProjectId;
                }

                if (metadata.KeyId is not null)
                {
                    meta["KeyId"] = metadata.KeyId;
                }

                var names = archive.Entries.Select(e => e.Name).ToList();

                return FirmwareOpResult.Ok(
                    $"Read {metadata.EntryCount} entries from '{filePath}' as {metadata.FormatName}.",
                    names,
                    meta);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read Oppo firmware metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// <para>将固件文件解包到输出目录。</para>
    /// Unpacks the firmware file to the output directory.
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                options?.Progress?.Report(5, "Reading Oppo firmware metadata...");
                var archive = await _reader.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
                var metadata = archive.Metadata;
                var names = archive.Entries.Select(e => e.Name).ToList();

                options?.Progress?.Report(10, "Extracting entries...");
                var extractOptions = new OppExtractOptions
                {
                    Overwrite = options?.Overwrite ?? true,
                };

                var result = await _reader.ExtractAsync(
                    filePath,
                    outputDirectory,
                    extractOptions,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                options?.Progress?.Report(100, "Extraction complete.");

                var meta = new Dictionary<string, string>
                {
                    ["Format"] = metadata.FormatName,
                    ["EntryCount"] = metadata.EntryCount.ToString(),
                };

                if (metadata.KeyId is not null)
                {
                    meta["KeyId"] = metadata.KeyId;
                }

                return FirmwareOpResult.Ok(
                    $"Extracted {result.ExtractedFileCount} entries from '{filePath}' to '{outputDirectory}'.",
                    names,
                    meta);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack Oppo firmware '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// <para>打包操作。Oppo 固件格式打包目前不支持。</para>
    /// Pack operation. Packing to Oppo firmware formats is not currently supported.
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return FirmwareOpResult.Fail(
                "Packing to Oppo firmware formats (OFP/OPS/OZIP) is not supported by this handler. " +
                "Only reading and unpacking are available.");
        }, cancellationToken);
    }
}