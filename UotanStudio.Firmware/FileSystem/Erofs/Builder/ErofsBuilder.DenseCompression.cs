using UotanStudio.Firmware.FileSystem.Erofs.Models;
namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that plans byte-granular dense pclusters: it binary-searches the longest source span whose encoding still fits the pcluster limit and falls back to plain clusters when compression gains nothing.
/// <para>EROFS 构建器的组成部分，负责按字节粒度规划稠密 pcluster：二分查找在 pcluster 上限内能容纳的最长源片段，
/// 压缩无收益时退回明文簇。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>Fills bounded physical clusters with byte-granular input, as permitted by upstream di_clusterofs.</summary>
    private static PclusterPlan[]? TryPlanDenseCompressed(BuildNode node, int blockSize, ErofsOptions options)
    {
        var clusterSize = node.LogicalClusterSize;
        var inputLimit = 1 << 20;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance is > 0 and < ErofsCompression.Lz4DistanceMax)
            inputLimit = Math.Min(inputLimit, options.Lz4MaxDistance);
        if (options.LegacyCompatibility)
            inputLimit = Math.Min(inputLimit,
                options.Lz4MaxDistance == 0 ? ErofsCompression.Lz4DistanceMax : options.Lz4MaxDistance);
        if (inputLimit < clusterSize) return null;
        using var input = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        var raw = new byte[(int)Math.Min(node.Size, inputLimit)];
        var encoded = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
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
                if (size <= 0 || size > options.MaxPclusterSize) return false;
                bestLength = length;
                bestEncoded = size;
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
            var covered = (position + bestLength) / clusterSize - position / clusterSize;
            var sharesHeadPage = position + bestLength >= node.Size && position % clusterSize != 0
                && node.Size % clusterSize != 0;
            if (bestLength <= bestEncoded || bestLength < nextBoundary
                || bestEncoded > blockSize && covered < 2 || sharesHeadPage)
            {
                if (clusterSize != blockSize) return null;
                var length = Math.Min(available, blockSize);
                plans.Add(new PclusterPlan { SourceOffset = position, SourceLength = length, CompressedLength = length, Plain = true });
                position += length;
            }
            else
            {
                plans.Add(new PclusterPlan { SourceOffset = position, SourceLength = bestLength, CompressedLength = bestEncoded });
                position += bestLength;
            }
        }
        return plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) < CeilDiv(node.Size, blockSize)
            ? plans.ToArray() : null;
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