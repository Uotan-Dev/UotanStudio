using System.Buffers;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that plans byte-granular dense pclusters: it binary-searches the longest source span whose encoding still fits the pcluster limit, keeps only that accepted frame, stages it once, and falls back to plain clusters when compression gains nothing.
/// <para>EROFS 构建器的组成部分，负责按字节粒度规划稠密 pcluster：二分查找在 pcluster 上限内能容纳
/// 的最长源片段，只保留该片段编码出的一帧并把它写入暂存文件一次，压缩无收益时退回明文簇。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>Fills bounded physical clusters with byte-granular input, as permitted by upstream di_clusterofs. Every accepted probe copies its frame over one pooled buffer, so the staging file receives exactly the frame the emitted compressed plan points at and the write pass copies it instead of compressing the same bytes again.
    /// <para>按上游 di_clusterofs 允许的方式以字节粒度填满有界物理簇：每次被接受的探测都把自己的编码帧
    /// 复制到同一块池化缓冲上，因此暂存文件只收到已发出压缩规划实际指向的那一帧，写出阶段直接复制而
    /// 不会把同样的字节再压缩一次。</para>
    /// <para>The two bounds a pcluster has to satisfy are separate because a reader derives them from
    /// different index fields. The physical bound is the number of blocks the index can name: without the
    /// big-pcluster advise <c>z_erofs_get_extent_compressedlen</c> pins it to exactly one filesystem
    /// block, so a frame larger than one block is unnameable, while with the advise the count is carried by
    /// the first non-head row, which only exists once the pcluster covers a second logical cluster. The
    /// logical bound is the span the head and non-head rows credit to the frame, and it is capped by the
    /// declared pcluster size when that size is a statement about logical spans (big pclusters) and by the
    /// format ceiling otherwise, since <see cref="ErofsOptions.MaxPclusterSize"/> is the declared physical
    /// pcluster size of the image.</para>
    /// <para>这两个边界必须分开，因为读取方是从不同索引字段推导它们的。物理边界是索引能够表明的块数：
    /// 未声明 big pcluster 时 <c>z_erofs_get_extent_compressedlen</c> 把它钉死为一个文件系统块，超过一块的帧
    /// 根本无法表达；声明时块数由首个非头簇行携带，而行只有当 pcluster 覆盖第二个逻辑簇时才存在。逻辑边界
    /// 是头簇与非头簇行记给该帧的跨度：声明 big pcluster 时该跨度受已声明 pcluster 大小约束，否则受格式
    /// 上限约束，因为 <see cref="ErofsOptions.MaxPclusterSize"/> 描述的是镜像的物理 pcluster 大小。</para></summary>
    private static PclusterPlan[]? TryPlanDenseCompressed(BuildNode node, int blockSize, ErofsOptions options,
        Core.CompressionPayloadStaging staging)
    {
        var clusterSize = node.LogicalClusterSize;
        var bigPcluster = DeclaredBigPcluster(options);
        var declaredPclusterSize = Math.Min(options.MaxPclusterSize, ErofsCompression.MaxPclusterSize);
        var frameLimit = bigPcluster ? declaredPclusterSize : blockSize;
        var inputLimit = bigPcluster ? declaredPclusterSize : ErofsCompression.MaxPclusterSize;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance is > 0 and < ErofsCompression.Lz4DistanceMax)
            inputLimit = Math.Min(inputLimit, options.Lz4MaxDistance);
        if (inputLimit < clusterSize) return null;
        using var input = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        var raw = new byte[(int)Math.Min(node.Size, inputLimit)];
        var encoded = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var accepted = ArrayPool<byte>.Shared.Rent(Math.Max(1, frameLimit));
        try
        {
            var plans = new List<PclusterPlan>();
            long position = 0;
            while (position < node.Size)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var available = (int)Math.Min(node.Size - position, raw.Length);
                var buffered = 0;
                var bestLength = 0;
                var bestEncoded = 0;
                bool Fits(int length)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    if (length > buffered)
                    {
                        input.Position = position + buffered;
                        input.ReadExactly(raw, buffered, length - buffered);
                        buffered = length;
                    }
                    var size = ErofsCompression.AlgorithmCompress(options,
                        raw, 0, length, encoded, 0, encoded.Length);
                    if (size <= 0 || size > frameLimit) return false;
                    if (size > blockSize && (int)(position % clusterSize) + length < 2 * clusterSize) return false;
                    bestLength = length;
                    bestEncoded = size;
                    encoded.AsSpan(0, size).CopyTo(accepted);
                    return true;
                }
                var low = 0;
                var high = available + 1;
                for (var probe = Math.Min(clusterSize, available); probe < high; probe = Math.Min(available, checked(probe * 2)))
                {
                    if (!Fits(probe)) { high = probe; break; }
                    low = probe;
                    if (probe == available) break;
                }
                while (low + 1 < high)
                {
                    var mid = low + (high - low) / 2;
                    if (Fits(mid)) low = mid;
                    else high = mid;
                }
                var nextBoundary = clusterSize - position % clusterSize;
                var sharesHeadPage = position + bestLength >= node.Size && position % clusterSize != 0
                    && node.Size % clusterSize != 0;
                if (bestLength <= bestEncoded || bestLength < nextBoundary || sharesHeadPage)
                {
                    if (clusterSize != blockSize) return null;
                    var length = Math.Min(available, blockSize);
                    plans.Add(new PclusterPlan { SourceOffset = position, SourceLength = length, CompressedLength = length, Plain = true, StagedOffset = -1 });
                    position += length;
                }
                else
                {
                    plans.Add(new PclusterPlan
                    {
                        SourceOffset = position,
                        SourceLength = bestLength,
                        CompressedLength = bestEncoded,
                        StagedOffset = staging.Append(accepted.AsSpan(0, bestEncoded))
                    });
                    position += bestLength;
                }
            }
            return plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) < CeilDiv(node.Size, blockSize)
                ? [.. plans] : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(accepted);
        }
    }

    private static long PlanEndCluster(BuildNode node, int index)
    {
        var plans = node.Pclusters!;
        if (index + 1 < plans.Length) return plans[index + 1].SourceOffset / node.LogicalClusterSize;
        var last = plans[index];
        var head = last.SourceOffset / node.LogicalClusterSize;
        var end = node.Size / node.LogicalClusterSize;
        return node.CompressionOptions?.OptimizeCompression == true && end > head && node.Size % node.LogicalClusterSize != 0
            ? end : CeilDiv(node.Size, node.LogicalClusterSize);
    }
}