using System.Text;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Models;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;

public sealed class PayloadFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = " The directory packer creates an unsigned full payload.";
    }

    public string FormatName => "Android OTA Payload (payload.bin)";

    public IReadOnlyList<string> FileExtensions { get; } = ["bin", "payload"];

    /// <summary>
    /// Determines whether this handler can process the given file.
    /// <para>判断该处理器能否处理指定文件；依据 .bin/.payload 扩展名或 "CrAU" 魔数进行判断。</para>
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
            Span<byte> header = stackalloc byte[4];
            var read = fs.Read(header);
            if (read >= 4 && Encoding.ASCII.GetString(header) == PayloadConstants.PayloadMagic)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
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
                var metadata = await PayloadParser.GetPayloadMetadataAsync(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var meta = new Dictionary<string, string>
                {
                    ["Format"] = FormatName,
                    ["PartitionCount"] = metadata.PartitionNames.Count.ToString(),
                    ["BlockSize"] = metadata.BlockSize.ToString(),
                    ["MinorVersion"] = metadata.Manifest.MinorVersion.ToString(),
                    ["ManifestLength"] = MetadataDisplay.Number(metadata.ManifestLength),
                    ["MetadataSignatureLength"] = MetadataDisplay.Number(metadata.MetadataSignatureLength),
                    ["BaseOffset"] = MetadataDisplay.Number(metadata.BaseOffset),
                    ["MaxTimestamp"] = MetadataDisplay.Number(metadata.MaxTimestamp),
                    ["SignaturesOffset"] = MetadataDisplay.Number(metadata.SignaturesOffset),
                    ["SignaturesSize"] = MetadataDisplay.Number(metadata.SignaturesSize),
                    ["PartialUpdate"] = metadata.Manifest.PartialUpdate.ToString(),
                };

                var components = PartitionInfoHelper.ComputePartitionInfo(metadata.Manifest)
                    .Select(p => FirmwareComponent.Of(p.Name, (long)p.SizeBytes))
                    .ToList();

                return FirmwareOpResult.Ok(
                    $"Read {metadata.PartitionNames.Count} partitions from '{filePath}'.",
                    metadata.PartitionNames,
                    meta,
                    components);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read payload metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks all partitions of the payload to the output directory.
    /// <para>将载荷的所有分区解包到输出目录。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var metadata = await PayloadParser.GetPayloadMetadataAsync(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                options?.Progress?.Report(5, "Reading payload manifest...");
                var byteProgressAdapter = options?.Progress is null
                    ? null
                    : new Progress<(long Processed, long Total)>(tuple =>
                    {
                        var (processed, total) = tuple;
                        if (total <= 0)
                        {
                            return;
                        }

                        var percent = 5.0 + (processed * 95.0) / total;
                        options.Progress.Report(Math.Clamp(percent, 5.0, 100.0), "Extracting payload partitions...");
                    });
                await PayloadParser.ExtractAsync(filePath, null, outputDirectory, cancellationToken, byteProgress: byteProgressAdapter).ConfigureAwait(false);
                options?.Progress?.Report(100, "Extraction complete.");

                return FirmwareOpResult.Ok(
                    $"Extracted {metadata.PartitionNames.Count} partitions from '{filePath}' to '{outputDirectory}'.",
                    metadata.PartitionNames);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs a directory of partition images into a payload.bin file.
    /// <para>将分区镜像目录打包为 payload.bin 文件。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var images = Directory.EnumerateFiles(inputPath, "*.img", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                    .ToList();
                if (images.Count == 0)
                {
                    return FirmwareOpResult.Fail($"No *.img partition images found in '{inputPath}'.");
                }

                var names = new List<string>();
                using (var builder = new PayloadBuilder())
                {
                    foreach (var image in images)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(image);
                        builder.AddPartition(name, image);
                        names.Add(name);
                    }

                    builder.Build(outputPath);
                }

                return FirmwareOpResult.Ok(
                    $"Packed {names.Count} partitions from '{inputPath}' into '{outputPath}'.",
                    names);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack '{inputPath}': {ex.Message}");
            }
        }, cancellationToken);
    }
}