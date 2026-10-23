using System.Buffers;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using ErofsInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;
namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that plans byte-granular dense pclusters: it first lets the destination-filling encoder name the span its frame fits, falls back to binary-searching that span when the frame is refused, keeps only the accepted frame, stages it once, and falls back to plain clusters when compression gains nothing. Every LZ4 build the cluster-restarting planner cannot fill uses this plan, including the default one-block budget; a plan that needs a plain frame the emitted reader cannot address, or that saves no block at all, is handed back to the cluster-restarting planner.
/// <para>EROFS 构建器的组成部分，负责按字节粒度规划稠密 pcluster：先由填满目标的编码器给出它那一帧所装入的跨度，
/// 帧被拒绝时再回退为二分查找该跨度，只保留被接受的帧并把它写入暂存区一次，压缩无收益时退回明文簇。逐簇重启规划器
/// 填不满的每种 LZ4 构建（包括默认的单块预算）都使用该规划；需要读取方无法寻址的原样帧、或完全省不下块的规划则交回
/// 逐簇重启规划器。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>Fills bounded physical clusters with byte-granular input, as permitted by the on-disk
    /// cluster-offset field. Every accepted probe copies its frame over one pooled buffer, so the staging
    /// area receives exactly the frame the emitted compressed plan points at and the write pass copies it
    /// instead of compressing the same bytes again.
    /// <para>以磁盘簇偏移字段允许的方式以字节粒度填满有界物理簇：每次被接受的探测都把自己的编码帧
    /// 复制到同一块池化缓冲上，因此暂存区只收到已发出压缩规划实际指向的那一帧，写出阶段直接复制而
    /// 不会把同样的字节再压缩一次。</para>
    /// <para>The two bounds a pcluster has to satisfy are separate because a reader derives them from
    /// different index fields. The physical bound is the number of blocks the index can name: without the
    /// big-pcluster advise it is pinned to exactly one filesystem block, while with the advise the count is
    /// carried by the first non-head row, which only exists once the pcluster covers a second logical
    /// cluster. The logical bound is the span the head and non-head rows credit to the frame. Only a budget
    /// above one block states a logical cap as well, because the physical cap alone leaves the decoded span
    /// free: a one-block frame may credit many logical clusters, which is the shape a build that leaves
    /// <see cref="ErofsOptions.MaxPclusterSize"/> at the block size keeps for its byte-granular pclusters.</para>
    /// <para>这两个边界必须分开，因为读取方是从不同索引字段推导它们的。物理边界是索引能够表明的块数：
    /// 未声明 big pcluster 时它被钉死为一个文件系统块；声明时块数由首个非头簇行携带，而行只有当
    /// pcluster 覆盖第二个逻辑簇时才存在。逻辑边界是头簇与非头簇行记给该帧的跨度。只有超过一个块的预算才同时
    /// 表达逻辑上限，因为仅有物理上限时解码跨度不受约束：一个单块帧可以记到许多逻辑簇上，这正是把
    /// <see cref="ErofsOptions.MaxPclusterSize"/> 留在块大小的构建为其按字节 pcluster 保留的形态。</para>
    /// <para>A span that reaches end of file is accepted only when the trailing row the index writes for the
    /// residual terminal cluster can close its extent, so a frame is never credited less than a reader
    /// demands. A span that starts inside its cluster and reaches end of file stays plain, because that is the
    /// final-extent shape a pre-5.13 reader resolves from the row chain alone; a full terminal cluster may
    /// still be encoded as its own frame, and the layout turns that pcluster plain when the inode advertises
    /// big pclusters. A plain fallback is only taken when it is one block wide and, for a build that requests
    /// the interlaced layout, starts on a block boundary; otherwise the plan is returned as
    /// <see langword="null"/> so the cluster-restarting planner emits a layout the reader can address without
    /// deciding the rotation from a physical address the planner has not assigned yet.</para>
    /// <para>触及文件末尾的跨度只有在索引为剩余末尾簇写出的收尾行能够收尾其 extent 时才被接受，因此帧记给读取方的
    /// 字节数不会少于其要求。起点位于簇内且触及文件末尾的跨度保持原样，因为 pre-5.13 读取方正是只凭行链解析这种末尾
    /// extent 形态；完整的末尾簇仍可作为独立帧编码，而当 inode 声明 big pcluster 时布局会把它改写为原样。只有在宽度
    /// 恰为一块、且对请求交错布局的构建其起点落在块边界上时才采用原样回退；否则返回 <see langword="null"/>，让逐簇
    /// 重启规划器发出读取方无需依据尚未分配的物理地址决定旋转即可寻址的布局。</para></summary>
    private static ErofsPclusterPlan[]? TryPlanDenseCompressed(ErofsInode node, int blockSize, ErofsOptions options,
        ErofsStagingBatch batch, ErofsEncoderContext context)
    {
        var clusterSize = node.LogicalClusterSize;
        var fileSize = (long)node.Size;
        if (fileSize <= 0 || clusterSize <= 0)
        {
            return null;
        }

        var bigPcluster = DeclaredBigPcluster(options);
        var declaredPclusterSize = options.MaxPclusterSize > 0
            ? Math.Min(options.MaxPclusterSize, ErofsCompression.MaxPclusterSize)
            : blockSize;
        var frameLimit = bigPcluster ? declaredPclusterSize : blockSize;
        var inputLimit = frameLimit > blockSize ? declaredPclusterSize : ErofsCompression.MaxPclusterSize;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4
            && options.Lz4MaxDistance is > 0 and < ErofsCompression.Lz4DistanceMax)
        {
            inputLimit = Math.Min(inputLimit, options.Lz4MaxDistance);
        }

        if (inputLimit < clusterSize)
        {
            return null;
        }

        using var input = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        var rawCapacity = (int)Math.Min(fileSize, inputLimit);
        var encodedCapacity = ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, rawCapacity);
        var raw = ArrayPool<byte>.Shared.Rent(rawCapacity);
        var encoded = ArrayPool<byte>.Shared.Rent(encodedCapacity);
        var accepted = ArrayPool<byte>.Shared.Rent(Math.Max(1, frameLimit));
        try
        {
            var plans = new List<ErofsPclusterPlan>();
            var position = 0L;
            while (position < fileSize)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var available = (int)Math.Min(fileSize - position, rawCapacity);
                var buffered = 0;
                var bestLength = 0;
                var bestEncoded = 0;
                bool Fits(int length)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    if (length <= 0 || length > available)
                    {
                        return false;
                    }

                    if (length > buffered)
                    {
                        input.Position = position + buffered;
                        input.ReadExactly(raw, buffered, length - buffered);
                        buffered = length;
                    }

                    var size = ErofsPlannerCompression.Compress(options, context, raw, 0, length, encoded, encodedCapacity);
                    if (size <= 0 || size > frameLimit)
                    {
                        return false;
                    }

                    if (size > blockSize && position % clusterSize + length < 2 * clusterSize)
                    {
                        return false;
                    }

                    if (!AcceptsClosingIndexRow(position, length, size, clusterSize, fileSize, blockSize))
                    {
                        return false;
                    }

                    bestLength = length;
                    bestEncoded = size;
                    encoded.AsSpan(0, size).CopyTo(accepted);
                    return true;
                }

                // The destination-filling encoder names the widest input that fits frameLimit, which is the
                // span the probe search below can only approximate, so one such encode replaces the search
                // whenever its frame passes the same rules. The window grows only while the encoder exhausts
                // it, so a span far denser than one frame is still found without reading the whole input
                // limit up front.
                bool FillEncoded(int length, out int filled, out int encodedLength)
                {
                    filled = 0;
                    encodedLength = 0;
                    if (length <= 0 || length > available)
                    {
                        return false;
                    }

                    if (buffered < length)
                    {
                        input.Position = position + buffered;
                        input.ReadExactly(raw, buffered, length - buffered);
                        buffered = length;
                    }

                    var size = ErofsCompression.CompressLz4Fill(options, context, raw, 0, length, accepted,
                        Math.Min(frameLimit, accepted.Length), out var consumed);
                    if (size <= 0 || consumed <= 0 || consumed > length)
                    {
                        return false;
                    }

                    if (size > blockSize && position % clusterSize + consumed < 2 * clusterSize)
                    {
                        return false;
                    }

                    // A big-pcluster head row on the file's last logical cluster makes the kernel read the
                    // block-count row past the index. Leave the whole terminal cluster to the search, which
                    // stores it plain.
                    if (bigPcluster && fileSize % clusterSize == 0
                        && position + consumed > (CeilDiv(fileSize, clusterSize) - 1L) * clusterSize)
                    {
                        return false;
                    }

                    if (!AcceptsClosingIndexRow(position, consumed, size, clusterSize, fileSize, blockSize))
                    {
                        return false;
                    }

                    filled = consumed;
                    encodedLength = size;
                    return true;
                }

                bool TryFill(out int filled, out int encodedLength)
                {
                    filled = 0;
                    encodedLength = 0;
                    if (options.Algorithm != ErofsCompressionAlgorithm.Lz4)
                    {
                        return false;
                    }

                    // One bounded encode per pcluster. Reading a small multiple of the frame limit lets a
                    // compressible run spend its destination budget on a wider span without re-encoding the
                    // same bytes; exhausting the window is itself a valid destination-fill result, because
                    // the frame still decodes to exactly the consumed input.
                    var window = fileSize <= 256 * 1024
                        ? available
                        : Math.Min(available, Math.Max(frameLimit, clusterSize) * 4);
                    return window > 0 && FillEncoded(window, out filled, out encodedLength);
                }

                if (!TryFill(out bestLength, out bestEncoded))
                {
                    bestLength = 0;
                    bestEncoded = 0;
                    var low = 0;
                    var high = available + 1;
                    for (var probe = Math.Min(clusterSize, available); probe < high; probe = Math.Min(available, checked(probe * 2)))
                    {
                        options.CancellationToken.ThrowIfCancellationRequested();
                        if (!Fits(probe))
                        {
                            high = probe;
                            break;
                        }

                        low = probe;
                        if (probe == available)
                        {
                            break;
                        }
                    }

                    while (low + 1 < high)
                    {
                        options.CancellationToken.ThrowIfCancellationRequested();
                        var mid = low + (high - low) / 2;
                        if (Fits(mid))
                        {
                            low = mid;
                        }
                        else
                        {
                            high = mid;
                        }
                    }
                }

                var nextBoundary = clusterSize - position % clusterSize;
                var sharesHeadPage = position + bestLength >= fileSize && position % clusterSize != 0
                    && fileSize % clusterSize != 0;
                if (bestLength <= bestEncoded || bestLength < nextBoundary || sharesHeadPage)
                {
                    if (clusterSize != blockSize)
                    {
                        return null;
                    }

                    if (options.InterlacedPlainPclusters && position % blockSize != 0)
                    {
                        return null;
                    }

                    var length = Math.Min(available, blockSize);
                    plans.Add(new ErofsPclusterPlan
                    {
                        SourceOffset = position,
                        SourceLength = length,
                        CompressedLength = length,
                        Plain = true,
                        StagedOffset = -1,
                    });
                    position += length;
                    continue;
                }

                var plan = new ErofsPclusterPlan
                {
                    SourceOffset = position,
                    SourceLength = bestLength,
                    CompressedLength = bestEncoded,
                };
                batch.Append(plan, accepted.AsSpan(0, bestEncoded));
                plans.Add(plan);
                position += bestLength;
            }

            if (node.SourceInode is not { SourceCompressed: true }
                && plans.Sum(plan => CeilDiv(plan.CompressedLength, blockSize)) >= CeilDiv(fileSize, clusterSize))
            {
                return null;
            }

            return [.. plans];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(accepted);
            ArrayPool<byte>.Shared.Return(encoded);
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static long PlanEndCluster(ErofsInode node, int index)
    {
        var plans = node.Pclusters!;
        if (index + 1 < plans.Length) return plans[index + 1].SourceOffset / node.LogicalClusterSize;
        var last = plans[index];
        var head = last.SourceOffset / node.LogicalClusterSize;
        var end = (long)node.Size / node.LogicalClusterSize;
        return node.CompressionOptions?.OptimizeCompression == true && end > head && (long)node.Size % node.LogicalClusterSize != 0
            ? end : CeilDiv((long)node.Size, node.LogicalClusterSize);
    }
}