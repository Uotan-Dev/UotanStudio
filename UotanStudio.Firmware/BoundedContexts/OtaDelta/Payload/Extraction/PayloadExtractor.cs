using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Bsdiff;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.IO;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Operations;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Extraction;

internal class PayloadExtractor
{
    internal const long DefaultMaximumPartitionBytes = 64L * 1024 * 1024 * 1024;

    private readonly long maximumPartitionBytes;

    internal PayloadExtractor(long maximumPartitionBytes = DefaultMaximumPartitionBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPartitionBytes);
        this.maximumPartitionBytes = maximumPartitionBytes;
    }

    private static readonly HashSet<InstallOperation.Types.Type> ParallelizableTypes =
    [
        InstallOperation.Types.Type.Replace,
        InstallOperation.Types.Type.ReplaceBz,
        InstallOperation.Types.Type.ReplaceXz,
        InstallOperation.Types.Type.ReplaceZstd,
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
    /// handle is opened per partition so concurrent partitions do not share, and therefore do not
    /// serialize on, a single payload stream; otherwise the caller-supplied shared stream is used.
    /// <para>从载荷流中提取单个分区到输出文件。当提供 <paramref name="openPayloadStream"/> 时，
    /// 每个分区都会打开专属于自己的只读载荷句柄，使并发分区不共享（因而不会因共享而串行化）单一载荷流；
    /// 否则使用调用方传入的共享流。</para>
    /// </summary>
    /// <param name="partition">Manifest entry describing the partition's operations, block extents and hash metadata.
    /// <para>描述该分区的操作、块 extent 与哈希元数据的清单项。</para></param>
    /// <param name="outFilename">Destination image path; its directory is created on demand and the file is published only after the temporary output is complete.
    /// <para>目标镜像路径；按需创建其目录，并且仅在临时输出完成后发布该文件。</para></param>
    /// <param name="stream">Payload data stream holding the operation blobs, seekable when <paramref name="openPayloadStream"/> is absent;
    /// may be <see langword="null"/> when a factory is supplied. Ownership stays with the caller.
    /// <para>承载操作数据块的载荷流；在未提供 <paramref name="openPayloadStream"/> 时必须可寻址；提供工厂时可为 <see langword="null"/>。
    /// 所有权始终属于调用方。</para></param>
    /// <param name="baseOffset">Absolute byte offset in <paramref name="stream"/> where operation data begins.
    /// <para><paramref name="stream"/> 中操作数据起始的绝对字节偏移。</para></param>
    /// <param name="blockSize">Block size declared by the manifest, in bytes; it must be nonzero.
    /// <para>清单声明的块大小（字节），必须非零。</para></param>
    /// <param name="cancellationToken">Checked before the run, around every operation and while copying the base image; cancelling leaves only the temporary file, which is deleted.
    /// <para>在执行前、每个操作前后以及复制基座镜像时都会检查；取消时仅留下随后会被删除的临时文件。</para></param>
    /// <param name="sourceImages">Base image path per partition name, used by source-dependent operations; <see langword="null"/> or a missing entry makes such operations fail.
    /// <para>按分区名索引的基座镜像路径，供源依赖操作使用；为 <see langword="null"/> 或缺少对应项时此类操作会失败。</para></param>
    /// <param name="byteProgress">Optional per-partition byte progress sink reporting processed bytes against the validated partition total.
    /// <para>可选的分区级字节进度回调，按已校验的分区总量上报已处理字节。</para></param>
    /// <param name="openPayloadStream">
    /// Optional factory opening a partition-exclusive read-only payload stream (disposed before return).
    /// <para>可选的工厂，用于打开分区专属只读载荷流（返回前释放）。</para>
    /// </param>
    /// <param name="operationWorkers">Optional cap on concurrently processed operations within this partition; when absent, the shared worker count applies.
    /// <para>可选的本分区内并发操作数上限；未提供时采用共享的工作者数量。</para></param>
    private void ExtractPartition(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? sourceImages, IProgress<(long Processed, long Total)>? byteProgress = null, Func<Stream>? openPayloadStream = null, int? operationWorkers = null)
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
        var workers = operationWorkers ?? SharedKernel.FirmwareParallelism.Workers;
        if (blockSize == 0) throw new PartitionExtractionException("Payload block size must be nonzero.", partition.PartitionName);
        var totalSize = GetValidatedPartitionSize(partition, blockSize);
        var fullOutputPath = Path.GetFullPath(outFilename);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var tempPath = fullOutputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var outFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920))
            {
                if (totalSize > 0)
                {
                    outFile.SetLength(totalSize);
                }

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
                        BinaryDeltaPatch.CopyExactly(hash, Stream.Null, baseStream.Length - Math.Min(baseStream.Length, totalSize), cancellationToken);
                        hash.Complete();
                    }
                    else
                    {
                        outFile.Position = 0;
                        BinaryDeltaPatch.CopyExactly(baseStream, outFile, Math.Min(baseStream.Length, totalSize), cancellationToken);
                    }
                }

                object writeLock = new();
                var ownPayload = openPayloadStream is not null;
                object streamLock = ownPayload ? new object() : stream!;

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
                    using var payloadStream = openPayloadStream!();
                    Stream opStream = payloadStream;

                    Parallel.ForEach(
                        parallelOps.OrderBy(o => o.DataOffset),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = workers,
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
                    Parallel.ForEach(
                        parallelOps.OrderBy(o => o.DataOffset),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = workers,
                            CancellationToken = cancellationToken
                        },
                        op => OperationProcessor.ProcessOperation(op, outFile, stream!, baseOffset, blockSize, writeLock, streamLock, cancellationToken, progressTracker));

                    using var deltaPayload = new IndependentReadStream(stream!, streamLock);
                    foreach (var op in sequentialOps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        OperationProcessor.ProcessDeltaOperation(op, baseStream!, outFile, deltaPayload, baseOffset, blockSize, cancellationToken, progressTracker);
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

    private long GetValidatedPartitionSize(PartitionUpdate partition, uint blockSize)
    {
        long inferredSize = 0;
        try
        {
            foreach (var operation in partition.Operations)
            {
                ulong operationBytes = 0;
                foreach (var extent in operation.DstExtents)
                {
                    var extentBytes = checked(extent.NumBlocks * blockSize);
                    operationBytes = checked(operationBytes + extentBytes);
                    var endBlock = checked(extent.StartBlock + extent.NumBlocks);
                    var endOffset = checked(endBlock * blockSize);
                    inferredSize = Math.Max(inferredSize, checked((long)endOffset));
                }

                if (operation.DstLength != 0 && operation.DstLength != operationBytes)
                {
                    throw new PartitionExtractionException(
                        $"Operation destination length {operation.DstLength} does not match its extents ({operationBytes} bytes).",
                        partition.PartitionName);
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException(
                "Destination extent end offset exceeds the supported range.", partition.PartitionName, ex);
        }

        if (inferredSize > maximumPartitionBytes)
        {
            throw new PartitionExtractionException(
                $"Destination extents require {inferredSize} bytes, exceeding the configured per-partition limit of {maximumPartitionBytes} bytes.",
                partition.PartitionName);
        }

        if (partition.NewPartitionInfo is not { } targetInfo)
        {
            return inferredSize;
        }

        long declaredSize;
        try
        {
            declaredSize = checked((long)targetInfo.Size);
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException(
                "Declared partition size exceeds the supported range.", partition.PartitionName, ex);
        }

        if (declaredSize > maximumPartitionBytes)
        {
            throw new PartitionExtractionException(
                $"Declared partition size {declaredSize} exceeds the configured per-partition limit of {maximumPartitionBytes} bytes.",
                partition.PartitionName);
        }

        if (inferredSize > declaredSize)
        {
            throw new PartitionExtractionException("Destination extents exceed the declared partition size.", partition.PartitionName);
        }

        return declaredSize;
    }

    /// <summary>
    /// Asynchronously extracts a single partition from the payload stream to an output file.
    /// <para>异步从载荷流中提取单个分区到输出文件。</para>
    /// </summary>
    public Task ExtractPartitionAsync(PartitionUpdate partition, string outFilename, Stream stream, ulong baseOffset, uint blockSize, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? sourceImages = null, IProgress<(long Processed, long Total)>? byteProgress = null, Func<Stream>? openPayloadStream = null)
        => Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, blockSize, cancellationToken, sourceImages, byteProgress, openPayloadStream), cancellationToken);

    /// <summary>
    /// Extracts all matching partitions from the manifest to the output directory.
    /// Partitions are processed with bounded parallelism (<see cref="UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers"/>);
    /// when <paramref name="byteProgress"/> is supplied, byte-level progress is aggregated across all
    /// partitions via a thread-safe accumulator (reports throttle to one per 100 ms).
    /// <para>从清单中提取所有匹配的分区到输出目录。分区按有界并行度（<see cref="UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers"/>）
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

        long totalBytes = 0;
        if (byteProgress is not null)
            foreach (var partition in selected) totalBytes = checked(totalBytes + ProgressSize(partition, manifest.BlockSize));
        var crossProgress = byteProgress is null ? null : new CrossPartitionProgress(byteProgress, totalBytes);
        byteProgress?.Report((0, totalBytes));

        var workers = SharedKernel.FirmwareParallelism.Workers;
        var partitionWorkers = Math.Max(1, Math.Min(workers, selected.Length));
        var operationWorkers = Math.Max(1, workers / partitionWorkers);
        try
        {
            Parallel.ForEach(
                selected,
                new ParallelOptions { MaxDegreeOfParallelism = partitionWorkers },
                partition =>
                {
                    var outFilename = GetSafeOutputPath(outputDir, partition.PartitionName);
                    IProgress<(long Processed, long Total)>? sink = crossProgress is null ? null : new PartitionDeltaForwarder(crossProgress);
                    ExtractPartition(partition, outFilename, stream, baseOffset, manifest.BlockSize, CancellationToken.None, sourceImages, sink, openPayloadStream, operationWorkers);
                });
        }
        catch (AggregateException ex)
        {
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }

        byteProgress?.Report((totalBytes, totalBytes));
    }

    /// <summary>
    /// Asynchronously extracts all matching partitions from the manifest to the output directory.
    /// Partitions are processed with bounded parallelism (<see cref="UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers"/>);
    /// byte-level progress is aggregated across all partitions via a thread-safe accumulator.
    /// <para>异步从清单中提取所有匹配的分区到输出目录。分区按有界并行度（<see cref="UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers"/>）
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

        var crossProgress = byteProgress is null ? null : new CrossPartitionProgress(byteProgress, totalBytes);
        byteProgress?.Report((0, totalBytes));

        var workers = SharedKernel.FirmwareParallelism.Workers;
        var partitionWorkers = Math.Max(1, Math.Min(workers, selected.Length));
        var operationWorkers = Math.Max(1, workers / partitionWorkers);
        try
        {
            await Parallel.ForEachAsync(
                selected,
                new ParallelOptions { MaxDegreeOfParallelism = partitionWorkers, CancellationToken = cancellationToken },
                async (partition, token) =>
                {
                    var outFilename = GetSafeOutputPath(outputDir, partition.PartitionName);
                    IProgress<(long Processed, long Total)>? sink = crossProgress is null ? null : new PartitionDeltaForwarder(crossProgress);
                    await Task.Run(() => ExtractPartition(partition, outFilename, stream, baseOffset, manifest.BlockSize,
                        token, sourceImages, sink, openPayloadStream, operationWorkers), token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (AggregateException ex)
        {
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        crossProgress?.Complete();
    }

    internal static long ProgressSize(PartitionUpdate partition, uint blockSize) => partition.NewPartitionInfo is { } info
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