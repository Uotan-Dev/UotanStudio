using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.CrossCutting.Compress.Compressors;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;

public sealed partial class PayloadBuilder
{
    private int? _compressionWorkers;

    /// <summary>
    /// Limits concurrent operation compression to 1–8 workers; overrides FirmwareParallelism.Workers for this builder.
    /// Each worker holds at most 1 MiB input and 2 MiB + 64 KiB output, plus codec state.
    /// Choose one worker when multiple builds run concurrently or memory is constrained.
    /// </summary>
    public PayloadBuilder WithCompressionWorkers(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workers, 8);
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

            // No tasks or completed results escape the batch. Input buffers are reused only after
            // every worker has stopped and the ordered writer has consumed their output.
            if (count == 1)
                slots[0].Compress(partition.CompressionType, cancellationToken);
            else
            {
                try
                {
                    Parallel.For(0, count, parallelOptions,
                        i => slots[i].Compress(partition.CompressionType, cancellationToken));
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

        internal void Compress(CompressionType compression, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new MemoryStream(Input, 0, InputLength, writable: false);
            using var destination = new MemoryStream(Output, writable: true);
            destination.SetLength(0);
            using (var bounded = new CountingWriteStream(destination, InputLength * 2L + 65536,
                       leaveOpen: true, cancellationToken: cancellationToken))
            {
                if (compression == CompressionType.Xz)
                {
                    // This input is already bounded to one operation. Avoid the general streaming
                    // XZ encoder's disk spool for each 1 MiB chunk, without buffering a whole image.
                    var original = InputLength == Input.Length ? Input : Input.AsSpan(0, InputLength).ToArray();
                    bounded.Write(XzCompressor.Compress(original, dictionarySize: 1u << 20, maxDegreeOfParallelism: 1));
                }
                else
                {
                    CompressStream(source, bounded, compression);
                }
            }
            OutputLength = checked((int)destination.Length);
            Hash = SHA256.HashData(Output.AsSpan(0, OutputLength));
        }
    }
}