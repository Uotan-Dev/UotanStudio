namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsBuilder
{
    /// <summary>Fills bounded physical clusters with byte-granular input, as permitted by upstream di_clusterofs.</summary>
    private static PclusterPlan[]? TryPlanDenseCompressed(BuildNode node, int blockSize, ErofsOptions options)
    {
        var clusterSize = node.LogicalClusterSize;
        var inputLimit = 1 << 20;
        // 65535 is the full LZ4 history window, not a limit on the input length.
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance is > 0 and < 65535)
            inputLimit = Math.Min(inputLimit, options.Lz4MaxDistance);
        if (inputLimit < clusterSize) return null;
        using var input = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        var raw = new byte[(int)Math.Min(node.Size, inputLimit)];
        var encoded = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var plans = new List<PclusterPlan>();
        long position = 0;
        // Full-index (pre-big-pcluster) kernels compute a pcluster's output size as the number of
        // covered logical clusters times the cluster size. A compressed pcluster that also covers
        // the trailing partial cluster therefore decodes fewer bytes than the kernel expects and
        // fails with "failed to decompress ... out[...]". The legacy logical planner already
        // truncates to the cluster boundary; the dense planner must obey the same invariant, so
        // probes stop at the last full cluster and the partial tail is emitted as Plain.
        // 全索引（pre-big-pcluster）内核按覆盖的逻辑簇数 × 簇大小计算 pcluster 输出大小。压缩
        // pcluster 若同时覆盖尾部非整簇，解压字节数会少于内核预期并触发
        // "failed to decompress ... out[...]"。legacy 逻辑簇规划器已截断到簇边界，dense 规划器
        // 必须遵守同一不变量：探测止于最后一个完整簇，尾部非整簇按 Plain 输出。
        var alignedEnd = node.Size / clusterSize * clusterSize;
        while (position < node.Size)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var probeLimit = (int)Math.Min(alignedEnd - position, raw.Length);
            if (probeLimit <= 0)
            {
                // Only the trailing partial cluster remains; it can never join a pcluster.
                // 只剩尾部非整簇，不能并入任何 pcluster。
                plans.Add(new PclusterPlan
                {
                    SourceOffset = position,
                    SourceLength = (int)(node.Size - position),
                    CompressedLength = (int)(node.Size - position),
                    Plain = true,
                });
                break;
            }
            var available = (int)Math.Min(node.Size - position, raw.Length);
            var buffered = 0;
            var bestLength = 0;
            var bestEncoded = 0;
            bool Fits(int length)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                if (length > alignedEnd - position) return false;
                if (length > buffered)
                {
                    input.Position = position + buffered;
                    input.ReadExactly(raw, buffered, length - buffered);
                    buffered = length;
                }
                var size = ErofsCompression.AlgorithmCompress((ErofsAlgorithm)options.Algorithm,
                    raw, 0, length, encoded, 0, encoded.Length, options.CompressionLevel,
                    options.DictionarySize, options.WindowBits);
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
            if (bestLength <= bestEncoded || bestLength < nextBoundary && bestLength != available
                || bestEncoded > blockSize && covered < 2)
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