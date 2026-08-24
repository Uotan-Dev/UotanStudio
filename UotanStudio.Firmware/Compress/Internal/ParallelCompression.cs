namespace UotanStudio.Firmware.Compress.Internal;

/// <summary>
/// 共享分块并行压缩辅助：把输入切成固定大小的块，每块用回调压缩成独立成员/帧，
/// 并行执行后按块序拼接。仅用于**编码**路径；解码端不需要多线程（见
/// <see cref="CompressionOptions.MaxDegreeOfParallelism"/> 文档）。
/// <para>Shared chunked-parallel compression helper: splits the input into fixed-size
/// chunks, compresses each chunk into an independent member/frame via a callback,
/// runs them in parallel, and concatenates the results in chunk order. Used for the
/// **encoding** path only; decoding needs no parallelism (see
/// <see cref="CompressionOptions.MaxDegreeOfParallelism"/> docs).</para>
/// </summary>
internal static class ParallelCompression
{
    /// <summary>默认块大小（1 MiB）：成员头/帧开销占比足够小，且块数足够并行。</summary>
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>
    /// 若并行可用（并行度 &gt; 1 且至少两个块），并行压缩各块并返回按序拼接结果；
    /// 否则返回 null，调用方应走原有串行路径（输出与既有实现逐字节一致）。
    /// <para>When parallelism is applicable (degree &gt; 1 and at least two chunks),
    /// compresses the chunks in parallel and returns the concatenated result in chunk
    /// order; otherwise returns null and the caller should use its existing sequential
    /// path (byte-identical to the previous implementation).</para>
    /// </summary>
    /// <param name="data">待压缩数据。<para>Data to compress.</para></param>
    /// <param name="maxDegreeOfParallelism">并行度（见 <see cref="Parallelism.Resolve"/>）。<para>The degree of parallelism.</para></param>
    /// <param name="compressChunk">把 [start, start+count) 压缩成一个独立成员的委托。
    /// <para>Delegate compressing [start, start+count) into one independent member.</para></param>
    /// <returns>拼接后的多成员输出；并行不可用时为 null。</returns>
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