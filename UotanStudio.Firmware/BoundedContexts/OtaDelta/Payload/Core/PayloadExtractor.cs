using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;

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

    /// <summary>
    /// Extracts a single partition from the payload stream to an output file.
    /// When <paramref name="openPayloadStream"/> is provided, a partition-exclusive read-only payload
    /// handle is opened per partition so concurrent partitions no longer share (and serialize on) a
    /// single payload stream; otherwise the caller-supplied shared stream is used.
    /// <para>从载荷流中提取单个分区到输出文件。当提供 <paramref name="openPayloadStream"/> 时，
    /// 每个分区都会打开专属于自己的只读载荷句柄，使并发分区不再共享（并因共享而串行化）单一载荷流；
    /// 否则使用调用方传入的共享流。</para>
    /// </summary>
    /// <param name="openPayloadStream">
    /// Optional factory opening a partition-exclusive read-only payload stream (disposed before return).
    /// <para>可选的工厂，用于打开分区专属只读载荷流（返回前释放）。</para>
    /// </param>
    private void ExtractPartition(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? sourceImages, IProgress<(long Processed, long Total)>? byteProgress = null, Func<Stream>? openPayloadStream = null)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(outFilename);
        if (stream is null && openPayloadStream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
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

                using var baseStream = baseImagePath is null
                    ? null
                    : new FileStream(baseImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

                // 消除基座双读：校验与拷贝合并为单遍——在 VerifyingReadStream 之上直接 CopyExactly。
                // Eliminate the double read of the base image: verification and copy are merged into a
                // single pass — CopyExactly reads straight through the VerifyingReadStream.
                if (baseStream is not null)
                {
                    baseStream.Position = 0;
                    if (partition.OldPartitionInfo is { } sourceInfo)
                    {
                        if (checked((ulong)baseStream.Length) != sourceInfo.Size)
                            throw new PartitionExtractionException("Partition length does not match its manifest.", partition.PartitionName);
                        if (sourceInfo.Hash.Length > 0)
                        {
                            if (sourceInfo.Hash.Length != 32)
                                throw new PartitionExtractionException("Invalid partition SHA-256 length.", partition.PartitionName);
                        }

                        baseStream.Position = 0;
                        using var hash = new VerifyingReadStream(baseStream, sourceInfo.Hash.Length > 0 ? sourceInfo.Hash.ToArray() : null);
                        outFile.Position = 0;
                        BinaryDeltaPatch.CopyExactly(hash, outFile, Math.Min(baseStream.Length, totalSize), cancellationToken);
                        hash.Complete();
                    }
                    else
                    {
                        // OldPartitionInfo 缺失时仅拷贝、不校验（保持原语义）。
                        // Copy without verification when OldPartitionInfo is missing (original semantics).
                        outFile.Position = 0;
                        BinaryDeltaPatch.CopyExactly(baseStream, outFile, Math.Min(baseStream.Length, totalSize), cancellationToken);
                    }
                }

                object writeLock = new();
                // 分区专属载荷句柄：每个分区打开自己的只读 payload 流并配一个专属锁，
                // 跨分区互不阻塞；锁仍传给下游以保护本分区内部的并发读。
                // Partition-exclusive payload handle: each partition opens its own read-only payload
                // stream with a dedicated lock so partitions never block each other; the lock is still
                // passed downstream to guard reads within this partition.
                var ownPayload = openPayloadStream is not null;
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

                if (ownPayload)
                {
                    // 专属句柄路径：本分区的所有操作读自己的 payload 流，streamLock 仅约束本分区内部并发。
                    // Exclusive-handle path: all operations of this partition read their own payload stream;
                    // streamLock only serializes concurrency within this partition.
                    using var payloadStream = openPayloadStream!();
                    Stream opStream = payloadStream;

                    Parallel.ForEach(
                        parallelOps.OrderBy(o => o.DataOffset),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = SharedKernel.FirmwareParallelism.Workers,
                            CancellationToken = cancellationToken
                        },
                        op => OperationProcessor.ProcessOperation(op, outFile, opStream, baseOffset, blockSize, writeLock, streamLock, cancellationToken, progressTracker));

                    foreach (var op in sequentialOps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        OperationProcessor.ProcessDeltaOperation(op, baseStream!, outFile, opStream, baseOffset, blockSize, cancellationToken, progressTracker);
                    }
                }
                else
                {
                    // 共享流路径（保持原行为）：跨分区共用调用方提供的 payload 流。
                    // Shared-stream path (original behavior): partitions share the caller-provided payload stream.
                    Parallel.ForEach(
                        parallelOps.OrderBy(o => o.DataOffset),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = SharedKernel.FirmwareParallelism.Workers,
                            CancellationToken = cancellationToken
                        },
                        op => OperationProcessor.ProcessOperation(op, outFile, stream!, baseOffset, blockSize, writeLock, streamLock, cancellationToken, progressTracker));

                    foreach (var op in sequentialOps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        OperationProcessor.ProcessDeltaOperation(op, baseStream!, outFile, stream!, baseOffset, blockSize, cancellationToken, progressTracker);
                    }
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
    public Task ExtractPartitionAsync(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, Func<Stream>? openPayloadStream = null)
        => Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize, cancellationToken, sourceImages, byteProgress, openPayloadStream), cancellationToken);

    /// <summary>
    /// Extracts all matching partitions from the manifest to the output directory.
    /// Partitions are processed with bounded parallelism (<see cref="FirmwareParallelism.Workers"/>);
    /// when <paramref name="byteProgress"/> is supplied, byte-level progress is aggregated across all
    /// partitions via a thread-safe accumulator (reports throttle to one per 100 ms).
    /// <para>从清单中提取所有匹配的分区到输出目录。分区按有界并行度（<see cref="FirmwareParallelism.Workers"/>）
    /// 处理；提供 <paramref name="byteProgress"/> 时，字节级进度通过线程安全累计器跨分区聚合（每 100ms 至多上报一次）。</para>
    /// </summary>
    public void ExtractPartitions(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, Func<Stream>? openPayloadStream = null)
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

        // 并发分区共享同一个 sink：与异步路径一致，经增量转发器跨分区聚合，避免分区内绝对值交错。
        // Concurrent partitions share one sink: mirroring the async path, progress is aggregated across
        // partitions via delta forwarders so in-partition absolute values never interleave.
        long totalBytes = 0;
        if (byteProgress is not null)
            foreach (var partition in selected) totalBytes = checked(totalBytes + ProgressSize(partition, manifest.BlockSize));
        var crossProgress = byteProgress is null ? null : new CrossPartitionProgress(byteProgress, totalBytes);
        byteProgress?.Report((0, totalBytes));

        var workers = SharedKernel.FirmwareParallelism.Workers;
        try
        {
            Parallel.ForEach(
                selected,
                new ParallelOptions { MaxDegreeOfParallelism = workers },
                partition =>
                {
                    var outFilename = GetSafeOutputPath(outputDir, partition.PartitionName);
                    IProgress<(long Processed, long Total)>? sink = crossProgress is null ? null : new PartitionDeltaForwarder(crossProgress);
                    ExtractPartition(partition, outFilename, stream, baseOffset, manifest.BlockSize, CancellationToken.None, sourceImages, sink, openPayloadStream);
                });
        }
        catch (AggregateException ex)
        {
            // 保留原有异常契约：拆开包装，抛出首个真实异常。
            // Preserve the original exception contract: unwrap and rethrow the first inner exception.
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }

        byteProgress?.Report((totalBytes, totalBytes));
    }

    /// <summary>
    /// Asynchronously extracts all matching partitions from the manifest to the output directory.
    /// Partitions are processed with bounded parallelism (<see cref="FirmwareParallelism.Workers"/>);
    /// byte-level progress is aggregated across all partitions via a thread-safe accumulator.
    /// <para>异步从清单中提取所有匹配的分区到输出目录。分区按有界并行度（<see cref="FirmwareParallelism.Workers"/>）
    /// 处理；字节级进度通过线程安全累计器跨分区聚合。</para>
    /// </summary>
    public async Task ExtractPartitionsAsync(DeltaArchiveManifest manifest, Stream stream, ulong baseOffset, string[] extractFiles, string outputDir, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, Func<Stream>? openPayloadStream = null)
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

        // 跨分区分摊的进度累计器：各分区字节写入经 Interlocked 汇总后共享同一 100ms 节流窗口。
        // Cross-partition apportioned progress accumulator: per-partition byte writes are merged via
        // Interlocked and share a single 100ms throttle window.
        var crossProgress = byteProgress is null ? null : new CrossPartitionProgress(byteProgress, totalBytes);
        byteProgress?.Report((0, totalBytes));

        var workers = SharedKernel.FirmwareParallelism.Workers;
        try
        {
            await Parallel.ForEachAsync(
                selected,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
                async (partition, token) =>
                {
                    var outFilename = GetSafeOutputPath(outputDir, partition.PartitionName);
                    // 每个分区一个增量转发器：把分区内的绝对读数折算为增量后汇入共享累计器。
                    // One delta forwarder per partition: converts in-partition absolute readings into
                    // deltas before merging them into the shared accumulator.
                    IProgress<(long Processed, long Total)>? sink = crossProgress is null ? null : new PartitionDeltaForwarder(crossProgress);
                    await ExtractPartitionAsync(partition, outFilename, stream, baseOffset, manifest.BlockSize, token, sourceImages, sink, openPayloadStream).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (AggregateException ex)
        {
            // 保留原有异常契约：拆开包装，抛出首个真实异常。
            // Preserve the original exception contract: unwrap and rethrow the first inner exception.
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        crossProgress?.Complete();
    }

    private static long ProgressSize(PartitionUpdate partition, uint blockSize) => partition.NewPartitionInfo is { } info
        ? checked((long)info.Size)
        : partition.Operations.SelectMany(op => op.DstExtents)
            .Select(e => checked((long)checked(checked(e.StartBlock + e.NumBlocks) * blockSize))).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Thread-safe byte progress accumulator shared by all concurrently extracted partitions.
    /// Completed bytes are merged with <see cref="Interlocked"/> so no lock is taken on the hot path;
    /// reports are throttled to at most one per 100 ms and a final (total, total) flush is emitted on
    /// <see cref="Complete"/>.
    /// <para>由所有并发提取分区共享的线程安全字节进度累计器。已写字节通过 <see cref="Interlocked"/>
    /// 汇总，热路径无需加锁；上报节流为每 100ms 至多一次，<see cref="Complete"/> 时最终上报 (total, total)。</para>
    /// </summary>
    private sealed class CrossPartitionProgress(IProgress<(long Processed, long Total)> sink, long total)
    {
        private const int ReportIntervalMilliseconds = 100;

        private long completedBytes;
        private long lastReportTimestamp = Environment.TickCount64;
        private bool finalReported;

        /// <summary>
        /// Merges a delta of newly completed bytes and reports when the throttle window has elapsed.
        /// <para>合并一段新增完成字节，并在节流窗口到期时上报。</para>
        /// </summary>
        internal void Add(long bytes)
        {
            if (finalReported || bytes <= 0)
            {
                return;
            }

            var updated = Interlocked.Add(ref completedBytes, bytes);
            if (Environment.TickCount64 - Volatile.Read(ref lastReportTimestamp) < ReportIntervalMilliseconds)
            {
                return;
            }

            Volatile.Write(ref lastReportTimestamp, Environment.TickCount64);
            sink.Report((updated, total));
        }

        /// <summary>
        /// Forces a final report of (total, total).
        /// <para>强制最终上报 (total, total)。</para>
        /// </summary>
        public void Complete()
        {
            if (finalReported)
            {
                return;
            }

            finalReported = true;
            sink.Report((total, total));
        }
    }

    /// <summary>
    /// Per-partition adapter converting the absolute in-partition readings produced by
    /// <see cref="PartitionByteProgress"/> into deltas forwarded to the shared
    /// <see cref="CrossPartitionProgress"/> accumulator.
    /// <para>分区级适配器：把 <see cref="PartitionByteProgress"/> 产生的分区内绝对读数折算为增量，
    /// 转发给共享的 <see cref="CrossPartitionProgress"/> 累计器。</para>
    /// </summary>
    private sealed class PartitionDeltaForwarder(CrossPartitionProgress sink) : IProgress<(long Processed, long Total)>
    {
        private long lastReported;

        /// <summary>
        /// Forwards the delta between consecutive absolute readings to the shared accumulator.
        /// <para>将相邻绝对读数之间的增量转发给共享累计器。</para>
        /// </summary>
        public void Report((long Processed, long Total) sample)
        {
            // 传入的 Processed 为分区内单调递增的绝对值；折算为增量后转发。
            // The incoming Processed value is monotonic within the partition; forward only the delta.
            long delta;
            long current;
            do
            {
                current = Volatile.Read(ref lastReported);
                delta = sample.Processed - current;
                if (delta <= 0)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref lastReported, sample.Processed, current) != current);

            sink.Add(delta);
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