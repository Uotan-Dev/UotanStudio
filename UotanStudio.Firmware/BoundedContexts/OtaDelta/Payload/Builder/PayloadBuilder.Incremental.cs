using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    /// <summary>
    /// Adds an incremental partition built from a source base image and its target image. Unchanged
    /// blocks are emitted as SOURCE_COPY operations (no payload data, hashed against the source) and
    /// changed blocks as REPLACE operations, mirroring update_engine's block-level incremental format.
    /// <para>由源基座镜像和目标镜像构建增量分区。未变更块输出为 SOURCE_COPY 操作（不占用载荷数据，
    /// 哈希基于源镜像），变更块输出为 REPLACE 操作，与 update_engine 的块级增量格式保持一致。</para>
    /// </summary>
    /// <remarks>
    /// Requires block-aligned source and target of equal length; SOURCE_COPY needs minor version &#8805; 2,
    /// which is raised automatically. A full REPLACE payload remains the fallback when the incremental
    /// encoding would not shrink the transfer; callers decide whether to prefer this path.
    /// <para>要求源与目标块对齐且长度相等；SOURCE_COPY 需次版本号 &#8805; 2，此处自动提升。
    /// 当增量编码无法缩小传输时应回退到完整 REPLACE 载荷，由调用方决定是否采用本路径。</para>
    /// </remarks>
    public PayloadBuilder AddPartitionIncremental(string partitionName, string targetImagePath, string sourceImagePath, CompressionType compression = CompressionType.None)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(targetImagePath);
        ArgumentNullException.ThrowIfNull(sourceImagePath);
        if (string.IsNullOrWhiteSpace(partitionName))
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        if (string.IsNullOrWhiteSpace(targetImagePath))
            throw new ArgumentException("Target image path cannot be empty or whitespace.", nameof(targetImagePath));
        if (string.IsNullOrWhiteSpace(sourceImagePath))
            throw new ArgumentException("Source image path cannot be empty or whitespace.", nameof(sourceImagePath));
        if (!File.Exists(targetImagePath))
            throw new FileNotFoundException($"Target image file not found: {targetImagePath}", nameof(targetImagePath));
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException($"Source image file not found: {sourceImagePath}", nameof(sourceImagePath));

        var info = NewIncrementalInfo(partitionName, compression);
        info.ImagePath = targetImagePath;
        info.SourcePath = sourceImagePath;
        _partitions.Add(info);
        return this;
    }

    /// <summary>
    /// Adds an incremental partition from seekable target and source streams. The caller owns both streams;
    /// they are disposed with the builder. See the path overload for the incremental encoding contract.
    /// <para>由可定位的目标流与源流构建增量分区。调用方拥有这两个流；随构建器一并释放。
    /// 增量编码契约见路径重载。</para>
    /// </summary>
    public PayloadBuilder AddPartitionIncremental(string partitionName, Stream targetStream, Stream sourceStream, CompressionType compression = CompressionType.None)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(targetStream);
        ArgumentNullException.ThrowIfNull(sourceStream);
        if (string.IsNullOrWhiteSpace(partitionName))
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));

        var info = NewIncrementalInfo(partitionName, compression);
        info.ImageStream = targetStream;
        info.SourceStream = sourceStream;
        _partitions.Add(info);
        return this;
    }

    private PartitionBuildInfo NewIncrementalInfo(string partitionName, CompressionType compression)
    {
        var operation = compression switch
        {
            CompressionType.None => InstallOperation.Types.Type.Replace,
            CompressionType.BZip2 => InstallOperation.Types.Type.ReplaceBz,
            CompressionType.Xz => InstallOperation.Types.Type.ReplaceXz,
            CompressionType.Brotli => throw new ArgumentException("Android payload has no Brotli REPLACE operation.", nameof(compression)),
            CompressionType.Zstd => throw new ArgumentException("Zstd REPLACE operation 14 is supported for extension reading only; this builder does not emit it.", nameof(compression)),
            _ => throw new ArgumentOutOfRangeException(nameof(compression), $"Compression type {compression} is not supported.")
        };

        // SOURCE_COPY is only valid from minor version 2 onwards; bump the default silently.
        if (MinorVersion < 2) MinorVersion = 2;

        return new PartitionBuildInfo
        {
            PartitionName = partitionName,
            OperationType = operation,
            CompressionType = compression,
            Incremental = true
        };
    }

    private void WriteIncrementalPartition(PartitionBuildInfo partition, FileStream data, PartitionUpdate update,
        int chunkSize, ref int operationCount, CancellationToken cancellationToken)
    {
        const int maximumOperations = 65536;
        using var ownedTarget = partition.ImagePath is null ? null : File.OpenRead(partition.ImagePath);
        using var ownedSource = partition.SourcePath is null ? null : File.OpenRead(partition.SourcePath);
        var target = ownedTarget ?? partition.ImageStream ?? throw new PayloadGenerationException("Incremental partition has no target stream.");
        var source = ownedSource ?? partition.SourceStream ?? throw new PayloadGenerationException("Incremental partition has no source stream.");
        if (!target.CanSeek || !source.CanSeek)
            throw new PayloadGenerationException("Incremental partitions require seekable source and target streams.");
        if (!target.CanRead || !source.CanRead)
            throw new PayloadGenerationException("Incremental partition streams must be readable.");

        var targetLength = checked(target.Length - target.Position);
        var sourceLength = checked(source.Length - source.Position);
        if (targetLength < 0 || targetLength % BlockSize != 0 || sourceLength % BlockSize != 0)
            throw new PayloadGenerationException("Partition size must be block-aligned.");
        if (targetLength != sourceLength)
            throw new PayloadGenerationException("Incremental partitions require equal source and target sizes.");

        var blockBytes = checked((int)BlockSize);
        var targetBuffer = new byte[chunkSize];
        var sourceBuffer = new byte[chunkSize];
        using var newHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var oldHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ulong block = 0;
        var remaining = targetLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min((long)chunkSize, remaining);
            target.ReadExactly(targetBuffer.AsSpan(0, count));
            source.ReadExactly(sourceBuffer.AsSpan(0, count));
            newHash.AppendData(targetBuffer, 0, count);
            oldHash.AppendData(sourceBuffer, 0, count);

            var blocks = count / blockBytes;
            var index = 0;
            while (index < blocks)
            {
                var changed = !targetBuffer.AsSpan(index * blockBytes, blockBytes).SequenceEqual(sourceBuffer.AsSpan(index * blockBytes, blockBytes));
                var start = index;
                while (index < blocks
                       && !targetBuffer.AsSpan(index * blockBytes, blockBytes).SequenceEqual(sourceBuffer.AsSpan(index * blockBytes, blockBytes)) == changed)
                    index++;

                var runBlocks = index - start;
                var runBytes = runBlocks * blockBytes;
                var startBlock = block + (ulong)start;
                if (++operationCount > maximumOperations)
                    throw new PayloadGenerationException("Payload exceeds the supported 65536 operations.");
                update.Operations.Add(changed
                    ? WriteIncrementalReplace(data, targetBuffer, start * blockBytes, runBytes, partition.OperationType, partition.CompressionType, startBlock, cancellationToken)
                    : WriteIncrementalSourceCopy(sourceBuffer, start * blockBytes, runBytes, runBlocks, startBlock));
            }

            block += (ulong)blocks;
            remaining -= count;
        }

        update.OldPartitionInfo = new PartitionInfo { Size = (ulong)sourceLength, Hash = ByteString.CopyFrom(oldHash.GetHashAndReset()) };
        update.NewPartitionInfo = new PartitionInfo { Size = (ulong)targetLength, Hash = ByteString.CopyFrom(newHash.GetHashAndReset()) };
    }

    private InstallOperation WriteIncrementalReplace(FileStream data, byte[] buffer, int offset, int count,
        InstallOperation.Types.Type operationType, CompressionType compressionType, ulong startBlock, CancellationToken cancellationToken)
    {
        var dataOffset = data.Position;
        using var hashing = new HashingWriteStream(data, leaveOpen: true);
        using (var source = new MemoryStream(buffer, offset, count, writable: false))
        using (var bounded = new CountingWriteStream(hashing, count * 2L + 65536, leaveOpen: true, cancellationToken: cancellationToken))
            CompressStream(source, bounded, compressionType);
        var storedLength = hashing.BytesWritten;
        var digest = hashing.GetHashAndReset();
        var operation = new InstallOperation
        {
            Type = operationType,
            DataOffset = (ulong)dataOffset,
            DataLength = (ulong)storedLength,
            DataSha256Hash = ByteString.CopyFrom(digest),
            DstLength = (ulong)count
        };
        operation.DstExtents.Add(new Extent { StartBlock = startBlock, NumBlocks = (ulong)count / BlockSize });
        return operation;
    }

    private static InstallOperation WriteIncrementalSourceCopy(byte[] sourceBuffer, int offset, int runBytes, int runBlocks, ulong startBlock)
    {
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = (ulong)runBytes,
            DstLength = (ulong)runBytes,
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(sourceBuffer.AsSpan(offset, runBytes)))
        };
        var extent = new Extent { StartBlock = startBlock, NumBlocks = (ulong)runBlocks };
        operation.SrcExtents.Add(extent);
        operation.DstExtents.Add(new Extent { StartBlock = startBlock, NumBlocks = (ulong)runBlocks });
        return operation;
    }
}