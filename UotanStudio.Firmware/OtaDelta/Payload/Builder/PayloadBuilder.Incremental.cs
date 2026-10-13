using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    /// <summary>
    /// Adds an incremental partition built from a source base image and its target image: unchanged
    /// blocks are emitted as SOURCE_COPY operations (no payload data, hashed against the source) and
    /// changed blocks as REPLACE operations.
    /// <para>由源基座镜像和目标镜像构建增量分区。未变更块输出为 SOURCE_COPY 操作（不占用载荷数据，
    /// 哈希基于源镜像），变更块输出为 REPLACE 操作。</para>
    /// </summary>
    /// <remarks>
    /// Requires block-aligned source and target; their lengths may differ. SOURCE_COPY needs minor version &#8805; 2,
    /// which is raised automatically. A full REPLACE payload remains the fallback when the incremental
    /// encoding would not shrink the transfer; callers decide whether to prefer this path.
    /// <para>要求源与目标块对齐，允许长度不同；SOURCE_COPY 需次版本号 &#8805; 2，此处自动提升。
    /// 当增量编码无法缩小传输时应回退到完整 REPLACE 载荷，由调用方决定是否采用本路径。</para>
    /// </remarks>
    /// <param name="partitionName">
    /// Partition written by this update; duplicate names across the builder are rejected at build time.
    /// <para>本次更新写入的分区名；构建时拒绝构建器内重复的分区名。</para>
    /// </param>
    /// <param name="targetImagePath">
    /// Desired image file, which must exist and be block-aligned; it is opened by the build, not by this call.
    /// <para>期望的镜像文件，必须存在且块对齐；由构建过程打开，而非本调用。</para>
    /// </param>
    /// <param name="sourceImagePath">
    /// Base image file the device is assumed to hold; it must exist and be block-aligned.
    /// <para>设备被认为持有的基座镜像文件，必须存在且块对齐。</para>
    /// </param>
    /// <param name="compression">
    /// Codec applied to the changed blocks of a REPLACE operation; only <see cref="CompressionType.None"/>,
    /// <see cref="CompressionType.BZip2"/> and <see cref="CompressionType.Xz"/> are emitted.
    /// <para>应用于 REPLACE 操作变更块的编解码器；仅会生成 <see cref="CompressionType.None"/>、
    /// <see cref="CompressionType.BZip2"/> 与 <see cref="CompressionType.Xz"/>。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. The minor version is raised to <c>2</c> when it is lower.
    /// <para>本构建器，便于链式调用。次版本号低于 <c>2</c> 时会被提升到 <c>2</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitionName"/> is empty or whitespace, one of the paths is empty or whitespace, or
    /// <paramref name="compression"/> requests a codec this builder does not emit.
    /// <para><paramref name="partitionName"/> 为空或仅含空白、某个路径为空或仅含空白，或 <paramref name="compression"/>
    /// 请求了本构建器不生成的编解码器。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compression"/> is not a defined member. <para><paramref name="compression"/> 不是已定义的成员。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="targetImagePath"/> or <paramref name="sourceImagePath"/> does not exist.
    /// <para><paramref name="targetImagePath"/> 或 <paramref name="sourceImagePath"/> 不存在。</para>
    /// </exception>
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
    /// <param name="partitionName">
    /// Partition written by this update; duplicate names across the builder are rejected at build time.
    /// <para>本次更新写入的分区名；构建时拒绝构建器内重复的分区名。</para>
    /// </param>
    /// <param name="targetStream">
    /// Desired image, read from its current position; it must be seekable and readable when the build runs.
    /// <para>期望的镜像，从其当前位置开始读取；构建运行时必须可定位且可读。</para>
    /// </param>
    /// <param name="sourceStream">
    /// Base image, read from its current position; its remaining length must be block-aligned.
    /// <para>基座镜像，从其当前位置开始读取；剩余可读长度必须块对齐，允许与目标不同。</para>
    /// </param>
    /// <param name="compression">
    /// Codec applied to the changed blocks of a REPLACE operation; only <see cref="CompressionType.None"/>,
    /// <see cref="CompressionType.BZip2"/> and <see cref="CompressionType.Xz"/> are emitted.
    /// <para>应用于 REPLACE 操作变更块的编解码器；仅会生成 <see cref="CompressionType.None"/>、
    /// <see cref="CompressionType.BZip2"/> 与 <see cref="CompressionType.Xz"/>。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. The minor version is raised to <c>2</c> when it is lower.
    /// <para>本构建器，便于链式调用。次版本号低于 <c>2</c> 时会被提升到 <c>2</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitionName"/> is empty or whitespace, or <paramref name="compression"/> requests a codec
    /// this builder does not emit.
    /// <para><paramref name="partitionName"/> 为空或仅含空白，或 <paramref name="compression"/> 请求了本构建器不生成的
    /// 编解码器。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compression"/> is not a defined member. <para><paramref name="compression"/> 不是已定义的成员。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/>, <paramref name="targetStream"/> or <paramref name="sourceStream"/> is
    /// <see langword="null"/>.
    /// <para><paramref name="partitionName"/>、<paramref name="targetStream"/> 或 <paramref name="sourceStream"/>
    /// 为 <see langword="null"/>。</para>
    /// </exception>
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
        if (targetLength < 0 || sourceLength < 0 || targetLength % BlockSize != 0 || sourceLength % BlockSize != 0)
        {
            throw new PayloadGenerationException("Partition size must be block-aligned.");
        }

        var blockBytes = checked((int)BlockSize);
        var targetBuffer = new byte[chunkSize];
        var sourceBuffer = new byte[chunkSize];
        using var newHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var oldHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sourceIndex = new IncrementalSourceIndex(source, blockBytes);
        long indexedBlock = 0;
        var sourceRemaining = sourceLength;
        while (sourceRemaining > 0)
        {
            var count = (int)Math.Min(chunkSize, sourceRemaining);
            ReadIncrementalChunk(source, sourceBuffer.AsSpan(0, count), cancellationToken);
            oldHash.AppendData(sourceBuffer, 0, count);
            for (var offset = 0; offset < count; offset += blockBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceIndex.Add(sourceBuffer.AsSpan(offset, blockBytes), indexedBlock++);
            }
            sourceRemaining -= count;
        }
        ulong block = 0;
        var remaining = targetLength;
        var matches = new long[chunkSize / blockBytes];
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min((long)chunkSize, remaining);
            ReadIncrementalChunk(target, targetBuffer.AsSpan(0, count), cancellationToken);
            newHash.AppendData(targetBuffer, 0, count);

            var blocks = count / blockBytes;
            for (var i = 0; i < blocks; i++)
            {
                var preferred = i > 0 && matches[i - 1] >= 0
                    ? matches[i - 1] + 1 : checked((long)block + i);
                matches[i] = sourceIndex.Find(targetBuffer.AsSpan(i * blockBytes, blockBytes), preferred, cancellationToken);
            }
            var index = 0;
            while (index < blocks)
            {
                var changed = matches[index] < 0;
                var start = index;
                index++;
                while (index < blocks
                       && (changed ? matches[index] < 0 : matches[index] == matches[index - 1] + 1))
                {
                    index++;
                }

                var runBlocks = index - start;
                var runBytes = runBlocks * blockBytes;
                var startBlock = block + (ulong)start;
                var deltaSource = matches[start] >= 0
                    ? matches[start]
                    : startBlock < (ulong)(sourceLength / blockBytes) ? (long)startBlock : -1;
                if (++operationCount > maximumOperations)
                    throw new PayloadGenerationException("Payload exceeds the supported 65536 operations.");
                update.Operations.Add(changed
                    ? partition.Imagediff
                        ? WriteIncrementalImagediff(data, targetBuffer, start * blockBytes, runBytes, partition,
                            startBlock, deltaSource, sourceIndex, sourceLength, sourceBuffer, cancellationToken)
                        : WriteIncrementalBestDelta(data, targetBuffer, start * blockBytes, runBytes, partition,
                            startBlock, deltaSource, sourceIndex, sourceLength, sourceBuffer, cancellationToken)
                    : WriteIncrementalSourceCopy(targetBuffer, start * blockBytes, runBytes, runBlocks, startBlock, (ulong)matches[start]));
            }

            block += (ulong)blocks;
            remaining -= count;
        }

        update.OldPartitionInfo = new PartitionInfo { Size = (ulong)sourceLength, Hash = ByteString.CopyFrom(oldHash.GetHashAndReset()) };
        update.NewPartitionInfo = new PartitionInfo { Size = (ulong)targetLength, Hash = ByteString.CopyFrom(newHash.GetHashAndReset()) };
    }

    private static void ReadIncrementalChunk(Stream input, Span<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer[..Math.Min(buffer.Length, 64 * 1024)]);
            if (read == 0)
            {
                throw new EndOfStreamException("Incremental partition ended before its declared length.");
            }

            buffer = buffer[read..];
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    private InstallOperation WriteIncrementalBestDelta(FileStream data, byte[] buffer, int offset, int count,
        PartitionBuildInfo partition, ulong startBlock, long sourceStart, IncrementalSourceIndex index, long sourceLength,
        byte[] oldBuffer, CancellationToken token)
    {
        var replacement = WriteIncrementalReplace(data, buffer, offset, count, partition.OperationType,
            partition.CompressionType, startBlock, token);
        if (sourceStart < 0)
        {
            return replacement;
        }
        var sourceOffset = checked(sourceStart * BlockSize);
        if (sourceOffset > sourceLength || count > sourceLength - sourceOffset)
        {
            return replacement;
        }
        index.Read(sourceStart, oldBuffer.AsSpan(0, count), token);
        using var old = new MemoryStream(oldBuffer, 0, count, writable: false);
        using var desired = new MemoryStream(buffer, offset, count, writable: false);
        using var patch = BinaryDeltaPatch.CreateTemporaryStream();
        BinaryDeltaPatch.Create(old, desired, patch, token);
        var candidate = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceBsdiff,
            DataOffset = replacement.DataOffset,
            DataLength = (ulong)patch.Length,
            DataSha256Hash = ByteString.CopyFrom(new byte[32]),
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(oldBuffer.AsSpan(0, count))),
            SrcLength = (ulong)count,
            DstLength = (ulong)count
        };
        candidate.SrcExtents.Add(new Extent { StartBlock = (ulong)sourceStart, NumBlocks = (ulong)count / BlockSize });
        candidate.DstExtents.Add(replacement.DstExtents[0].Clone());
        if (checked(patch.Length + candidate.CalculateSize()) >= checked((long)replacement.DataLength + replacement.CalculateSize()))
        {
            return replacement;
        }
        data.Position = checked((long)replacement.DataOffset);
        patch.Position = 0;
        using var hashing = new HashingWriteStream(data, leaveOpen: true);
        BinaryDeltaPatch.CopyExactly(patch, hashing, patch.Length, token);
        candidate.DataSha256Hash = ByteString.CopyFrom(hashing.GetHashAndReset());
        data.SetLength(data.Position);
        return candidate;
    }

    private static InstallOperation WriteIncrementalSourceCopy(byte[] sourceBuffer, int offset, int runBytes, int runBlocks, ulong startBlock, ulong sourceBlock)
    {
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = (ulong)runBytes,
            DstLength = (ulong)runBytes,
            SrcSha256Hash = ByteString.CopyFrom(SHA256.HashData(sourceBuffer.AsSpan(offset, runBytes)))
        };
        var extent = new Extent { StartBlock = sourceBlock, NumBlocks = (ulong)runBlocks };
        operation.SrcExtents.Add(extent);
        operation.DstExtents.Add(new Extent { StartBlock = startBlock, NumBlocks = (ulong)runBlocks });
        return operation;
    }
}