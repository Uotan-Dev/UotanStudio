using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Handles extracting partitions from a payload stream.
/// <para>处理从载荷流中提取分区。</para>
/// </summary>
internal class PayloadExtractor
{
    private static readonly HashSet<InstallOperation.Types.Type> ParallelizableTypes =
    [
        InstallOperation.Types.Type.Replace,
        InstallOperation.Types.Type.ReplaceBz,
        InstallOperation.Types.Type.ReplaceXz,
        (InstallOperation.Types.Type)14, // REPLACE_ZSTD (not in proto but supported)
        InstallOperation.Types.Type.Zero,
        InstallOperation.Types.Type.Discard,
        InstallOperation.Types.Type.Move
    ];

    private static readonly HashSet<InstallOperation.Types.Type> SequentialTypes =
    [
        InstallOperation.Types.Type.SourceCopy,
        InstallOperation.Types.Type.SourceBsdiff,
        InstallOperation.Types.Type.BrotliBsdiff,
        InstallOperation.Types.Type.Puffdiff,
        InstallOperation.Types.Type.Bsdiff
    ];

    /// <summary>
    /// Extracts a single partition from the payload stream to an output file.
    /// <para>从载荷流中提取单个分区到输出文件。</para>
    /// </summary>
    /// <param name="partition">The partition update descriptor containing operations and extents.
    /// <para>包含操作和区间的分区更新描述符。</para>
    /// </param>
    /// <param name="outFilename">The output file path for the extracted partition image.
    /// <para>提取的分区镜像的输出文件路径。</para>
    /// </param>
    /// <param name="stream">The payload stream to read operation data from.
    /// <para>用于读取操作数据的载荷流。</para>
    /// </param>
    /// <param name="baseOffset">The base offset in the stream where data blobs begin.
    /// <para>流中数据块开始的基偏移量。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <exception cref="PayloadUnsupportedOperationException">Thrown when an operation type is genuinely unsupported.
    /// <para>当操作类型确实不受支持时抛出。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when the partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public void ExtractPartition(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(outFilename);
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(outFilename))
        {
            throw new ArgumentException("Output filename cannot be empty or whitespace.", nameof(outFilename));
        }

        try
        {
            using var outFile = new FileStream(outFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920);

            long totalSize = 0;
            foreach (var op in partition.Operations)
            {
                foreach (var ext in op.DstExtents)
                {
                    totalSize += (long)ext.NumBlocks * blockSize;
                }
            }

            if (totalSize > 0)
            {
                outFile.SetLength(totalSize);
            }

            object writeLock = new();
            object streamLock = new();

            var parallelOps = new List<InstallOperation>();
            var sequentialOps = new List<InstallOperation>();

            foreach (var op in partition.Operations)
            {
                if (SequentialTypes.Contains(op.Type))
                {
                    sequentialOps.Add(op);
                }
                else if (ParallelizableTypes.Contains(op.Type))
                {
                    parallelOps.Add(op);
                }
            }

            Parallel.ForEach(
                parallelOps.OrderBy(o => o.DataOffset),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) },
                op => OperationProcessor.ProcessOperation(op, outFile, stream, baseOffset, blockSize, writeLock, streamLock));

            foreach (var op in sequentialOps)
            {
                switch (op.Type)
                {
                    case InstallOperation.Types.Type.SourceCopy:
                        OperationProcessor.ProcessSourceCopy(op, outFile, blockSize);
                        break;
                    case InstallOperation.Types.Type.SourceBsdiff:
                        {
                            var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                            OperationProcessor.ProcessSourceBsdiff(op, patchData, outFile, blockSize);
                        }
                        break;
                    case InstallOperation.Types.Type.BrotliBsdiff:
                        {
                            var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                            OperationProcessor.ProcessBrotliBsdiff(op, patchData, outFile, blockSize);
                        }
                        break;
                    case InstallOperation.Types.Type.Puffdiff:
                        {
                            var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                            OperationProcessor.ProcessPuffdiff(op, patchData, patchData.Length, outFile, blockSize);
                        }
                        break;
                    case InstallOperation.Types.Type.Bsdiff:
                        {
                            var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                            OperationProcessor.ProcessBsdiff(op, patchData, outFile, blockSize);
                        }
                        break;
                    case InstallOperation.Types.Type.Replace:
                        break;
                    case InstallOperation.Types.Type.ReplaceBz:
                        break;
                    case InstallOperation.Types.Type.Move:
                        break;
                    case InstallOperation.Types.Type.Zero:
                        break;
                    case InstallOperation.Types.Type.Discard:
                        break;
                    case InstallOperation.Types.Type.ReplaceXz:
                        break;
                    case InstallOperation.Types.Type.Zucchini:
                        break;
                    case InstallOperation.Types.Type.Lz4diffBsdiff:
                        break;
                    case InstallOperation.Types.Type.Lz4diffPuffdiff:
                        break;
                    default:
                        break;
                }
            }
        }
        catch (PayloadUnsupportedOperationException)
        {
            throw;
        }
        catch (PartitionExtractionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PartitionExtractionException(
                $"Failed to extract partition '{partition.PartitionName}' to '{outFilename}': {ex.Message}",
                partition.PartitionName,
                ex);
        }
    }

    /// <summary>
    /// Asynchronously extracts a single partition from the payload stream to an output file.
    /// <para>异步从载荷流中提取单个分区到输出文件。</para>
    /// </summary>
    /// <param name="partition">The partition update descriptor containing operations and extents.
    /// <para>包含操作和区间的分区更新描述符。</para>
    /// </param>
    /// <param name="outFilename">The output file path for the extracted partition image.
    /// <para>提取的分区镜像的输出文件路径。</para>
    /// </param>
    /// <param name="stream">The payload stream to read operation data from.
    /// <para>用于读取操作数据的载荷流。</para>
    /// </param>
    /// <param name="baseOffset">The base offset in the stream where data blobs begin.
    /// <para>流中数据块开始的基偏移量。</para>
    /// </param>
    /// <param name="blockSize">The block size used for extent calculations.
    /// <para>用于区间计算的块大小。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task that represents the asynchronous extraction operation.
    /// <para>表示异步提取操作的任务。</para>
    /// </returns>
    /// <exception cref="PayloadUnsupportedOperationException">Thrown when an operation type is genuinely unsupported.
    /// <para>当操作类型确实不受支持时抛出。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when the partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public Task ExtractPartitionAsync(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken = default)
        => Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize), cancellationToken);

    /// <summary>
    /// Extracts all matching partitions from the manifest to the output directory.
    /// <para>从清单中提取所有匹配的分区到输出目录。</para>
    /// </summary>
    /// <param name="manifest">The delta archive manifest containing partition definitions.
    /// <para>包含分区定义的增量归档清单。</para>
    /// </param>
    /// <param name="stream">The payload stream to read operation data from.
    /// <para>用于读取操作数据的载荷流。</para>
    /// </param>
    /// <param name="baseOffset">The base offset in the stream where data blobs begin.
    /// <para>流中数据块开始的基偏移量。</para>
    /// </param>
    /// <param name="extractFiles">An array of partition names to extract. If empty, all partitions are extracted.
    /// <para>要提取的分区名称数组。如果为空，则提取所有分区。</para>
    /// </param>
    /// <param name="outputDir">The output directory for extracted partition images.
    /// <para>提取的分区镜像的输出目录。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when any partition extraction fails.
    /// <para>当任何分区提取失败时抛出。</para>
    /// </exception>
    public void ExtractPartitions(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(extractFiles);

        if (string.IsNullOrEmpty(outputDir))
        {
            outputDir = Directory.GetCurrentDirectory();
        }

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        HashSet<string>? extractSet = extractFiles.Length > 0 ? [.. extractFiles] : null;

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

            var outFilename = Path.Combine(outputDir, $"{partition.PartitionName}.img");
            ExtractPartition(partition, outFilename, stream, baseOffset, manifest.BlockSize);
        }
    }

    /// <summary>
    /// Asynchronously extracts all matching partitions from the manifest to the output directory.
    /// <para>异步从清单中提取所有匹配的分区到输出目录。</para>
    /// </summary>
    /// <param name="manifest">The delta archive manifest containing partition definitions.
    /// <para>包含分区定义的增量归档清单。</para>
    /// </param>
    /// <param name="stream">The payload stream to read operation data from.
    /// <para>用于读取操作数据的载荷流。</para>
    /// </param>
    /// <param name="baseOffset">The base offset in the stream where data blobs begin.
    /// <para>流中数据块开始的基偏移量。</para>
    /// </param>
    /// <param name="extractFiles">An array of partition names to extract. If empty, all partitions are extracted.
    /// <para>要提取的分区名称数组。如果为空，则提取所有分区。</para>
    /// </param>
    /// <param name="outputDir">The output directory for extracted partition images.
    /// <para>提取的分区镜像的输出目录。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task that represents the asynchronous extraction operation.
    /// <para>表示异步提取操作的任务。</para>
    /// </returns>
    /// <exception cref="PartitionExtractionException">Thrown when any partition extraction fails.
    /// <para>当任何分区提取失败时抛出。</para>
    /// </exception>
    public async Task ExtractPartitionsAsync(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(extractFiles);

        if (string.IsNullOrEmpty(outputDir))
        {
            outputDir = Directory.GetCurrentDirectory();
        }

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        HashSet<string>? extractSet = extractFiles.Length > 0 ? [.. extractFiles] : null;

        foreach (var partition in manifest.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(partition.PartitionName))
            {
                continue;
            }

            if (extractSet != null && !extractSet.Contains(partition.PartitionName))
            {
                continue;
            }

            var outFilename = Path.Combine(outputDir, $"{partition.PartitionName}.img");
            await ExtractPartitionAsync(partition, outFilename, stream, baseOffset, manifest.BlockSize, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] ReadOperationData(InstallOperation op, Stream stream, ulong baseOffset, object streamLock)
    {
        var data = new byte[op.DataLength];
        var dataPos = (long)(baseOffset + op.DataOffset);
        lock (streamLock)
        {
            stream.Seek(dataPos, SeekOrigin.Begin);
            var bytesRead = 0;
            while (bytesRead < data.Length)
            {
                var read = stream.Read(data, bytesRead, data.Length - bytesRead);
                if (read == 0)
                {
                    break;
                }
                bytesRead += read;
            }
            if (bytesRead != data.Length)
            {
                throw new PartitionExtractionException(
                    $"Failed to read {data.Length} bytes from payload stream at offset {dataPos}, only {bytesRead} bytes read",
                    string.Empty);
            }
        }
        return data;
    }
}