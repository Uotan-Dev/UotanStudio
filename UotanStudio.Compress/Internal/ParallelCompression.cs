namespace UotanStudio.Compress.Internal;

internal static class ParallelCompression
{
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>
    /// Compresses the chunks in parallel when parallelism applies, and otherwise returns null.
    /// <para>并行可用时并行压缩各块，否则返回 null 交由调用方走原串行路径。</para>
    /// </summary>
    /// <param name="data">Complete uncompressed input, split into <see cref="DefaultChunkSize"/>-byte
    /// chunks with a shorter final chunk; an empty array always takes the sequential path and yields
    /// <see langword="null"/>. <para>完整的未压缩输入，按 <see cref="DefaultChunkSize"/> 字节切块，最后一块可以更短；
    /// 空数组始终走串行路径并返回 <see langword="null"/>。</para></param>
    /// <param name="maxDegreeOfParallelism">Caller request resolved by <c>Parallelism.Resolve</c> against
    /// the chunk count; <see langword="null"/> or any value of 1 and below leaves the result
    /// <see langword="null"/>. <para>调用方请求，由 <c>Parallelism.Resolve</c> 与块数量共同解析；为
    /// <see langword="null"/> 或不大于 1 时结果为 <see langword="null"/>。</para></param>
    /// <param name="compressChunk">Callback invoked once per chunk with its start offset in
    /// <paramref name="data"/> and its length, returning that chunk's compressed bytes; it may run on
    /// several threads at once and must be thread-safe. A <see langword="null"/> callback is not detected
    /// up front and fails when the first chunk is invoked.
    /// <para>每个块调用一次的回调，参数为该块在 <paramref name="data"/> 中的起始偏移与长度，返回该块的压缩字节；
    /// 它可能同时在多个线程上运行，因此必须线程安全。<see langword="null"/> 回调不会被提前发现，并在调用首个块时失败。</para></param>
    /// <returns>The chunk results concatenated in chunk order, or <see langword="null"/> when parallelism
    /// does not apply and the caller should compress sequentially.
    /// <para>按块顺序拼接后的各块结果；并行不适用、调用方应改为串行压缩时返回 <see langword="null"/>。</para></returns>
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