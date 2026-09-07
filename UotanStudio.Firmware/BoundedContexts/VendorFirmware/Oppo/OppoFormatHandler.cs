using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo;

public sealed class OppoFormatHandler : IFormatHandler
{
    private readonly OppReader _reader = new();

    public string FormatName => "Oppo Firmware (OFP/OPS/OZIP)";

    public IReadOnlyList<string> FileExtensions { get; } = ["ofp", "ops", "ozip", "bin"];

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        var strongExtension = extension.Equals(".ofp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ops", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ozip", StringComparison.OrdinalIgnoreCase);
        var genericBin = extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);


        if (!strongExtension && !genericBin)
        {
            return false;
        }


        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> head = stackalloc byte[0x3C];
            var read = fs.Read(head);
            if (read >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
            {
                return false;
            }
            if (read >= 0x3C
                && head[0x38] == (byte)'A' && head[0x39] == (byte)'R'
                && head[0x3A] == (byte)'M' && head[0x3B] == (byte)'d')
            {
                return false;
            }
        }
        catch
        {

        }

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
            return strongExtension;
        }


        return strongExtension;
    }

    /// <summary>
    /// Reads format metadata without unpacking.
    /// <para>读取格式元数据（不解包）。</para>
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


                var components = archive.Entries
                    .Where(e => IsFirmwareComponent(e.Name))
                    .Select(e => FirmwareComponent.Of(e.Name, e.Size))
                    .ToList();

                return FirmwareOpResult.Ok(
                    $"Read {metadata.EntryCount} entries from '{filePath}' as {metadata.FormatName}.",
                    components: components,
                    metadata: meta);
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
    /// Unpacks the firmware file to the output directory.
    /// <para>将固件文件解包到输出目录。</para>
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
    /// Determines whether an entry is a real firmware component (excludes OTA config, signing metadata, and other non-firmware directories).
    /// <para>判断条目是否为真正的固件组件（排除 OTA 配置、签名等非固件目录）。</para>
    /// </summary>
    private static bool IsFirmwareComponent(string name)
    {

        if (name.StartsWith("firmware-update/", StringComparison.Ordinal))
            return true;


        if (name.EndsWith(".dat.br", StringComparison.Ordinal)
            || name.EndsWith(".new.dat.br", StringComparison.Ordinal)
            || name.EndsWith(".transfer.list", StringComparison.Ordinal)
            || name.EndsWith(".patch.dat", StringComparison.Ordinal))
            return true;


        if (name.StartsWith("compatibility.zip", StringComparison.Ordinal)
            || name.StartsWith("oppo_update_script", StringComparison.Ordinal)
            || name.StartsWith("oppo_operate_data", StringComparison.Ordinal))
            return true;


        if (name.StartsWith("data_update_patch/", StringComparison.Ordinal))
            return false;
        if (name.StartsWith("META-INF/", StringComparison.Ordinal))
            return false;
        if (name.Equals("oppo_metadata", StringComparison.Ordinal))
            return false;


        return !name.Contains('/');
    }

    /// <summary>
    /// Pack operation.
    /// <para>打包操作。</para>
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
