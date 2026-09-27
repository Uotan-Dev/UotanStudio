using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Explicitly requests an immediate XML DA reboot and terminates the session after acknowledgement.
    /// No startup, transfer or disposal operation calls this automatically.
    /// <para>显式请求 XML DA 立即重启，并在确认后终止会话。启动、传输或释放操作均不会自动调用此方法。</para></summary>
    /// <remarks>The caller must authorize the operation. Acknowledgement is not evidence of the new boot mode
    /// or durable storage. A missing response faults the session without retry. A local disconnect failure
    /// preserves the acknowledged terminal state. Reconnection requires a separately selected endpoint.
    /// <para>调用方必须授权该操作。确认并不证明已进入新的启动模式或存储已持久化。缺失响应会使会话故障且不重试。
    /// 本地断开失败仍保留已确认的终态。重连需要单独选定端点。</para></remarks>
    /// <param name="options">The explicit mode and optional authorized ADB/logging flags.
    /// <para>显式模式和可选的授权 ADB/日志标志。</para></param>
    /// <param name="timeoutMs">The cooperative deadline shared by both commands, from 1 ms to 24 hours.
    /// <para>两个命令共享的协作式截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the command exchanges.
    /// <para>等待与命令交换的取消令牌。</para></param>
    /// <returns>A task completing after acknowledgement and local disconnection.
    /// <para>确认并完成本地断开后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">XML storage discovery has not completed or the channel is unavailable.
    /// <para>XML 存储发现尚未完成或通道不可用。</para></exception>
    /// <exception cref="MtkProtocolException">A command is rejected or the response is malformed.
    /// <para>命令被拒绝或响应格式错误。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired.
    /// <para>协作式截止时间已到。</para></exception>
    public async Task RebootXmlAsync(MtkXmlRebootOptions options, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mode = options.BuildBootModeCommand();
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel is unavailable.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                if (mode is not null)
                {
                    await SendXmlControlCommandOwnedAsync(channel, mode, token).ConfigureAwait(false);
                }
                var reboot = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:REBOOT"),
                    new XElement("arg", new XElement("action", "IMMEDIATE")));
                await SendXmlControlCommandOwnedAsync(channel, reboot, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML reboot deadline expired; the target outcome is unknown.", error);
            }
            catch
            {
                FaultXml();
                throw;
            }
            _state = MtkSessionState.XmlRebootAcknowledged;
            _xml = null;
            XmlStorage = null;
            Transport.Disconnect();
        }
        finally
        {
            _operation.Release();
        }
    }
}