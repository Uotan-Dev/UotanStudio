using System.Collections.Concurrent;
using UotanStudio.Compress.Codecs.Lz4;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// One reusable LZ4 encoder context, borrowed from <see cref="ErofsLz4StatePool"/> for the duration of one
/// planning call. The wrapped native stream is cleared before every encode, so the emitted bytes depend only
/// on the block, its level and its algorithm, exactly as a freshly allocated context would produce them.
/// A context is owned by one thread at a time and is never shared between concurrent encoders.
/// <para>可复用的单个 LZ4 编码上下文，由 <see cref="ErofsLz4StatePool"/> 借出并在一次规划调用期间持有。
/// 包装的原生流在每次编码前清零，因此输出字节只取决于该块、其级别与算法，与新建上下文的结果一致。
/// 上下文同一时刻只属于一个线程，绝不在并发编码之间共享。</para>
/// </summary>
internal sealed unsafe class ErofsLz4Context : IDisposable
{
    private void* _state;

    /// <summary>
    /// Rents a native context sized for the requested encoder identity.
    /// <para>按请求的编码器身份借用一块原生上下文。</para>
    /// </summary>
    /// <param name="highCompression"><see langword="true"/> for the larger HC context.
    /// <para>为 <see langword="true"/> 时使用更大的 HC 上下文。</para></param>
    internal ErofsLz4Context(bool highCompression)
    {
        HighCompression = highCompression;
        _state = Lz4DestinationCodec.RentState(highCompression);
    }

    /// <summary>Whether this context carries the HC encoder's larger state.
    /// <para>该上下文是否承载 HC 编码器更大的状态。</para></summary>
    internal bool HighCompression { get; }

    /// <summary>
    /// Encodes one block through the borrowed context.
    /// <para>经借用的上下文编码一个块。</para>
    /// </summary>
    /// <param name="source">Block payload to encode. <para>待编码的块载荷。</para></param>
    /// <param name="target">Destination span bounded like the context-free overload.
    /// <para>与不带上下文的重载同样受限的目标区间。</para></param>
    /// <param name="algorithm">1 for the fast path, 2 for HC. <para>1 为快速路径，2 为 HC。</para></param>
    /// <param name="level">Compression level handed to the selected path. <para>传给所选路径的压缩级别。</para></param>
    /// <param name="consumed">Bytes the encoder consumed from <paramref name="source"/>.
    /// <para>编码器从 <paramref name="source"/> 消费的字节数。</para></param>
    /// <returns>Bytes written into <paramref name="target"/>.
    /// <para>写入 <paramref name="target"/> 的字节数。</para></returns>
    internal int Encode(ReadOnlySpan<byte> source, Span<byte> target, int algorithm, int level, out int consumed)
        => Lz4DestinationCodec.Encode(source, target, algorithm, level, out consumed, _state);

    /// <inheritdoc />
    public void Dispose()
    {
        Lz4DestinationCodec.ReturnState(_state);
        _state = null;
    }
}

/// <summary>
/// Recycles <see cref="ErofsLz4Context"/> instances between the parallel planning workers of one build.
/// A worker that finishes a payload returns its contexts instead of releasing them, so the next payload planned
/// on that worker reuses the same native allocation and no context is ever used by two workers at once.
/// <para>在同一次构建的并行规划工作者之间回收 <see cref="ErofsLz4Context"/>。工作者完成一份负载后把上下文归还
/// 而不是释放，使该工作者规划下一份负载时复用同一块原生分配，且任何上下文都不会被两个工作者同时使用。</para>
/// </summary>
internal sealed class ErofsLz4StatePool : IDisposable
{
    private readonly ConcurrentQueue<ErofsLz4Context> _fastPath = new();
    private readonly ConcurrentQueue<ErofsLz4Context> _highCompression = new();

