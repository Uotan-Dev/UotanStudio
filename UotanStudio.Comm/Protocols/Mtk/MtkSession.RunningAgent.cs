namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Attaches a fresh session to an explicitly selected running XFlash DA2 at an idle command boundary.
    /// <para>将全新会话附加到显式选定、处于空闲命令边界的运行中 XFlash DA2。</para></summary>
    /// <remarks>The caller must select and verify the reconnected transport and the DA protocol.
    /// Geometry and packet lengths are queried anew; no boot handshake, agent upload, speed change,
    /// storage transfer or command replay occurs. Successful discovery does not prove hardware identity
    /// or authentication. Boot identity remains unavailable. A failed exchange faults the session;
    /// this method cannot resynchronize an interrupted command or revive a faulted session.
    /// <para>调用方必须选择并验证重连传输与 DA 协议。重新查询几何信息和包长度，不执行 boot 握手、
    /// 代理上传、提速、存储传输或命令重放。发现成功不证明硬件身份或认证通过，boot 身份信息仍不可用。
    /// 交换失败会使会话故障；本方法不能重新同步中断的命令，也不能复活故障会话。</para></remarks>
    /// <param name="kind">The explicit storage family; absent storage is rejected without fallback.
    /// <para>显式存储家族；不存在该家族时拒绝附加，不进行回退。</para></param>
    /// <param name="timeoutMs">The cooperative exchange deadline after acquiring the operation gate, from 1 ms to 24 hours.
    /// <para>取得操作门后的协作式交换截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete exchange.
    /// <para>用于等待与完整交换的取消令牌。</para></param>
    /// <returns>Freshly validated geometry published only after all replies complete.
    /// <para>仅在全部回复完成后发布的、重新校验的几何信息。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not new or the transport is disconnected.
    /// <para>会话不是全新状态，或传输已断开。</para></exception>
    /// <exception cref="MtkProtocolException">The selected storage is absent or the response is invalid.
    /// <para>选定的存储不存在，或回复无效。</para></exception>
    /// <exception cref="TimeoutException">The cooperative exchange deadline expired.
    /// <para>协作式交换截止时间已到期。</para></exception>
    public async Task<MtkXFlashStorageInfo> AttachRunningXFlashAgentAsync(MtkXFlashStorageKind kind,
        int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Created);
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            try
            {
                var channel = new MtkXFlashChannel(Transport, _timeoutMs);
                var storage = await QueryXFlashStorageOwnedAsync(channel, kind, deadline.Token).ConfigureAwait(false)
                    ?? throw new MtkProtocolException("The running XFlash agent reported no storage of the selected family.");
                deadline.Token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xflash = channel;
                XFlashStorage = storage;
                _state = MtkSessionState.XFlashStorageReady;
                return storage;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXFlashStorage();
                throw new TimeoutException("The running XFlash agent attachment deadline expired; reconnect before continuing.", error);
            }
            catch
            {
                FaultXFlashStorage();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Attaches a fresh session to an explicitly selected running XML DA2 at an idle command boundary.
    /// <para>将全新会话附加到显式选定、处于空闲命令边界的运行中 XML DA2。</para></summary>
    /// <remarks>The caller must verify the transport and protocol. This negotiates host callbacks and
    /// queries bounded hardware information, without initial START consumption, boot handshake,
    /// hardware initialization, agent upload, authentication, speed changes or command replay.
    /// Geometry is published after END/START completes; it proves neither identity nor authorization.
    /// Boot identity remains unavailable. Failure faults the session and requires a new connection.
    /// <para>调用方必须验证传输与协议。本方法协商主机回调并查询有界硬件信息，不消费初始 START，
    /// 不执行 boot 握手、硬件初始化、代理上传、认证、提速或命令重放。
    /// 几何信息在 END/START 完成后发布，不证明身份或授权。boot 身份信息仍不可用。
    /// 失败会使会话故障，并要求建立新连接。</para></remarks>
    /// <param name="timeoutMs">The cooperative exchange deadline after acquiring the operation gate, from 1 ms to 24 hours.
    /// <para>取得操作门后的协作式交换截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete exchange.
    /// <para>用于等待与完整交换的取消令牌。</para></param>
    /// <returns>Freshly validated XML storage geometry.
    /// <para>重新校验的 XML 存储几何信息。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not new or the transport is disconnected.
    /// <para>会话不是全新状态，或传输已断开。</para></exception>
    /// <exception cref="TimeoutException">The cooperative exchange deadline expired.
    /// <para>协作式交换截止时间已到期。</para></exception>
    public async Task<MtkXmlStorageInfo> AttachRunningXmlAgentAsync(int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Created);
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            try
            {
                var channel = new MtkXmlChannel(Transport, _timeoutMs);
                var storage = await QueryXmlStorageOwnedAsync(channel, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xml = channel;
                XmlStorage = storage;
                _state = MtkSessionState.XmlStorageGeometryKnown;
                return storage;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The running XML agent attachment deadline expired; reconnect before continuing.", error);
            }
            catch
            {
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }
}