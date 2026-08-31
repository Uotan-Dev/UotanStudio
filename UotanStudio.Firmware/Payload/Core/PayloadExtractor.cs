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
        InstallOperation.Types.Type.Bsdiff,
        InstallOperation.Types.Type.Zucchini,
        InstallOperation.Types.Type.Lz4diffBsdiff,
        InstallOperation.Types.Type.Lz4diffPuffdiff
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
    /// <param name="sourceImages">Optional per-partition base image file path mapping. When a partition
    /// with source-dependent operations is extracted, its base (old) partition image is streamed into
    /// the temporary output file first so source extents resolve to base data instead of zero-filled
    /// bytes. A partition with source-dependent operations and no matching base image throws
    /// <see cref="PartitionExtractionException"/>.
    /// <para>可选的每分区基座镜像文件路径映射。提取含源依赖操作的分区时，会先将该分区的基座
    /// （旧）镜像流式写入临时输出文件，使源区间解析到基座数据而非零填充字节。含源依赖操作但
    /// 没有对应基座镜像的分区会抛出 <see cref="PartitionExtractionException"/>。</para>
    /// </param>
    /// <exception cref="PayloadUnsupportedOperationException">Thrown when an operation type is genuinely unsupported.
    /// <para>当操作类型确实不受支持时抛出。</para>
    /// </exception>
    /// <exception cref="PartitionExtractionException">Thrown when the partition extraction fails.
    /// <para>当分区提取失败时抛出。</para>
    /// </exception>
    public void ExtractPartition(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, IReadOnlyDictionary<string, string>? sourceImages = null)
        => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize, CancellationToken.None, sourceImages);

    private void ExtractPartition(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? sourceImages)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(outFilename);
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(outFilename))
        {
            throw new ArgumentException("Output filename cannot be empty or whitespace.", nameof(outFilename));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullOutputPath = Path.GetFullPath(outFilename);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var tempPath = fullOutputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var outFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920))
            {

                long totalSize = 0;
                try
                {
                    foreach (var op in partition.Operations)
                    {
                        foreach (var ext in op.DstExtents)
                        {
                            var endBlock = checked(ext.StartBlock + ext.NumBlocks);
                            var endOffset = checked(endBlock * blockSize);
                            totalSize = Math.Max(totalSize, checked((long)endOffset));
                        }
                    }
                }
                catch (OverflowException ex)
                {
                    throw new PartitionExtractionException("Destination extent end offset exceeds the supported range", partition.PartitionName, ex);
                }

                if (totalSize > 0)
                {
                    outFile.SetLength(totalSize);
                }

                // Source-dependent operations (SOURCE_COPY/SOURCE_BSDIFF/BROTLI_BSDIFF/PUFFDIFF/
                // BSDIFF/LZ4DIFF_*/ZUCCHINI) resolve their source extents against the base partition
                // image. Without a base these would silently read zero-filled bytes and produce corrupt
                // output, so a base is mandatory whenever such an operation is present.
                // 源依赖操作（SOURCE_COPY/SOURCE_BSDIFF/BROTLI_BSDIFF/PUFFDIFF/BSDIFF/LZ4DIFF_*/ZUCCHINI）
                // 从基座分区镜像解析其源区间。无基座时会静默读取零填充字节并产出损坏数据，
                // 因此只要存在此类操作就必须提供基座。
                var needsBase = partition.Operations.Any(op => IsSourceDependent(op.Type));
                string? baseImagePath = null;
                if (sourceImages != null && sourceImages.TryGetValue(partition.PartitionName, out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    baseImagePath = path;
                }

                if (needsBase && baseImagePath == null)
                {
                    throw new PartitionExtractionException(
                        $"Partition '{partition.PartitionName}' contains source-dependent operations but no base image was provided. " +
                        "Provide a source payload or base image via sourcePayloadPath/sourceImages. " +
                        $"分区 '{partition.PartitionName}' 包含源依赖操作但未提供基座镜像；请通过 sourcePayloadPath 或 sourceImages 提供基座。",
                        partition.PartitionName);
                }

                if (baseImagePath != null)
                {
                    if (!File.Exists(baseImagePath))
                    {
                        throw new PartitionExtractionException(
                            $"Base image file '{baseImagePath}' for partition '{partition.PartitionName}' does not exist. " +
                            $"分区 '{partition.PartitionName}' 的基座镜像文件 '{baseImagePath}' 不存在。",
                            partition.PartitionName);
                    }

                    // Stream the base (old) partition image into the output file so source extents
                    // resolve to base data. The file was already sized to the destination; a shorter
                    // base leaves the tail zero-filled while a longer one extends the file so source
                    // reads can reach beyond the destination size during processing.
                    // 将基座（旧）分区镜像流式写入输出文件，使源区间解析到基座数据。文件此前已按
                    // 目标大小设定长度：较短的基座在尾部留零，较长的基座会扩展文件，以便处理期间
                    // 源读取可到达目标大小之外的区域。
                    using var baseStream = new FileStream(baseImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    baseStream.CopyTo(outFile);
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
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4),
                        CancellationToken = cancellationToken
                    },
                    op => OperationProcessor.ProcessOperation(op, outFile, stream, baseOffset, blockSize, writeLock, streamLock, cancellationToken));

                foreach (var op in sequentialOps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                        case InstallOperation.Types.Type.Zucchini:
                            {
                                var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                                OperationProcessor.ProcessZucchini(op, patchData, outFile, blockSize);
                            }
                            break;
                        case InstallOperation.Types.Type.Lz4diffBsdiff:
                            {
                                var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                                OperationProcessor.ProcessLz4diffBsdiff(op, patchData, outFile, blockSize);
                            }
                            break;
                        case InstallOperation.Types.Type.Lz4diffPuffdiff:
                            {
                                var patchData = ReadOperationData(op, stream, baseOffset, streamLock);
                                OperationProcessor.ProcessLz4diffPuffdiff(op, patchData, outFile, blockSize);
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
                        default:
                            break;
                    }
                }

                if (outFile.Length > totalSize)
                {
                    outFile.SetLength(totalSize);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullOutputPath, true);
        }
        catch (OperationCanceledException)
        {
            throw;
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
        finally
        {
            File.Delete(tempPath);
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
    /// <param name="sourceImages">Optional per-partition base image file path mapping. See
    /// <see cref="ExtractPartition(PartitionUpdate, string, Stream, ulong, uint, IReadOnlyDictionary{string, string}?)"/>.
    /// <para>可选的每分区基座镜像文件路径映射。参见
    /// <see cref="ExtractPartition(PartitionUpdate, string, Stream, ulong, uint, IReadOnlyDictionary{string, string}?)"/>。</para>
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
    public Task ExtractPartitionAsync(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null)
        => Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize, cancellationToken, sourceImages), cancellationToken);

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
    /// <param name="sourceImages">Optional per-partition base image file path mapping used as the base
    /// (old) partition image for source-dependent operations. See
    /// <see cref="ExtractPartition(PartitionUpdate, string, Stream, ulong, uint, IReadOnlyDictionary{string, string}?)"/>.
    /// <para>可选的每分区基座镜像文件路径映射，作为源依赖操作的基座（旧）分区镜像。参见
    /// <see cref="ExtractPartition(PartitionUpdate, string, Stream, ulong, uint, IReadOnlyDictionary{string, string}?)"/>。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when any partition extraction fails.
    /// <para>当任何分区提取失败时抛出。</para>
    /// </exception>
    public void ExtractPartitions(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, IReadOnlyDictionary<string, string>? sourceImages = null)
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

            var outFilename = GetSafeOutputPath(outputDir, partition.PartitionName);
            ExtractPartition(partition, outFilename, stream, baseOffset, manifest.BlockSize, CancellationToken.None, sourceImages);
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
    /// <param name="sourceImages">Optional per-partition base image file path mapping used as the base
    /// (old) partition image for source-dependent operations. See
    /// <see cref="ExtractPartition(PartitionUpdate, string, Stream, ulong, uint, IReadOnlyDictionary{string, string}?)"/>.
    /// <para>可选的每分区基座镜像文件路径映射，作为源依赖操作的基座（旧）分区镜像。参见
    /// <see cref="ExtractPartition(PartitionUpdate, string, Stream, ulong, uint, IReadOnlyDictionary{string, string}?)"/>。</para>
    /// </param>
    /// <returns>A task that represents the asynchronous extraction operation.
    /// <para>表示异步提取操作的任务。</para>
    /// </returns>
    /// <exception cref="PartitionExtractionException">Thrown when any partition extraction fails.
    /// <para>当任何分区提取失败时抛出。</para>
    /// </exception>
    public async Task ExtractPartitionsAsync(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null)
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

            var outFilename = GetSafeOutputPath(outputDir, partition.PartitionName);
            await ExtractPartitionAsync(partition, outFilename, stream, baseOffset, manifest.BlockSize, cancellationToken, sourceImages).ConfigureAwait(false);
        }
    }

    internal static bool IsSourceDependent(InstallOperation.Types.Type type) => type is
        InstallOperation.Types.Type.SourceCopy or
        InstallOperation.Types.Type.SourceBsdiff or
        InstallOperation.Types.Type.BrotliBsdiff or
        InstallOperation.Types.Type.Puffdiff or
        InstallOperation.Types.Type.Bsdiff or
        InstallOperation.Types.Type.Lz4diffBsdiff or
        InstallOperation.Types.Type.Lz4diffPuffdiff or
        InstallOperation.Types.Type.Zucchini;

    private static string GetSafeOutputPath(string outputDir, string name)
    {
        if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\') ||
            name.Contains("..", StringComparison.Ordinal))
        {
            throw new PartitionExtractionException($"Invalid partition output name: {name}", name);
        }

        var root = Path.GetFullPath(outputDir);
        var candidate = Path.GetFullPath(Path.Combine(root, name + ".img"));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new PartitionExtractionException($"Partition output escapes output directory: {name}", name);
        }
        return candidate;
    }

    private static byte[] ReadOperationData(InstallOperation op, Stream stream, ulong baseOffset, object streamLock)
    {
        if (!stream.CanSeek)
        {
            throw new PartitionExtractionException("Payload stream must be seekable", string.Empty);
        }

        int dataLength;
        long dataPos;
        try
        {
            dataLength = checked((int)op.DataLength);
            dataPos = checked((long)checked(baseOffset + op.DataOffset));
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException("Payload operation data offset or length exceeds the supported range", string.Empty, ex);
        }
        if (dataPos > stream.Length || dataLength > stream.Length - dataPos)
        {
            throw new PartitionExtractionException("Payload operation data range exceeds the stream length", string.Empty);
        }

        var data = new byte[dataLength];
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