    /// <summary>
    /// Borrows an idle context of the requested identity, or allocates one when the pool is empty.
    /// <para>借用请求身份的空闲上下文；池为空时新建一个。</para>
    /// </summary>
    /// <param name="highCompression"><see langword="true"/> to borrow an HC context.
    /// <para>为 <see langword="true"/> 时借用 HC 上下文。</para></param>
    /// <returns>A context the caller owns until <see cref="Return"/>.
    /// <para>由调用方持有至 <see cref="Return"/> 为止的上下文。</para></returns>
    internal ErofsLz4Context Rent(bool highCompression)
    {
        var idle = highCompression ? _highCompression : _fastPath;
        return idle.TryDequeue(out var context) ? context : new ErofsLz4Context(highCompression);
    }

    /// <summary>
    /// Returns a borrowed context, disposing it when the pool already holds one per worker.
    /// <para>归还借出的上下文；池中已按工作者数量存满时直接释放。</para>
    /// </summary>
    /// <param name="context">Context obtained from <see cref="Rent"/>.
    /// <para>由 <see cref="Rent"/> 取得的上下文。</para></param>
    internal void Return(ErofsLz4Context context)
    {
        var idle = context.HighCompression ? _highCompression : _fastPath;
        if (idle.Count >= Core.FirmwareParallelism.MaximumWorkers)
        {
            context.Dispose();
            return;
        }

        idle.Enqueue(context);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        while (_fastPath.TryDequeue(out var context))
        {
            context.Dispose();
        }

        while (_highCompression.TryDequeue(out var context))
        {
            context.Dispose();
        }
    }
}

/// <summary>
/// Entry point the EROFS planners use for their probe encodings: a probe runs through the worker's
/// <see cref="ErofsEncoderContext"/>, which retains one LZ4 context, one ZSTD compressor and one MicroLZMA
/// encoder for the whole payload instead of constructing a backend per probe. The destination capacity is
/// always the full codec bound, because a destination capped at the frame limit makes liblz4's
/// destination-filling encoder replace a match with a literal tail and still report the whole span as consumed,
/// which changes frames the planner accepts.
/// <para>EROFS 规划器用于探测编码的入口：探测经该工作者的 <see cref="ErofsEncoderContext"/> 执行，它在整份负载期间
/// 保留一个 LZ4 上下文、一个 ZSTD 压缩器与一个 MicroLZMA 编码器，而不是每次探测都构造后端。目标容量始终取
/// 编解码器的完整上界，因为把目标限制到帧上限会让 liblz4 的填满目标编码器把匹配换成字面量尾巴却仍报告整段已被消费，
/// 从而改变规划器接受的帧。</para>
/// </summary>
internal static class ErofsPlannerCompression
{
    /// <summary>
    /// Encodes one candidate span with the planner's context.
    /// <para>用规划器的上下文编码一个候选跨度。</para>
    /// </summary>
    /// <param name="options">Codec settings of the node being planned.
    /// <para>正在规划的节点的编解码设置。</para></param>
    /// <param name="context">Encoder state borrowed by the calling worker for the payload being planned, or
    /// <see langword="null"/> to allocate private state for this call.
    /// <para>调用工作者为正在规划的负载借用的编码器状态；为 <see langword="null"/> 时仅为本调用分配私有状态。</para></param>
    /// <param name="source">Buffer holding the candidate bytes. <para>存放候选字节的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the candidate starts.
    /// <para><paramref name="source"/> 中候选的起始偏移。</para></param>
    /// <param name="sourceLength">Candidate length in bytes. <para>候选的字节长度。</para></param>
    /// <param name="target">Buffer receiving the encoded frame. <para>接收编码帧的缓冲区。</para></param>
    /// <param name="targetLength">Destination capacity in bytes. <para>目标容量（字节）。</para></param>
    /// <returns>The encoded length, or <c>0</c> when the frame does not fit the destination.
    /// <para>编码长度；帧放不进目标时为 <c>0</c>。</para></returns>
    internal static int Compress(ErofsOptions options, ErofsEncoderContext? context, byte[] source, int sourceIndex,
        int sourceLength, byte[] target, int targetLength)
        => ErofsCompression.AlgorithmCompress(options, context, source, sourceIndex, sourceLength, target, 0, targetLength);
}