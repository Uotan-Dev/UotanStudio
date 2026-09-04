using System.IO.Compression;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Models;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload;

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

            // Only partitions with source-dependent operations need a base image; skipping the rest
            // avoids spurious errors when the source payload does not contain every partition.
            // 只有含源依赖操作的分区才需要基座镜像；跳过其余分区可避免源载荷不包含某分区时报错。
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

    private static string ExtractPayloadFromZip(string filepath)
    {
        using var zr = ZipFile.OpenRead(filepath);
        var zf = FindPayloadEntry(zr)
            ?? throw new InvalidManifestException($"{PayloadConstants.PayloadFilename} not found in the zip file");
        var tempPath = TempFileHelper.CreateTempFile();
        using (var outFile = File.Create(tempPath))
        using (var zfStream = zf.Open())
        {
            zfStream.CopyTo(outFile);
        }

        return tempPath;
    }
}
