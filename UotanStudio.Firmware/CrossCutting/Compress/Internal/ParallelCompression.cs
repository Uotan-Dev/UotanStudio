namespace UotanStudio.Firmware.CrossCutting.Compress.Internal;

internal static class ParallelCompression
{
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>
    /// When parallelism is applicable (degree &gt; 1 and at least two chunks), compresses the chunks in parallel and returns the concatenated result in chunk order; otherwise returns null and the caller should use its existing sequential path (byte-identical to the previous implementation).
    /// <para>若并行可用（并行度 &gt; 1 且至少两个块），并行压缩各块并返回按序拼接结果； 否则返回 null，调用方应走原有串行路径（输出与既有实现逐字节一致）。</para>
    /// </summary>
    public static byte[]? TryCompressChunks(byte[] data, int? maxDegreeOfParallelism, Func<int, int, byte[]> compressChunk)
    {
        if (data.Length == 0)
        {
            return null;
        }

        var chunkCount = (data.Length + DefaultChunkSize - 1) / DefaultChunkSize;
        if (chunkCount < 2)
        {
            return null;
        }

        var dop = Parallelism.Resolve(maxDegreeOfParallelism, chunkCount);
        if (dop <= 1)
        {
            return null;
        }

        var results = new byte[chunkCount][];
        Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
        {
            var start = i * DefaultChunkSize;
            var count = Math.Min(DefaultChunkSize, data.Length - start);
            results[i] = compressChunk(start, count);
        });

        var total = 0;
        foreach (var r in results)
        {
            total += r.Length;
        }

        var output = new byte[total];
        var offset = 0;
        foreach (var r in results)
        {
            Buffer.BlockCopy(r, 0, output, offset, r.Length);
            offset += r.Length;
        }
        return output;
    }
}