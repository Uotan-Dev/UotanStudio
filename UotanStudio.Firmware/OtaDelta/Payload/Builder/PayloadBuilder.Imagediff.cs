using Google.Protobuf;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    /// <summary>
    /// Adds an incremental partition whose changed runs are emitted as SOURCE_IMAGEDIFF operations carrying
    /// IMGDIFF2 containers produced by <see cref="ImageDeltaBuilder"/>. Unchanged runs remain SOURCE_COPY.
    /// <para>添加增量分区：变更区段以 SOURCE_IMAGEDIFF 操作发射，数据块为 <see cref="ImageDeltaBuilder"/>
    /// 生成的 IMGDIFF2 容器；未变更区段仍为 SOURCE_COPY。</para>
    /// </summary>
    /// <remarks>
    /// SOURCE_IMAGEDIFF (value 15) is this repository's payload extension, mirroring the block-OTA
    /// imgdiff encoding; the parallel extractor path reports it as an
    /// unsupported operation. LZ4DIFF generation is deliberately not attempted: the container's inner streams
    /// are computed over LZ4-compressed partition representations that no local oracle produces, so its exact
    /// framing cannot be pinned from the applier alone.
    /// <para>SOURCE_IMAGEDIFF（值 15）是本仓库的载荷扩展，对应块 OTA 的 imgdiff 编码；
    /// 并行提取路径会将其报告为不支持的操作。LZ4DIFF 生成被刻意放弃：其内部流建立在 LZ4 压缩分区表示之上，
    /// 本地没有能生成该表示的 oracle，仅凭 applier 无法钉死其精确封装。</para>
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
    /// <param name="sourcePartitionName">
    /// Partition recorded in each operation's source-partition-name field; <see langword="null"/> records
    /// <paramref name="partitionName"/> itself. <para>写入每条操作 source-partition-name 字段的分区名；为
    /// <see langword="null"/> 时记录 <paramref name="partitionName"/> 本身。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. The minor version is raised to <c>2</c> when it is lower.
    /// <para>本构建器，便于链式调用。次版本号低于 <c>2</c> 时会被提升到 <c>2</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A name or path is empty or whitespace. <para>分区名或路径为空/仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/>, <paramref name="targetImagePath"/> or <paramref name="sourceImagePath"/>
    /// is <see langword="null"/>. <para><paramref name="partitionName"/>、<paramref name="targetImagePath"/> 或
    /// <paramref name="sourceImagePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="targetImagePath"/> or <paramref name="sourceImagePath"/> does not exist.
    /// <para><paramref name="targetImagePath"/> 或 <paramref name="sourceImagePath"/> 不存在。</para>
    /// </exception>
    public PayloadBuilder AddPartitionIncrementalImagediff(string partitionName, string targetImagePath, string sourceImagePath, string? sourcePartitionName = null)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(targetImagePath);
        ArgumentNullException.ThrowIfNull(sourceImagePath);
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        }

        if (string.IsNullOrWhiteSpace(targetImagePath))
        {
            throw new ArgumentException("Target image path cannot be empty or whitespace.", nameof(targetImagePath));
        }

        if (string.IsNullOrWhiteSpace(sourceImagePath))
        {
            throw new ArgumentException("Source image path cannot be empty or whitespace.", nameof(sourceImagePath));
        }

        if (!File.Exists(targetImagePath))
        {
            throw new FileNotFoundException($"Target image file not found: {targetImagePath}", nameof(targetImagePath));
        }

        if (!File.Exists(sourceImagePath))
        {
            throw new FileNotFoundException($"Source image file not found: {sourceImagePath}", nameof(sourceImagePath));
        }

        var info = NewIncrementalInfo(partitionName, CompressionType.None);
        info.Imagediff = true;
        info.SourcePartitionName = sourcePartitionName ?? partitionName;
        info.ImagePath = targetImagePath;
        info.SourcePath = sourceImagePath;
        _partitions.Add(info);
        return this;
    }

    /// <summary>
    /// Adds an incremental IMGDIFF2 partition from seekable target and source streams. The caller owns both
    /// streams; they are disposed with the builder. See the path overload for the SOURCE_IMAGEDIFF contract.
    /// <para>由可定位的目标流与源流添加增量 IMGDIFF2 分区。调用方拥有这两个流；随构建器一并释放。
    /// SOURCE_IMAGEDIFF 契约见路径重载。</para>
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
    /// <para>基座镜像，从其当前位置开始读取；剩余可读长度必须块对齐。</para>
    /// </param>
    /// <param name="sourcePartitionName">
    /// Partition recorded in each operation's source-partition-name field; <see langword="null"/> records
    /// <paramref name="partitionName"/> itself. <para>写入每条操作 source-partition-name 字段的分区名；为
    /// <see langword="null"/> 时记录 <paramref name="partitionName"/> 本身。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. The minor version is raised to <c>2</c> when it is lower.
    /// <para>本构建器，便于链式调用。次版本号低于 <c>2</c> 时会被提升到 <c>2</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitionName"/> is empty or whitespace. <para><paramref name="partitionName"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/>, <paramref name="targetStream"/> or <paramref name="sourceStream"/> is
    /// <see langword="null"/>. <para><paramref name="partitionName"/>、<paramref name="targetStream"/> 或
    /// <paramref name="sourceStream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public PayloadBuilder AddPartitionIncrementalImagediff(string partitionName, Stream targetStream, Stream sourceStream, string? sourcePartitionName = null)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(targetStream);
        ArgumentNullException.ThrowIfNull(sourceStream);
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        }

        var info = NewIncrementalInfo(partitionName, CompressionType.None);
        info.Imagediff = true;
        info.SourcePartitionName = sourcePartitionName ?? partitionName;
        info.ImageStream = targetStream;
        info.SourceStream = sourceStream;
        _partitions.Add(info);
        return this;
    }

    private InstallOperation WriteIncrementalImagediff(FileStream data, byte[] buffer, int offset, int count,
        PartitionBuildInfo partition, ulong startBlock, long sourceStart, IncrementalSourceIndex index, long sourceLength,
        byte[] oldBuffer, CancellationToken cancellationToken)
    {
        var blocks = (ulong)count / BlockSize;
        if (sourceStart >= 0)
        {
            var sourceOffset = checked(sourceStart * BlockSize);
            if (sourceOffset <= sourceLength && count <= sourceLength - sourceOffset)
            {
                index.Read(sourceStart, oldBuffer.AsSpan(0, count), cancellationToken);
                using var old = new MemoryStream(oldBuffer, 0, count, writable: false);
                using var desired = new MemoryStream(buffer, offset, count, writable: false);
                using var patch = BinaryDeltaPatch.CreateTemporaryStream();
                ImageDeltaBuilder.Build(old, desired, patch, checked((int)BlockSize), new ImgDiffBuilderOptions { SelfVerify = true }, cancellationToken);
                patch.Position = 0;
                var dataOffset = data.Position;
                using (var hashing = new HashingWriteStream(data, leaveOpen: true))
                {
                    BinaryDeltaPatch.CopyExactly(patch, hashing, patch.Length, cancellationToken);
                    var operation = new InstallOperation
                    {
                        Type = InstallOperation.Types.Type.SourceImagediff,
                        DataOffset = (ulong)dataOffset,
                        DataLength = (ulong)patch.Length,
                        DataSha256Hash = ByteString.CopyFrom(hashing.GetHashAndReset()),
                        SrcSha256Hash = ByteString.CopyFrom(Sha256.ComputeHash(oldBuffer.AsSpan(0, count))),
                        SrcLength = (ulong)count,
                        DstLength = (ulong)count,
                        SourcePartitionName = partition.SourcePartitionName ?? partition.PartitionName
                    };
                    operation.SrcExtents.Add(new Extent { StartBlock = (ulong)sourceStart, NumBlocks = blocks });
                    operation.DstExtents.Add(new Extent { StartBlock = startBlock, NumBlocks = blocks });
                    return operation;
                }
            }
        }

        return WriteIncrementalReplace(data, buffer, offset, count, InstallOperation.Types.Type.Replace,
            CompressionType.None, startBlock, cancellationToken);
    }
}