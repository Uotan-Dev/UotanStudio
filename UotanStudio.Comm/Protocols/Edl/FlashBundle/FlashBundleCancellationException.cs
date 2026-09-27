namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Raised when a flash bundle is cancelled, carrying the structured result that records which
/// items were acknowledged, which were interrupted mid-transfer, and whether a reconnect is
/// required.
/// <para>刷写包被取消时抛出，随附结构化结果，记录哪些条目已获确认、哪些在传输中途被打断，
/// 以及是否需要重连。</para>
/// Cancellation after payload bytes reached the device is never evidence that the device received
/// nothing; inspect <see cref="Result"/>.<see cref="FlashBundleResult.Items"/> instead of treating
/// this exception as an unmodified-storage signal.
/// <para>载荷字节已到达设备之后的取消绝不是“设备未收到数据”的证据；请检查
/// <see cref="Result"/>.<see cref="FlashBundleResult.Items"/>，不要把它当作存储未变更的信号。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FlashBundleCancellationException"/> class.
/// <para>初始化 <see cref="FlashBundleCancellationException"/> 类的新实例。</para>
/// </remarks>
/// <param name="result">The structured cancellation result. <para>结构化的取消结果。</para></param>
/// <param name="token">The cancellation token that was requested. <para>已请求的取消令牌。</para></param>
public sealed class FlashBundleCancellationException(FlashBundleResult result, CancellationToken token)
    : OperationCanceledException("The flash bundle was cancelled.", token)
{
    /// <summary>
    /// Gets the structured result captured at cancellation time.
    /// <para>获取取消时捕获的结构化结果。</para>
    /// </summary>
    /// <value>A completed result with per-item evidence, never <see langword="null"/>.
    /// <para>带逐条目证据的完整结果，绝不会为 <see langword="null"/>。</para></value>
    public FlashBundleResult Result { get; } = result ?? throw new ArgumentNullException(nameof(result));
}