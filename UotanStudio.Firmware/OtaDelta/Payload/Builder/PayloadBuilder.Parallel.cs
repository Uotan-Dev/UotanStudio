using System.Runtime.ExceptionServices;
using Google.Protobuf;
using UotanStudio.Compress.Codecs.Zstd;
using UotanStudio.Compress.Compressors;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private int? _compressionWorkers;

    /// <summary>Limits concurrent operation compression to the given worker count for this builder.
    /// <para>把本构建器的并发操作压缩限制为给定位数的工作者。</para></summary>
    /// <param name="workers">Number of compression slots used per partition batch; the setting is stored until the build reads it and applies to every codec, including <c>CompressionType.None</c>, whose slots store their chunks verbatim. The effective count is further capped so two resident batches fit the worker memory budget.
    /// <para>每个分区批次使用的压缩槽数量；该设置被保存直至构建时读取，并对所有编解码器生效，包含 <c>CompressionType.None</c>，其槽位原样存储各自的块。实际数量还会被进一步限制，使两批常驻缓冲落在 工作者内存预算内。</para></param>
    /// <returns>This builder, so calls can be chained.<para>本构建器，便于链式调用。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workers"/> is below <c>1</c> or above <see cref="Core.FirmwareParallelism.MaximumWorkers"/>.
    /// <para><paramref name="workers"/> 小于 <c>1</c> 或大于 <see cref="Core.FirmwareParallelism.MaximumWorkers"/>。</para></exception>
    public PayloadBuilder WithCompressionWorkers(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workers, Core.FirmwareParallelism.MaximumWorkers);
        _compressionWorkers = workers;
        return this;
    }

    /// <summary>
    /// Streams one partition into REPLACE operations through a two-stage software pipeline: while the previous
    /// batch is being compressed, the next batch is read and hashed, so the sequential read stage and the parallel
    /// compression stage overlap instead of alternating on one thread.
    /// <para>经两级软件流水线把单个分区写成 REPLACE 操作：上一批正在压缩时同时读取并哈希下一批，使串行的读取阶段与
    /// 并行的压缩阶段重叠，而不是在同一线程上交替进行。</para>
    /// </summary>
    /// <remarks>
    /// The emitted operations are byte-for-byte the same as a strictly sequential build, and they are appended in
    /// ascending block order: compression runs on every batch in parallel, but batches are still consumed in the
    /// order they were read. The caller keeps ownership of the input stream and the data blob. The worker budget
    /// counts both resident batches of slots, each <c>chunkSize * 3 + 64 KiB</c> bytes, because the reader fills the
    /// second batch while the consumer compresses the first.
    /// <para>写出的操作与严格串行构建逐字节一致，并按块升序追加：每批压缩并行进行，但各批仍按读取顺序被消费。
    /// 调用方保有输入流与数据块的属主。工作者预算按两批常驻槽位计算，每批 <c>chunkSize * 3 + 64 KiB</c> 字节，
    /// 因为读取方在消费方压缩第一批时正在填充第二批。</para>
    /// </remarks>
    private ulong WritePartitionOperations(Stream input, long? declaredLength, FileStream data,
        PartitionBuildInfo partition, PartitionUpdate update, DigestAccumulator partitionHash,
        int chunkSize, ref int operationCount, int compressionWorkers, CancellationToken cancellationToken)
    {
        var workers = compressionWorkers;
        if (declaredLength is { } size)
            workers = (int)Math.Min(workers, Math.Max(1, size / chunkSize + (size % chunkSize == 0 ? 0 : 1)));
        workers = Core.FirmwareParallelism.BudgetedWorkers(
            bytesPerWorker: checked(2 * (chunkSize * 3L + 65536)),
            budgetBytes: Core.FirmwareParallelism.WorkerMemoryBudgetBytes,
            requestedWorkers: workers);

        var pipeline = new PartitionPipeline(chunkSize, workers, BlockSize);
        try
        {
            pipeline.Prefetch(input, declaredLength, partitionHash, cancellationToken);
            var position = pipeline.Read(ref operationCount, cancellationToken);
            while (pipeline.HasBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pipeline.Prefetch(input, declaredLength, partitionHash, cancellationToken);
                pipeline.CompressBatch(partition.CompressionType, partition.ZstdDictionary, cancellationToken);
                pipeline.EmitBatch(data, partition.OperationType, update, BlockSize);
                position = pipeline.Read(ref operationCount, cancellationToken);
            }
            return position;
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two-stage software pipeline over the chunks of one partition: a dedicated reader fills the next batch while
    /// the consumer compresses and emits the current one, so the serial read/hash stage does not idle the
    /// compression workers and vice versa.
    /// <para>作用于单个分区各块的两级软件流水线：专用读取方在消费方压缩并写出当前批时填充下一批，
    /// 使串行的读取/哈希阶段不会让压缩工作者空转，反之亦然。</para>
    /// </summary>
    /// <remarks>
    /// Two batches of slots are allocated and swapped, so the pipeline costs one extra batch of buffers on top of
    /// the compression buffers the build already needed. Reading happens on a task the pipeline owns, and each
    /// filled chunk is folded into the input digest there, so the serial digest cost overlaps compression; the
    /// completed read task orders those appends before the consumer observes the batch. Only the consumer enforces
    /// the 65536-operation limit, because it is the side that knows how many operations earlier partitions emitted.
    /// Compression runs on the consumer thread's parallel loop, which keeps every emitted chunk in the exact order
    /// the previous barrier-based loop produced.
    /// <para>分配并轮换两批槽位，因此流水线在构建本就需要压缩缓冲之上多付出一批缓冲。读取发生在流水线自有的任务上，
    /// 每个填充的块都在该任务内并入输入摘要，使串行的摘要代价与压缩重叠；读取任务完成所建立的内存序保证这些追加
    /// 在消费方观察该批之前已经生效。只有消费方执行 65536 条操作上限检查，因为只有它知道前面各分区已输出多少条操作。
    /// 压缩运行在消费线程的并行循环中，从而让每个写出的块与原先基于屏障的循环顺序完全一致。</para>
    /// </remarks>
    private sealed class PartitionPipeline : IDisposable
    {
        private readonly int _chunkSize;
        private readonly uint _blockSize;
        private readonly CompressionSlot[][] _batches;
        private readonly ParallelOptions _parallelOptions;
        private Task<ReadOutcome>? _pending;
        private int _pendingBatch;
        private int _currentBatch;
        private bool _exhausted;
        private ulong _position;

        internal PartitionPipeline(int chunkSize, int workers, uint blockSize)
        {
            _chunkSize = chunkSize;
            _blockSize = blockSize;
            _batches =
            [
                Enumerable.Range(0, workers).Select(_ => new CompressionSlot(chunkSize)).ToArray(),
                Enumerable.Range(0, workers).Select(_ => new CompressionSlot(chunkSize)).ToArray()
            ];
            _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = workers };
        }

        /// <summary>Whether the batch the consumer currently owns holds at least one filled slot.
        /// <para>消费方当前持有的批次是否至少包含一个已填充的槽位。</para></summary>
        internal bool HasBatch => _count > 0;

        /// <summary>The number of filled slots in the batch the consumer currently owns.
        /// <para>消费方当前持有的批次中已填充槽位的数量。</para></summary>
        private int _count;

        /// <summary>
        /// Retires the in-flight read and takes ownership of the batch it filled, enforcing the operation limit on
        /// the consumer because only the consumer knows how many operations earlier partitions emitted.
        /// <para>结束在途读取并接管它填充的批次，同时在消费方执行操作上限检查，因为只有消费方知道前面各分区已输出多少条操作。</para>
        /// </summary>
        internal ulong Read(ref int operationCount, CancellationToken cancellationToken)
        {
            var completed = RetirePending(cancellationToken);
            _currentBatch = _pendingBatch;
            for (var i = 0; i < completed.Count; i++)
            {
                if (++operationCount > 65536)
                    throw new PayloadGenerationException("Payload exceeds the supported 65536 operations.");
            }

            _count = completed.Count;
            _position = completed.Position;
            _exhausted = completed.Exhausted;
            return _position;
        }

        /// <summary>
        /// Starts reading the next batch into the buffer pair the consumer has just vacated, so the read overlaps the
        /// compression of the batch handed over by <see cref="Read"/>.
        /// <para>把下一批读入消费方刚刚腾空的缓冲对，使读取与 <see cref="Read"/> 交付那一批的压缩重叠。</para>
        /// </summary>
        internal void Prefetch(Stream input, long? declaredLength, DigestAccumulator partitionHash,
            CancellationToken cancellationToken)
        {
            if (_exhausted)
            {
                return;
            }

            _pendingBatch = _currentBatch ^ 1;
            var target = _batches[_pendingBatch];
            var start = _position;
            _pending = Task.Run(
                () => ReadBatch(input, declaredLength, target, start, partitionHash, cancellationToken),
                cancellationToken);
        }

        /// <summary>The result of one batch read: the position reached and whether the stream ended inside it.
        /// <para>单批读取的结果：到达的位置以及流是否在该批内结束。</para></summary>
        private readonly record struct ReadOutcome(ulong Position, int Count, bool Exhausted);

        /// <summary>
        /// Waits for the in-flight read, or reports the position reached so far with no slots when none is in flight,
        /// so the caller's partition size survives every drained batch.
        /// <para>等待在途读取；没有在途读取时报告已到达的位置且不带任何槽位，使调用方的分区大小在每个已排空的批次之后仍然正确。</para>
        /// </summary>
        private ReadOutcome RetirePending(CancellationToken cancellationToken)
        {
            if (_pending is null)
            {
                return new ReadOutcome(_position, 0, true);
            }

            var pending = _pending;
            _pending = null;
            try
            {
                return pending.GetAwaiter().GetResult();
            }
            catch (AggregateException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
                throw;
            }
        }

        /// <summary>
        /// Reads one batch and folds every filled chunk into the input digest on this task, so the serial digest
        /// cost overlaps the consumer's compression instead of running between compression batches.
        /// <para>读取一批并把每个填充的块在本任务内并入输入摘要，使串行的摘要代价与消费方的压缩重叠，
        /// 而不是在压缩批次之间执行。</para>
        /// </summary>
        private ReadOutcome ReadBatch(Stream input, long? declaredLength, CompressionSlot[] slots, ulong start,
            DigestAccumulator partitionHash, CancellationToken cancellationToken)
        {
            var count = 0;
            var position = start;
            for (; count < slots.Length; count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = slots[count];
                if (declaredLength is { } length)
                {
                    var remaining = checked(length - (long)position);
                    if (remaining == 0) break;
                    slot.InputLength = (int)Math.Min(_chunkSize, remaining);
                    input.ReadExactly(slot.Input.AsSpan(0, slot.InputLength));
                }
                else
                {
                    slot.InputLength = input.ReadAtLeast(slot.Input, _chunkSize, throwOnEndOfStream: false);
                    if (slot.InputLength == 0) break;
                }
                if (slot.InputLength % _blockSize != 0)
                    throw new PayloadGenerationException("Partition size must be block-aligned.");
                partitionHash.Append(slot.Input.AsSpan(0, slot.InputLength));
                slot.StartBlock = position / _blockSize;
                position = checked(position + (ulong)slot.InputLength);
            }

            return new ReadOutcome(position, count, count < slots.Length);
        }

        /// <summary>Compresses every filled slot of the batch the consumer owns, in parallel when more than one is filled.
        /// <para>压缩消费方所持批次中每个已填充的槽位；填充数量多于一小时并行执行。</para></summary>
        internal void CompressBatch(CompressionType compression, byte[]? dictionary, CancellationToken cancellationToken)
        {
            var slots = _batches[_currentBatch];
            var count = _count;
            if (count == 1)
            {
                slots[0].Compress(compression, dictionary, cancellationToken);
                return;
            }

            _parallelOptions.CancellationToken = cancellationToken;
            try
            {
                Parallel.For(0, count, _parallelOptions, i => slots[i].Compress(compression, dictionary, cancellationToken));
            }
            catch (AggregateException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        /// <summary>
        /// Appends each slot's stored bytes to the data blob in block order and records one operation per slot.
        /// Stored bytes are the compressed output, or the input range itself when the codec keeps blocks verbatim.
        /// <para>按块顺序把每个槽位的已存储字节追加到数据块，并为每个槽位记录一条操作。已存储字节是压缩输出，
        /// 当编解码器原样保存块时则是输入区间本身。</para>
        /// </summary>
        internal void EmitBatch(FileStream data, InstallOperation.Types.Type operationType, PartitionUpdate update, uint blockSize)
        {
            var slots = _batches[_currentBatch];
            for (var i = 0; i < _count; i++)
            {
                var slot = slots[i];
                var operation = new InstallOperation
                {
                    Type = operationType,
                    DataOffset = (ulong)data.Position,
                    DataLength = (ulong)slot.OutputLength,
                    DataSha256Hash = ByteString.CopyFrom(slot.Hash),
                    DstLength = (ulong)slot.InputLength
                };
                operation.DstExtents.Add(new Extent { StartBlock = slot.StartBlock, NumBlocks = (ulong)slot.InputLength / blockSize });
                var stored = slot.StoresInput
                    ? slot.Input.AsSpan(0, slot.OutputLength)
                    : slot.Output.AsSpan(0, slot.OutputLength);
                data.Write(stored);
                update.Operations.Add(operation);
            }

            _count = 0;
        }

        /// <summary>Waits for any in-flight read and releases every slot of both batches.
        /// <para>等待任何在途读取结束，并释放两批中的所有槽位。</para></summary>
        public void Dispose()
        {
            try
            {
                RetirePending(CancellationToken.None);
            }
            catch (PayloadGenerationException)
            {
            }
            finally
            {
                foreach (var batch in _batches)
                {
                    foreach (var slot in batch)
                    {
                        slot.Dispose();
                    }
                }
            }
        }
    }

    private sealed class CompressionSlot(int chunkSize) : IDisposable
    {
        internal readonly byte[] Input = new byte[chunkSize];
        internal readonly byte[] Output = new byte[checked(chunkSize * 2 + 65536)];
        internal int InputLength;
        internal int OutputLength;
        internal ulong StartBlock;
        internal byte[] Hash = [];

        /// <summary>
        /// Whether <see cref="OutputLength"/> bytes of <see cref="Input"/> are the stored operation data, because the
        /// codec keeps blocks verbatim; <see cref="PartitionPipeline.EmitBatch"/> then writes the input range instead
        /// of the output.
        /// <para>表示 <see cref="Input"/> 的前 <see cref="OutputLength"/> 字节就是已存储的操作数据，因为编解码器原样
        /// 保存块；此时 <see cref="PartitionPipeline.EmitBatch"/> 写出输入区间而不是输出区间。</para>
        /// </summary>
        internal bool StoresInput;
        private readonly DigestAccumulator _frameHash = Sha256.CreateAccumulator();
        private ZstdEncoder? _zstd;
        private byte[]? _zstdDictionary;

        /// <summary>
        /// Zstandard level shared by every chunk of one partition; <c>dfast</c> keeps the row match finder disabled,
        /// which is what makes a reused context emit the same bytes as a fresh one.
        /// <para>单个分区所有块共用的 Zstandard 级别；<c>dfast</c> 不启用行匹配器，这正是复用上下文与新建上下文
        /// 字节一致的前提。</para>
        /// </summary>
        private const int ZstdLevel = 3;

        /// <summary>
        /// Compresses the filled input range into <see cref="Output"/>, hashing the produced bytes on the
        /// way out so the operation digest needs no second pass over the buffer.
        /// Zstandard frames are produced straight into <see cref="Output"/> with the slot's own context and hashed
        /// from there; every other compressed codec keeps the hashing and counting stream chain.
        /// <see cref="CompressionType.None"/> stores the input bytes themselves: the slot records
        /// <see cref="StoresInput"/> and digests the input range in place, so neither a copy nor a duplicate output
        /// pass is paid.
        /// <para>把已填充的输入区间压缩进 <see cref="Output"/>，并在写出链上同步计算输出摘要，
        /// 免去对缓冲的第二遍哈希。Zstd 帧由本槽位的上下文直接写入 <see cref="Output"/> 并就地哈希；
        /// 其他压缩编解码器保持哈希与计数流链路。<see cref="CompressionType.None"/> 直接存储输入字节本身：
        /// 槽位记录 <see cref="StoresInput"/> 并就地计算输入区间的摘要，因此既不需要复制，也不需要重复的输出哈希。</para>
        /// </summary>
        /// <param name="compression">Codec selected by the partition. <para>分区选用的编解码器。</para></param>
        /// <param name="dictionary">
        /// External raw dictionary bytes for Zstd frames, or <see langword="null"/>; the same array is passed to
        /// every chunk of one partition build, which is what lets the slot keep one loaded context for the whole
        /// partition.
        /// <para>Zstd 帧使用的外部原始字典字节，可为 <see langword="null"/>；同一次分区构建的每个块都收到同一个数组，
        /// 这正是本槽位可以为整个分区保留一个已加载字典的上下文的原因。</para>
        /// </param>
        /// <param name="cancellationToken">Token checked by every forwarded write. <para>每次转发写入都会检查的取消令牌。</para></param>
        internal void Compress(CompressionType compression, byte[]? dictionary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compression == CompressionType.None)
            {
                OutputLength = InputLength;
                StoresInput = true;
                Hash = Sha256.ComputeHash(Input.AsSpan(0, InputLength));
                return;
            }

            StoresInput = false;
            if (compression == CompressionType.Zstd)
            {
                CompressZstd(dictionary);
                return;
            }
            using var source = new MemoryStream(Input, 0, InputLength, writable: false);
            using var destination = new MemoryStream(Output, writable: true);
            destination.SetLength(0);
            using var hashing = new HashingWriteStream(destination, leaveOpen: true);
            using (var bounded = new CountingWriteStream(hashing, InputLength * 2L + 65536,
                       leaveOpen: true, cancellationToken: cancellationToken))
            {
                if (compression == CompressionType.Xz)
                {
                    XzCompressor.Compress(source, bounded, dictionarySize: 1u << 20, maxDegreeOfParallelism: 1);
                }
                else
                {
                    CompressStream(source, bounded, compression);
                }
            }
            OutputLength = checked((int)destination.Length);
            Hash = hashing.GetHashAndReset();
        }

        /// <summary>
        /// Releases the codec context and the frame hash kept by this slot.
        /// <para>释放本槽位持有的编解码器上下文与帧哈希对象。</para>
        /// </summary>
        public void Dispose()
        {
            _zstd?.Dispose();
            _zstd = null;
            _frameHash.Dispose();
        }

        private void CompressZstd(byte[]? dictionary)
        {
            var compressor = EnsureZstdCompressor(dictionary);
            var written = CompressZstdFrame(compressor, Input.AsSpan(0, InputLength), Output);
            if (written > InputLength * 2L + 65536)
            {
                throw new IOException($"The maximum output byte count of {InputLength * 2L + 65536} was exceeded.");
            }

            OutputLength = written;
            _frameHash.Append(Output.AsSpan(0, written));
            Hash = _frameHash.GetHashAndReset();
        }

        private ZstdEncoder EnsureZstdCompressor(byte[]? dictionary)
        {
            if (_zstd is { } compressor && ReferenceEquals(_zstdDictionary, dictionary))
            {
                return compressor;
            }
            _zstd?.Dispose();
            _zstd = CreateZstdFrameContext(dictionary is null ? Memory<byte>.Empty : dictionary.AsMemory(), ZstdLevel);
            _zstdDictionary = dictionary;
            return _zstd;
        }
    }
}