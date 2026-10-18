namespace UotanStudio.Compress.Internal;

internal static class ParallelCompression
{
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>Compresses the chunks in parallel when parallelism applies, and otherwise returns null.
    /// <para>并行可用时并行压缩各块，否则返回 null 交由调用方走原串行路径。</para></summary>
    /// <param name="data">Complete uncompressed input, split into <see cref="DefaultChunkSize"/>-byte chunks with a shorter final chunk; an empty array always takes the sequential path and yields <see langword="null"/>.
    /// <para>完整的未压缩输入，按 <see cref="DefaultChunkSize"/> 字节切块，最后一块可以更短； 空数组始终走串行路径并返回 <see langword="null"/>。</para></param>
    /// <param name="maxDegreeOfParallelism">Caller request resolved by <c>Parallelism.Resolve</c> against the chunk count; <see langword="null"/> or any value of 1 and below leaves the result <see langword="null"/>.
    /// <para>调用方请求，由 <c>Parallelism.Resolve</c> 与块数量共同解析；为 <see langword="null"/> 或不大于 1 时结果为 <see langword="null"/>。</para></param>
    /// <param name="compressChunk">Callback invoked once per chunk with its start offset and length in <paramref name="data"/> plus an empty destination that receives that chunk's compressed bytes; it may run on several threads at once and must be thread-safe. The destination is a pooled <see cref="PooledWriteStream"/> whose growth stops at the chunk's worst-case bound, and the callback owns it only for the call, so it must not be disposed or retained. A <see langword="null"/> callback is not detected up front and fails when the first chunk is invoked.
    /// <para>每个块调用一次的回调，参数为该块在 <paramref name="data"/> 中的起始偏移、长度，以及接收该块压缩字节的空目标； 它可能同时在多个线程上运行，因此必须线程安全。目标是池化的 <see cref="PooledWriteStream"/>，其增长停在块的 最坏情况下界，回调只在本次调用期间借用，不得释放或保留。<see langword="null"/> 回调不会被提前发现，并在调用 首个块时失败。</para></param>
    /// <returns>The chunk results concatenated in chunk order, or <see langword="null"/> when parallelism does not apply and the caller should compress sequentially.
    /// <para>按块顺序拼接后的各块结果；并行不适用、调用方应改为串行压缩时返回 <see langword="null"/>。</para></returns>
    /// <remarks>Each chunk lands in its own pooled buffer, so the only copy into the returned array is the ordered concatenation; no chunk needs an intermediate exact-size copy of its own.
    /// <para>每个块写入自己的池化缓冲，因此写入返回数组的拷贝只有按序拼接这一步；任何块都不需要额外的精确尺寸拷贝。</para></remarks>
    public static byte[]? TryCompressChunks(byte[] data, int? maxDegreeOfParallelism, Action<int, int, Stream> compressChunk)
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

        var results = new PooledWriteStream[chunkCount];
        try
        {
            Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
            {
                var start = i * DefaultChunkSize;
                var count = Math.Min(DefaultChunkSize, data.Length - start);
                var chunk = new PooledWriteStream(ChunkOutputBound(count));
                results[i] = chunk;
                compressChunk(start, count, chunk);
            });

            var total = 0;
            foreach (var chunk in results)
            {
                total += chunk.WrittenLength;
            }

            var output = new byte[total];
            var offset = 0;
            foreach (var chunk in results)
            {
                var length = chunk.WrittenLength;
                Buffer.BlockCopy(chunk.Buffer, 0, output, offset, length);
                offset += length;
            }

            return output;
        }
        finally
        {
            foreach (var chunk in results)
            {
                chunk?.Dispose();
            }
        }
    }

    /// <summary>
    /// Soft upper bound for one chunk's compressed bytes: the chunk length plus 1/16 of it and a small fixed
    /// allowance, which covers the worst-case expansion of every codec on this path. A codec that somehow needs
    /// more still grows past the bound instead of failing.
    /// <para>单个块压缩字节的软上界：块长度加上其 1/16 与少量固定余量，覆盖本路径上每种编解码器的最坏膨胀。若某个
    /// 编解码器仍需要更多空间，缓冲会越过该上界继续增长而不是失败。</para>
    /// </summary>
    /// <param name="chunkLength">Uncompressed length of one chunk, always positive and at most
    /// <see cref="DefaultChunkSize"/>. <para>单个块的未压缩长度，始终为正且不超过 <see cref="DefaultChunkSize"/>。</para></param>
    /// <returns>The soft capacity bound in bytes. <para>软容量上界（字节）。</para></returns>
    private static int ChunkOutputBound(int chunkLength) => chunkLength + (chunkLength >> 4) + 256;
}