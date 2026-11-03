using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Reopens the transport for a faulted MTK session so the boot handshake can run again.
/// <para>为已故障的 MTK 会话重新打开传输，以便再次执行引导握手。</para>
/// </summary>
/// <remarks>
/// A transport cannot reopen itself: the USB adapter handed to a session wraps an already-open
/// device session, its <c>Connect</c> only records state, and disposing it closes the handle. Recovery
/// therefore has to go back through the device session factory, which is what this seam provides.
/// A session constructed without a reopener reports reconnect as unsupported instead of pretending.
/// <para>传输无法自行重开：交给会话的 USB 适配器包装的是已打开的设备会话，其 <c>Connect</c> 只记录状态，
/// 而释放它会关闭句柄。因此恢复必须回到设备会话工厂，这正是本接缝的作用。未注入重开器的会话会把重连
/// 报告为不支持，而不是假装成功。</para>
/// </remarks>
public interface IMtkTransportReopener
{
    /// <summary>
    /// Opens a new transport bound to the same physical target and command endpoints.
    /// <para>打开一个绑定到同一物理目标与命令端点的新传输。</para>
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A freshly opened transport the session takes ownership of.
    /// <para>会话将接管的新打开传输。</para></returns>
    /// <exception cref="OperationCanceledException">The caller cancelled the reopen.
    /// <para>调用方取消了重开。</para></exception>
    Task<ICommTransport> ReopenAsync(CancellationToken cancellationToken);
}