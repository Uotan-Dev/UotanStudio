using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Compression.Compressors;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private int? _compressionWorkers;

    /// <summary>
    /// Limits concurrent operation compression to 1 to <see cref="Core.FirmwareParallelism.MaximumWorkers"/> workers;
    /// overrides FirmwareParallelism.Workers for this builder.
    /// Each worker holds at most 1 MiB input and 2 MiB + 64 KiB output, plus codec state.
    /// Choose one worker when multiple builds run concurrently or memory is constrained.
    /// <para>将并发操作压缩限制在 1 到 <see cref="Core.FirmwareParallelism.MaximumWorkers"/> 个工作者之间；
    /// 对本构建器覆盖 FirmwareParallelism.Workers。每个工作者最多持有 1 MiB 输入与 2 MiB + 64 KiB 输出，另有编解码器状态。
    /// 当多个构建并发运行或内存受限时应选择单工作者。</para>
    /// </summary>
    /// <param name="workers">
    /// Number of compression slots used per partition batch; the setting is stored until the build reads it, and a
    /// <c>CompressionType.None</c> partition always compresses on one worker regardless of this value.
    /// <para>每个分区批次使用的压缩槽数量；该设置被保存直至构建时读取，无论取值如何，<c>CompressionType.None</c>
    /// 分区始终在单个工作者上压缩。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="workers"/> is below <c>1</c> or above <see cref="Core.FirmwareParallelism.MaximumWorkers"/>.
    /// <para><paramref name="workers"/> 小于 <c>1</c> 或大于 <see cref="Core.FirmwareParallelism.MaximumWorkers"/>。</para>
    /// </exception>
    public PayloadBuilder WithCompressionWorkers(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workers, Core.FirmwareParallelism.MaximumWorkers);
        _compressionWorkers = workers;
        return this;
    }

    private ulong WritePartitionOperations(Stream input, long? declaredLength, FileStream data,
        PartitionBuildInfo partition, PartitionUpdate update, IncrementalHash partitionHash,
        int chunkSize, ref int operationCount, int compressionWorkers, CancellationToken cancellationToken)
    {
        const int maximumOperations = 65536;
        var workers = partition.CompressionType == CompressionType.None ? 1 : compressionWorkers;
        if (declaredLength is { } size)
            workers = (int)Math.Min(workers, Math.Max(1, size / chunkSize + (size % chunkSize == 0 ? 0 : 1)));
        workers = Core.FirmwareParallelism.BudgetedWorkers(
            bytesPerWorker: checked(chunkSize * 3L + 65536),
            budgetBytes: Core.FirmwareParallelism.WorkerMemoryBudgetBytes,
            requestedWorkers: workers);
        var slots = Enumerable.Range(0, workers).Select(_ => new CompressionSlot(chunkSize)).ToArray();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken };
        ulong position = 0;
        while (true)
        {
            var count = 0;
            for (; count < slots.Length; count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = slots[count];
                if (declaredLength is { } length)
                {
                    var remaining = checked(length - (long)position);
                    if (remaining == 0) break;
                    slot.InputLength = (int)Math.Min(chunkSize, remaining);
                    input.ReadExactly(slot.Input.AsSpan(0, slot.InputLength));
                }
                else
                {
                    slot.InputLength = input.ReadAtLeast(slot.Input, chunkSize, throwOnEndOfStream: false);
                    if (slot.InputLength == 0) break;
                }
                if (slot.InputLength % BlockSize != 0)
                    throw new PayloadGenerationException("Partition size must be block-aligned.");
                if (++operationCount > maximumOperations)
                    throw new PayloadGenerationException("Payload exceeds the supported 65536 operations.");
                partitionHash.AppendData(slot.Input, 0, slot.InputLength);
                slot.StartBlock = position / BlockSize;
                position = checked(position + (ulong)slot.InputLength);
            }
            if (count == 0) break;

            if (count == 1)
                slots[0].Compress(partition.CompressionType, partition.ZstdDictionary, cancellationToken);
            else
            {
                try
                {
                    Parallel.For(0, count, parallelOptions,
                        i => slots[i].Compress(partition.CompressionType, partition.ZstdDictionary, cancellationToken));
                }
                catch (AggregateException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
            }
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = slots[i];
                var operation = new InstallOperation
                {
                    Type = partition.OperationType,
                    DataOffset = (ulong)data.Position,
                    DataLength = (ulong)slot.OutputLength,
                    DataSha256Hash = ByteString.CopyFrom(slot.Hash),
                    DstLength = (ulong)slot.InputLength
                };
                operation.DstExtents.Add(new Extent { StartBlock = slot.StartBlock, NumBlocks = (ulong)slot.InputLength / BlockSize });
                data.Write(slot.Output.AsSpan(0, slot.OutputLength));
                update.Operations.Add(operation);
            }
        }
        return position;
    }

    private sealed class CompressionSlot(int chunkSize)
    {
        internal readonly byte[] Input = new byte[chunkSize];
        internal readonly byte[] Output = new byte[checked(chunkSize * 2 + 65536)];
        internal int InputLength;
        internal int OutputLength;
        internal ulong StartBlock;
        internal byte[] Hash = [];

        /// <summary>
        /// Compresses the filled input range into <see cref="Output"/>, hashing the produced bytes on the
        /// way out so the operation digest needs no second pass over the buffer.
        /// <para>把已填充的输入区间压缩进 <see cref="Output"/>，并在写出链上同步计算输出摘要，
        /// 免去对缓冲的第二遍哈希。</para>
        /// </summary>
        /// <param name="compression">Codec selected by the partition. <para>分区选用的编解码器。</para></param>
        /// <param name="dictionary">
        /// External raw dictionary bytes for Zstd frames, or <see langword="null"/>; the same array is passed to
        /// every chunk of one partition build.
        /// <para>Zstd 帧使用的外部原始字典字节，可为 <see langword="null"/>；同一次分区构建的每个块都收到同一个数组。</para>
        /// </param>
        /// <param name="cancellationToken">Token checked by every forwarded write. <para>每次转发写入都会检查的取消令牌。</para></param>
        internal void Compress(CompressionType compression, byte[]? dictionary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                else if (compression == CompressionType.Zstd)
                {
                    CompressZstdChunk(source, bounded, dictionary is null ? Memory<byte>.Empty : dictionary.AsMemory(),
                        level: 3);
                }
                else
                {
                    CompressStream(source, bounded, compression);
                }
            }
            OutputLength = checked((int)destination.Length);
            Hash = hashing.GetHashAndReset();
        }
    }
}