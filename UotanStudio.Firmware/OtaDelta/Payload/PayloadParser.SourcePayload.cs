using System.IO.Compression;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>
/// Source-image part of <c>PayloadParser</c>: resolves base images for source-dependent operations and
/// materializes ZIP-embedded payloads to temporary files that are cleaned up by the callers.
/// <para><c>PayloadParser</c> 的源镜像分部：为源依赖操作解析基座镜像，并把 ZIP 内嵌载荷物化到
/// 由调用方清理的临时文件。</para>
/// </summary>
public static partial class PayloadParser
{
    private static Dictionary<string, string>? ResolveSourceImages(
        DeltaArchiveManifest manifest,
        string[] extractFiles,
        string? sourcePayloadPath,
        IReadOnlyDictionary<string, string>? sourceImages,
        List<string> tempSourceFiles)
    {
        if (string.IsNullOrWhiteSpace(sourcePayloadPath) && (sourceImages == null || sourceImages.Count == 0))
        {
            return null;
        }

        HashSet<string>? extractSet = extractFiles.Length > 0 ? [.. extractFiles] : null;
        Dictionary<string, string>? resolved = null;

        foreach (var partition in manifest.Partitions)
        {
            if (string.IsNullOrEmpty(partition.PartitionName))
            {
                continue;
            }

            if (extractSet != null && !extractSet.Contains(partition.PartitionName))
            {
                continue;
            }


            if (!partition.Operations.Any(op => PayloadExtractor.IsSourceDependent(op.Type)))
            {
                continue;
            }

            string? basePath = null;
            if (sourceImages != null && sourceImages.TryGetValue(partition.PartitionName, out var imagePath) && !string.IsNullOrWhiteSpace(imagePath))
            {
                basePath = imagePath;
            }
            else if (!string.IsNullOrWhiteSpace(sourcePayloadPath))
            {
                basePath = ExtractBaseImageFromSourcePayload(sourcePayloadPath, partition.PartitionName, sourceImages, tempSourceFiles);
            }

            if (basePath == null)
            {
                continue;
            }

            resolved ??= new Dictionary<string, string>(StringComparer.Ordinal);
            resolved[partition.PartitionName] = basePath;
        }

        return resolved;
    }

    private static string ExtractBaseImageFromSourcePayload(
        string sourcePayloadPath,
        string partitionName,
        IReadOnlyDictionary<string, string>? sourceImages,
        List<string> tempSourceFiles)
    {
        var tempFile = TempFileHelper.CreateTempFile();
        tempSourceFiles.Add(tempFile);
        try
        {
            string? zipTemp = null;
            try
            {
                var payloadType = DetectPayloadType(sourcePayloadPath);
                if (payloadType == PayloadType.ZipArchive)
                {
                    zipTemp = ExtractPayloadFromZip(sourcePayloadPath);
                }

                using var stream = File.OpenRead(zipTemp ?? sourcePayloadPath);
                var (manifest, header) = ReadManifest(stream);

                PartitionUpdate? partition = null;
                foreach (var p in manifest.Partitions)
                {
                    if (string.Equals(p.PartitionName, partitionName, StringComparison.Ordinal))
                    {
                        partition = p;
                        break;
                    }
                }

                if (partition == null)
                {
                    throw new PartitionExtractionException(
                        $"Source payload '{sourcePayloadPath}' does not contain partition '{partitionName}'. " +
                        $"源载荷 '{sourcePayloadPath}' 不包含分区 '{partitionName}'。",
                        partitionName);
                }

                new PayloadExtractor().ExtractPartition(partition, tempFile, stream, header.BaseOffset, manifest.BlockSize, sourceImages);
                return tempFile;
            }
            finally
            {
                if (zipTemp != null)
                {
                    TempFileHelper.SafeDelete(zipTemp);
                }
            }
        }
        catch (PartitionExtractionException ex)
        {
            throw new PartitionExtractionException(
                $"Failed to extract base image for partition '{partitionName}' from source payload '{sourcePayloadPath}': " +
                "the source payload must be a full payload or the partition must be supplied in sourceImages. " +
                $"从源载荷 '{sourcePayloadPath}' 提取分区 '{partitionName}' 的基座镜像失败：" +
                "源载荷必须是完整载荷，或需在 sourceImages 中提供该分区。",
                partitionName,
                ex);
        }
    }

    private static string ExtractPayloadFromZip(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var payloadEntry = FindPayloadEntry(archive)
            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
        var tempPath = TempFileHelper.CreateTempFile();
        using (var outFile = File.Create(tempPath))
        using (var entryStream = payloadEntry.Open())
        {
            entryStream.CopyTo(outFile);
        }

        return tempPath;
    }
}