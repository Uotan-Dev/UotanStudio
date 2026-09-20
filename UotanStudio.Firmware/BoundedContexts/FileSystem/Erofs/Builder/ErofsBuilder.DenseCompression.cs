using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;

public sealed partial class ErofsBuilder
{
    /// <summary>Fills bounded physical clusters with byte-granular input, as permitted by upstream di_clusterofs.</summary>
    private static PclusterPlan[]? TryPlanDenseCompressed(BuildNode node, int blockSize, ErofsOptions options)
    {
        var clusterSize = node.LogicalClusterSize;
        var inputLimit = 1 << 20;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance is > 0 and < ErofsCompression.Lz4DistanceMax)
            inputLimit = Math.Min(inputLimit, options.Lz4MaxDistance);
        // A legacy (pre-5.13) decoder validates every LZ4 match against the 64 KiB window the image
        // declares, and its physical pcluster is capped at one block anyway, so bounding the decoded
        // span by that window costs nothing measurable and keeps the stream decodable.
        // legacy（pre-5.13）解码器会按镜像声明的 64 KiB 窗口校验每个 LZ4 匹配，且它的物理 pcluster
        // 本就限制在一个块内；把解压跨度限制在该窗口内没有实质代价，并能保证可解码。
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
        // A pre-5.13 kernel derives a pcluster's demanded output from the index alone: for the
        // partially covered trailing cluster the terminator entry supplies the exact end, so a
        // compressed extent may end anywhere past the end of its own head cluster. An extent that
        // stops inside its head cluster has no slot left for that terminator (the head already owns
        // the entry for that logical cluster), and the decoder would be asked for a whole cluster it
        // cannot produce ("failed to decompress ... out[...]"). Probes therefore keep requiring that
        // the extent reaches its head cluster's boundary; shorter remainders go out as Plain.
        // pre-5.13 内核仅凭索引推导 pcluster 所需的输出量：尾部非整簇由终止项提供精确末端，因此
        // 压缩 extent 可以结束在其 head 簇边界之后的任意位置。若 extent 在其 head 簇内部就结束了，
        // 就没有槽位存放该终止项（head 已占用该逻辑簇的索引项），解码器会被要求产出它无法生成的
        // 整簇数据（"failed to decompress ... out[...]"）。故探测仍要求 extent 至少到达其 head 簇
        // 边界；更短的剩余部分按 Plain 输出。
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
            // A final extent that starts inside a cluster shares that cluster's head page with the
            // preceding pcluster. Upstream packers keep such tails raw: pre-5.13 kernels derive the
            // demanded output of the last cluster from its own head entry, and the shared-head
            // layout makes that amount disagree with what the compressed stream yields, so the tail
            // silently decodes to zeros instead of failing. Emitting the remainder as Plain matches
            // the source images and costs at most the partial tail cluster.
            // 起始于簇内部的末尾 extent 与前一个 pcluster 共享该簇的 head 页。上游打包器把这类尾部
            // 保持为原始数据：pre-5.13 内核由末尾簇自身的 head 项推导所需输出量，而共享 head 的布局
            // 使该数量与压缩流的实际产出不一致，于是尾部静默解出零数据而不报错。按 Plain 输出剩余
            // 部分与源镜像一致，最多只付出这个非整簇尾部的体积。
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