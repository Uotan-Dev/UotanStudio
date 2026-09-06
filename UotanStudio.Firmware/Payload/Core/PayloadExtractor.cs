using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Core;

internal class PayloadExtractor
{
    private static readonly HashSet<InstallOperation.Types.Type> ParallelizableTypes =
    [
        InstallOperation.Types.Type.Replace,
        InstallOperation.Types.Type.ReplaceBz,
        InstallOperation.Types.Type.ReplaceXz,
        (InstallOperation.Types.Type)14,
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
    public Task ExtractPartitionAsync(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null)
        => Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize, cancellationToken, sourceImages), cancellationToken);

    /// <summary>
    /// Extracts all matching partitions from the manifest to the output directory.
    /// <para>从清单中提取所有匹配的分区到输出目录。</para>
    /// </summary>
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
