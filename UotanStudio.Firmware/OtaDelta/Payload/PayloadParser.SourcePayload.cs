using System.IO.Compression;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload.IO;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>
/// Source-image part of <c>PayloadParser</c>: resolves base images for source-dependent operations into
/// temporary files that are cleaned up by the callers. A ZIP-embedded payload that is stored uncompressed
/// is read in place through the archive's own byte range, and only a compressed entry is materialized
/// to a temporary file first.
/// <para><c>PayloadParser</c> 的源镜像分部：为源依赖操作把基座镜像解析到由调用方清理的临时文件。
/// ZIP 内嵌载荷在未压缩存储时就地读取归档自身的字节区间，只有被压缩的条目才先物化到临时文件。</para>
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
                using var payload = payloadType == PayloadType.ZipArchive
                    ? OpenSourcePayloadFromZip(sourcePayloadPath, ref zipTemp)
                    : File.OpenRead(sourcePayloadPath);
                var (manifest, header) = ReadManifest(payload);

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

                new PayloadExtractor().ExtractPartition(partition, tempFile, payload, header.BaseOffset, manifest.BlockSize, sourceImages);
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

    /// <summary>
    /// Reads the payload bytes of a ZIP package used as a source payload. A stored entry is read in place
    /// through its own byte range; a compressed entry is materialized to a temporary file first.
    /// <para>读取作为源载荷的 ZIP 包的载荷字节。未压缩条目通过其自身字节区间就地读取；被压缩的条目
    /// 先物化到临时文件。</para>
    /// </summary>
    /// <param name="sourcePayloadPath">
    /// ZIP update package holding the embedded payload; opened read-only and read from offset zero of the
    /// payload entry. <para>内嵌载荷所在的 ZIP 升级包；以只读方式打开，并从载荷条目的偏移零开始读取。</para>
    /// </param>
    /// <param name="zipTemp">
    /// Receives the materialized temporary payload path when the entry could not be read in place; the
    /// caller deletes it and the value stays <see langword="null"/> otherwise.
    /// <para>当条目无法就地读取时接收物化出的临时载荷路径，由调用方删除；否则保持 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// A seekable payload stream owned by the caller.
    /// <para>由调用方负责释放的可定位载荷流。</para>
    /// </returns>
    private static Stream OpenSourcePayloadFromZip(string sourcePayloadPath, ref string? zipTemp)
    {
        if (!TryGetStoredPayloadLocation(sourcePayloadPath, out var dataStart, out var payloadLength))
        {
            zipTemp = ExtractPayloadFromZip(sourcePayloadPath);
            return File.OpenRead(zipTemp);
        }

        return new StoredZipEntryStream(sourcePayloadPath, dataStart, payloadLength);
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