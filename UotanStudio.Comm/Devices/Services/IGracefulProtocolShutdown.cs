namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Lets a protocol session say goodbye in its own wire format before the transport is released. The lease teardown
/// runs it once, ahead of the session and transport disposal, so a closing application ends the device-side session
/// instead of leaving it to time out after the USB handle disappears.
/// <para>让协议会话在传输释放之前用自身的线协议告别。租约清理在会话与传输释放之前执行一次，使正在关闭的应用
/// 结束设备端会话，而不是等 USB 句柄消失后由对端超时。</para>
/// </summary>
internal interface IGracefulProtocolShutdown
{
    /// <summary>
    /// Sends the protocol's own close frames. Best effort: the caller keeps disposing whatever this method throws past.
    /// <para>发送协议自身的关闭帧。尽力而为：调用方即使本方法抛出异常也会继续释放。</para>
    /// </summary>
    /// <param name="timeout">Upper bound on the whole farewell. <para>整段告别的上限。</para></param>
    /// <param name="cancellationToken">Cancels the farewell. <para>取消告别。</para></param>
    /// <returns>A task completing when the close frames were written or the budget expired.
    /// <para>关闭帧写出或预算耗尽时完成的任务。</para></returns>
    ValueTask SendShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken);
}