using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal class PayloadExtractor
{
    private static readonly HashSet<InstallOperation.Types.Type> ParallelizableTypes =
    [
        InstallOperation.Types.Type.Replace,
        InstallOperation.Types.Type.ReplaceBz,
        InstallOperation.Types.Type.ReplaceXz,
        InstallOperation.Types.Type.Zero,
        InstallOperation.Types.Type.Discard
    ];

    private static readonly HashSet<InstallOperation.Types.Type> SequentialTypes =
    [
        InstallOperation.Types.Type.Move,
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

    private void ExtractPartition(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? sourceImages, IProgress<(long Processed, long Total)>? byteProgress = null)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(outFilename);
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(outFilename))
        {
            throw new ArgumentException("Output filename cannot be empty or whitespace.", nameof(outFilename));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (blockSize == 0) throw new PartitionExtractionException("Payload block size must be nonzero.", partition.PartitionName);
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

                if (partition.NewPartitionInfo is { } targetInfo)
                {
                    var declaredSize = checked((long)targetInfo.Size);
                    if (totalSize > declaredSize)
                        throw new PartitionExtractionException("Destination extents exceed the declared partition size.", partition.PartitionName);
                    totalSize = declaredSize;
                }
                if (totalSize > 0)
                {
                    outFile.SetLength(totalSize);
                }


                // 有界节流字节进度：按目标总量累计已写字节，≥100ms 或阶段结束时上报一次。
                // Bounded throttled byte progress: accumulates written bytes against the target size,
                // reporting at most once per 100ms and once at each stage end.
                var progressTracker = byteProgress is null
                    ? null
                    : new PartitionByteProgress(byteProgress, totalSize);

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

                using var baseStream = baseImagePath is null ? null : new FileStream(baseImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (baseStream is not null && partition.OldPartitionInfo is { } sourceInfo)
                    VerifyPartition(baseStream, sourceInfo, partition.PartitionName, cancellationToken);
                if (baseStream is not null)
                {
                    baseStream.Position = 0;
                    outFile.Position = 0;
                    UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(baseStream, outFile, Math.Min(baseStream.Length, totalSize), cancellationToken);
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
                    else throw new PayloadUnsupportedOperationException($"Unsupported operation type: {op.Type}", op.Type.ToString());
                }

                Parallel.ForEach(
                    parallelOps.OrderBy(o => o.DataOffset),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = SharedKernel.FirmwareParallelism.Workers,
                        CancellationToken = cancellationToken
                    },
                    op => OperationProcessor.ProcessOperation(op, outFile, stream, baseOffset, blockSize, writeLock, streamLock, cancellationToken, progressTracker));

                foreach (var op in sequentialOps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OperationProcessor.ProcessDeltaOperation(op, baseStream!, outFile, stream, baseOffset, blockSize, cancellationToken, progressTracker);
                }

                progressTracker?.CompleteStage();

                if (partition.NewPartitionInfo is { } newInfo)
                    VerifyPartition(outFile, newInfo, partition.PartitionName, cancellationToken);
                if (outFile.Length > totalSize)
                {
                    outFile.SetLength(totalSize);
                }

                progressTracker?.Complete();
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
    public Task ExtractPartitionAsync(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null)
        => Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize, cancellationToken, sourceImages, byteProgress), cancellationToken);

    /// <summary>
    /// Extracts all matching partitions from the manifest to the output directory.
    /// <para>从清单中提取所有匹配的分区到输出目录。</para>
    /// </summary>
    public void ExtractPartitions(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null)
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
            ExtractPartition(partition, outFilename, stream, baseOffset, manifest.BlockSize, CancellationToken.None, sourceImages, byteProgress);
        }
    }

    /// <summary>
    /// Asynchronously extracts all matching partitions from the manifest to the output directory.
    /// <para>异步从清单中提取所有匹配的分区到输出目录。</para>
    /// </summary>
    public async Task ExtractPartitionsAsync(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null)
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

        var selected = manifest.Partitions.Where(p => !string.IsNullOrEmpty(p.PartitionName)
            && (extractSet is null || extractSet.Contains(p.PartitionName))).ToArray();
        long totalBytes = 0;
        if (byteProgress is not null)
            foreach (var partition in selected) totalBytes = checked(totalBytes + ProgressSize(partition, manifest.BlockSize));
        long completedBytes = 0;
        byteProgress?.Report((0, totalBytes));
        foreach (var partition in selected)
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
            var size = byteProgress is null ? 0 : ProgressSize(partition, manifest.BlockSize);
            var partitionProgress = byteProgress is null ? null : new AggregatePartitionProgress(byteProgress, completedBytes, size, totalBytes);
            await ExtractPartitionAsync(partition, outFilename, stream, baseOffset, manifest.BlockSize, cancellationToken, sourceImages, partitionProgress).ConfigureAwait(false);
            completedBytes = checked(completedBytes + size);
            byteProgress?.Report((completedBytes, totalBytes));
        }
    }

    private static long ProgressSize(PartitionUpdate partition, uint blockSize) => partition.NewPartitionInfo is { } info
        ? checked((long)info.Size)
        : partition.Operations.SelectMany(op => op.DstExtents)
            .Select(e => checked((long)checked(checked(e.StartBlock + e.NumBlocks) * blockSize))).DefaultIfEmpty(0).Max();

    private sealed class AggregatePartitionProgress(IProgress<(long Processed, long Total)> sink,
        long completed, long size, long total) : IProgress<(long Processed, long Total)>
    {
        private readonly object _gate = new();
        private long _reported;
        public void Report((long Processed, long Total) sample)
        {
            lock (_gate)
            {
                _reported = Math.Max(_reported, Math.Clamp(sample.Processed, 0, size));
                sink.Report((checked(completed + _reported), total));
            }
        }
    }

    internal static bool IsSourceDependent(InstallOperation.Types.Type type) => type is
        InstallOperation.Types.Type.Move or
        InstallOperation.Types.Type.SourceCopy or
        InstallOperation.Types.Type.SourceBsdiff or
        InstallOperation.Types.Type.BrotliBsdiff or
        InstallOperation.Types.Type.Puffdiff or
        InstallOperation.Types.Type.Bsdiff or
        InstallOperation.Types.Type.Lz4diffBsdiff or
        InstallOperation.Types.Type.Lz4diffPuffdiff or
        InstallOperation.Types.Type.Zucchini;

    private static void VerifyPartition(Stream image, PartitionInfo info, string name, CancellationToken ct)
    {
        if (checked((ulong)image.Length) != info.Size)
            throw new PartitionExtractionException("Partition length does not match its manifest.", name);
        if (info.Hash.Length > 0)
        {
            if (info.Hash.Length != 32) throw new PartitionExtractionException("Invalid partition SHA-256 length.", name);
            image.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = image.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), info.Hash.Span))
                throw new PartitionExtractionException("Partition SHA-256 does not match its manifest.", name);
        }
        image.Position = 0;
    }

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

}

