using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Sahara memory-debug (crashdump RAM dump) entry points on an EDL session: the sequence runs on the
/// session's own transport with neither a programmer upload nor a Firehose configure, and every step
/// delegates its wire logic to <see cref="SaharaProtocol"/>.
/// <para>Sahara 内存调试（crashdump RAM dump）在 EDL 会话上的入口：序列在会话自有传输上运行，
/// 既不加载 programmer 也不做 Firehose configure，且每一步的线上逻辑都委托给
/// <see cref="SaharaProtocol"/>。</para>
/// </summary>
/// <remarks>
/// <para>A target in crashdump mode only ever answers a memory-debug HELLO, so these entries create the
/// session's Sahara instance themselves instead of waiting for <see cref="IsConfigured"/> to turn true.
/// Once the link has been driven into Firehose - by a successful configure, by a programmer upload, or
/// by a probe that classified Firehose XML - the memory-debug packet sequence is no longer
/// interpretable, so every entry refuses there before a byte reaches the transport.</para>
/// <para>处于 crashdump 模式的目标只会应答内存调试 HELLO，因此这些入口会自行创建会话的 Sahara
/// 实例，而不等待 <see cref="IsConfigured"/> 变为真。一旦链路被推入 Firehose——无论来自成功的
/// configure、programmer 上传，还是识别出 Firehose XML 的探测——内存调试的包序列都不再可解释，
/// 因此每个入口都会在任一字节到达传输之前在此拒绝。</para>
/// </remarks>
public sealed partial class EdlSession
{
    /// <summary>
    /// Reads the crashdump target's Sahara debug region table on this session's transport.
    /// <para>在本会话的传输上读取 crashdump 目标的 Sahara 调试区域表。</para>
    /// </summary>
    /// <remarks>
    /// <para>Unlike <see cref="InitializeAsync(Stream?,CancellationToken)"/>, no programmer is uploaded
    /// and Firehose is never configured, so the link stays in Sahara memory-debug mode for the follow-up
    /// region dumps. The session's Firehose operation gate is still taken, so a dump cannot interleave
    /// with another session operation.</para>
    /// <para>与 <see cref="InitializeAsync(Stream?,CancellationToken)"/> 不同，本入口不上传 programmer，
    /// 也绝不配置 Firehose，因此链路保持 Sahara 内存调试模式以支撑后续的区域转储。会话的 Firehose
    /// 操作门仍会被占用，因此转储不会与另一个会话操作交错。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token checked between Sahara packets.
    /// <para>在 Sahara 数据包之间检查的取消令牌。</para></param>
    /// <returns>The regions in table order; empty when the target advertised a zero-length table.
    /// <para>按表顺序排列的区域；目标声明零长度表时为空。</para></returns>
    /// <exception cref="InvalidOperationException">The session already reached Firehose, so the link is
    /// not a Sahara memory-debug link. <para>会话已经抵达 Firehose，因此该链路并非 Sahara 内存调试链路。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    /// <exception cref="SaharaException">The target is not in memory-debug mode, its HELLO is malformed,
    /// or its reported table is out of bounds. <para>目标不处于内存调试模式、HELLO 畸形，或其上报的表越界。</para></exception>
    public async Task<IReadOnlyList<SaharaMemoryRegion>> ReadMemoryDebugRegionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var sahara = EnsureMemoryDebugSahara();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await sahara.ReadMemoryDebugRegionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams one crashdump memory region into a caller-owned destination through this session's
    /// transport, so the host keeps control of publishing, hashing and cancellation granularity.
    /// <para>通过本会话的传输把单个 crashdump 内存区域流式写入调用方持有的目标，从而使宿主保留
    /// 发布、哈希与取消粒度的控制权。</para>
    /// </summary>
    /// <remarks>
    /// <para>The region table must have been read first by
    /// <see cref="ReadMemoryDebugRegionsAsync(CancellationToken)"/> on the same session, because that
    /// read fixes the addressing width the target expects. The destination receives raw bytes with no
    /// framing and is not closed by this call.</para>
    /// <para>区域表必须先由同一会话上的 <see cref="ReadMemoryDebugRegionsAsync(CancellationToken)"/>
    /// 读取，因为那次读取会固定目标所期望的地址宽度。目标只会收到不带帧的裸字节，且本调用不会关闭
    /// 该目标流。</para>
    /// </remarks>
    /// <param name="region">The region to dump, taken from this session's own table read.
    /// <para>要转储的区域，取自本会话自身的表读取结果。</para></param>
    /// <param name="destination">The writable sink; the caller retains ownership.
    /// <para>可写的接收端；调用方保留所有权。</para></param>
    /// <param name="cancellationToken">Cancellation token checked before every MEMORY_READ.
    /// <para>在每次 MEMORY_READ 之前检查的取消令牌。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> or <paramref name="destination"/>
    /// is <see langword="null"/>. <paramref name="region"/> 或 <paramref name="destination"/> 为
    /// <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">The session already reached Firehose.
    /// <para>会话已经抵达 Firehose。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    /// <exception cref="SaharaException">The region is larger than the host bound, its address range
    /// overflows, or the target returned fewer bytes than requested.
    /// <para>区域超出主机上限、地址区间溢出，或目标返回的字节少于请求。</para></exception>
    public async Task<ulong> DumpMemoryRegionAsync(SaharaMemoryRegion region, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposed();
        var sahara = EnsureMemoryDebugSahara();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await sahara.DumpMemoryRegionAsync(region, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a caller-owned crashdump collection by leaving Sahara memory-debug mode with a RESET, so the
    /// target does not stay parked in the dump state after the last region byte arrived.
    /// <para>用一次 RESET 结束调用方自持的 crashdump 收集，使目标在最后一个区域字节到达后不再停留在转储
    /// 状态。</para>
    /// </summary>
    /// <remarks>
    /// <para>Owns the same session operation gate as the region reads, so the reset cannot interleave with
    /// another session operation.</para>
    /// <para>与区域读取使用同一把会话操作门，因此复位不会与另一个会话操作交错。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token checked while waiting for the gate and during
    /// the exchange. <para>在等待操作门与交换期间检查的取消令牌。</para></param>
    /// <exception cref="InvalidOperationException">The session already reached Firehose, so the link is
    /// not a Sahara memory-debug link. <para>会话已经抵达 Firehose，因此该链路并非 Sahara 内存调试链路。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    /// <exception cref="SaharaException">The target did not answer the RESET with a RESET_RESPONSE.
    /// <para>目标没有以 RESET_RESPONSE 应答这次 RESET。</para></exception>
    public async Task ResetMemoryDebugLinkAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var sahara = EnsureMemoryDebugSahara();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await sahara.ResetMemoryDebugLinkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts the RESET that leaves memory-debug mode without replacing the exception the caller is
    /// already unwinding.
    /// <para>尝试以 RESET 离开内存调试模式，且不会取代调用方正在展开的异常。</para>
    /// The wire-level cleanup failure is swallowed by the protocol, while a refused session state or
    /// gate still surfaces so the caller can report why no reset happened.
    /// <para>链路层面的清理失败由协议自行吞掉，而会话状态或操作门的拒绝仍会上浮，让调用方能够报告
    /// 复位为何没有发生。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The session already reached Firehose, so the link is
    /// not a Sahara memory-debug link. <para>会话已经抵达 Firehose，因此该链路并非 Sahara 内存调试链路。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task TryResetMemoryDebugLinkAsync()
    {
        ThrowIfDisposed();
        var sahara = EnsureMemoryDebugSahara();
        using var operation = await Firehose!.EnterOperationAsync(CancellationToken.None).ConfigureAwait(false);
        await sahara.TryResetMemoryDebugLinkAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Collects the whole crashdump region table into <paramref name="outputDirectory"/>.
    /// <para>把整张 crashdump 区域表收集到 <paramref name="outputDirectory"/>。</para>
    /// Each region lands in a sibling partial file and is renamed over its target path only after the
    /// complete dump, and the link ends with a Sahara RESET.
    /// <para>每个区域先落在同级的 partial 文件，只有完整转储后才重命名覆盖其目标路径，且链路以一次
    /// Sahara RESET 结束。</para>
    /// </summary>
    /// <remarks>
    /// <para>Prefer <see cref="DumpMemoryRegionAsync(SaharaMemoryRegion,Stream,CancellationToken)"/> when
    /// the caller owns the atomic publisher or must verify bytes before publishing; this overload leaves
    /// naming, staging and the final reset to the protocol.</para>
    /// <para>当调用方自己持有原子发布器，或必须在发布前校验字节时，应优先使用
    /// <see cref="DumpMemoryRegionAsync(SaharaMemoryRegion,Stream,CancellationToken)"/>；本重载把命名、
    /// 暂存与最后的复位都交给协议处理。</para>
    /// </remarks>
    /// <param name="outputDirectory">Directory the region files are written into; created when missing.
    /// <para>写入区域文件的目录；不存在时会被创建。</para></param>
    /// <param name="cancellationToken">Cancellation token checked between packets and chunks.
    /// <para>在数据包与块之间检查的取消令牌。</para></param>
    /// <returns>The regions that were written, in table order.
    /// <para>实际写入的区域，按表顺序。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is empty or whitespace.
    /// <paramref name="outputDirectory"/> 为空或仅空白。</exception>
    /// <exception cref="InvalidOperationException">The session already reached Firehose.
    /// <para>会话已经抵达 Firehose。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    /// <exception cref="SaharaException">A region proposed an unsafe or duplicated file name, or the
    /// table read failed; a best-effort reset is still attempted.
    /// <para>某个区域给出了不安全或重复的文件名，又或者表读取失败；仍会尝试尽力复位。</para></exception>
    public async Task<IReadOnlyList<SaharaMemoryRegion>> CollectMemoryDumpAsync(string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ThrowIfDisposed();
        var sahara = EnsureMemoryDebugSahara();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await sahara.CollectMemoryDumpAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the Sahara instance carrying the memory-debug sequence, creating it on this session's
    /// transport, and refuses a link that already left crashdump mode.
    /// <para>返回承载内存调试序列的 Sahara 实例（必要时在本会话传输上创建），并拒绝已经离开
    /// crashdump 模式的链路。</para>
    /// </summary>
    /// <remarks>
    /// <para>The guard is a state check rather than a probe: probing would consume the HELLO the
    /// memory-debug sequence has to read itself, so a session that never uploaded a programmer is
    /// accepted on trust and the target's HELLO mode remains the authoritative answer.</para>
    /// <para>该护栏是状态检查而非探测：探测会消耗内存调试序列必须亲自读取的 HELLO，因此对于从未
    /// 上传 programmer 的会话我们选择直接放行，并以目标 HELLO 自报的模式作为权威答案。</para>
    /// </remarks>
    /// <returns>The session's Sahara instance. <para>会话的 Sahara 实例。</para></returns>
    private SaharaProtocol EnsureMemoryDebugSahara()
    {
        if (IsConfigured || _protocolState == EdlProtocolState.Firehose
            || LinkStage is EdlLinkStage.ProgrammerUploading or EdlLinkStage.FirehoseVerifying or EdlLinkStage.FirehoseReady)
        {
            throw new InvalidOperationException(
                "Sahara memory debug only runs on a crashdump link that never uploaded a programmer or configured Firehose. " +
                "Sahara 内存调试只能作用于从未上传 programmer、也从未配置 Firehose 的 crashdump 链路。");
        }

        return Sahara ??= new SaharaProtocol(Transport);
    }
}