/// <summary>
/// Throttled per-partition byte progress accumulator. Writes are counted from each operation's
/// declared destination extent length (no extra buffering); reports are throttled to at most one
/// per 100 ms, plus one forced report at each stage boundary and a final (total, total) flush.
/// <para>按分区节流的字节进度累计器。已写字节按各操作的声明目标 extent 长度累计（不引入额外缓冲）；
/// 上报节流为每 100ms 至多一次，阶段结束时强制上报一次，最终上报 (total, total)。</para>
/// </summary>
internal sealed class PartitionByteProgress(IProgress<(long Processed, long Total)> sink, long total)
{
    private const int ReportIntervalMilliseconds = 100;

    private long processed;
    private long lastReportTimestamp = Environment.TickCount64;
    private bool finalReported;

    /// <summary>Gets the estimated target size for this partition (0 when unavailable). <para>本分区的估计目标总量（不可用时为 0）。</para></summary>
    public long Total { get; } = total;

    /// <summary>
    /// Adds written bytes and reports when the throttle window has elapsed.
    /// <para>累计已写字节，并在节流窗口到期时上报。</para>
    /// </summary>
    public void Add(long bytes)
    {
        if (bytes <= 0 || finalReported)
        {
            return;
        }

        var updated = Interlocked.Add(ref processed, bytes);
        if (Environment.TickCount64 - Volatile.Read(ref lastReportTimestamp) < ReportIntervalMilliseconds)
        {
            return;
        }

        Volatile.Write(ref lastReportTimestamp, Environment.TickCount64);
        sink.Report((updated, Total));
    }

    /// <summary>
    /// Forces a report at a stage boundary (e.g. after the parallel or sequential pass).
    /// <para>在阶段边界（如并行或串行阶段结束后）强制上报一次。</para>
    /// </summary>
    public void CompleteStage() => ReportNow();

    /// <summary>
    /// Forces a final report of (total, total) or the accumulated actual value.
    /// <para>强制最终上报 (total, total) 或累计实际值。</para>
    /// </summary>
    public void Complete()
    {
        if (finalReported)
        {
            return;
        }

        finalReported = true;
        var value = Interlocked.Read(ref processed);
        var reportedTotal = Total > 0 ? Total : value;
        sink.Report((reportedTotal, reportedTotal));
    }

    private void ReportNow()
    {
        if (finalReported)
        {
            return;
        }

        Volatile.Write(ref lastReportTimestamp, Environment.TickCount64);
        sink.Report((Interlocked.Read(ref processed), Total));
    }
}