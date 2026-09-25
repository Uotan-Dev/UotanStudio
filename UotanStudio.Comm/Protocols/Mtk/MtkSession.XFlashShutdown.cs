using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Explicitly requests shutdown or a boot-mode transition from a storage-ready XFlash
    /// agent. Both statuses must confirm success before the session becomes terminal.
    /// No reboot is performed automatically by flashing or disposal.
    /// <para>向存储就绪的 XFlash agent 显式请求关机或启动模式切换。两个状态都必须
    /// 确认成功后会话才进入终态。刷写或释放不会自动执行重启。</para>
    /// </summary>
    /// <remarks>
    /// Requires caller authorization. Uses synchronous target completion with watchdog,
    /// download-bit, RTC-preservation and USB-retention flags cleared. These flags and the
    /// requested boot mode are target-dependent; ACK does not prove boot or durable storage.
    /// The owned transport is disconnected, not reset, after acknowledgement. A disconnect
    /// failure leaves the acknowledged terminal state observable; Dispose still releases it.
    /// In-flight cancellation or loss of the final response faults the session without retry.
    /// </remarks>
    /// <param name="mode">The explicitly authorized target power/boot request.
    /// <para>显式授权的目标电源/启动请求。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the command exchange.
    /// <para>用于等待与命令交换的取消令牌。</para></param>
    /// <returns>A task that completes after both shutdown statuses are acknowledged and the transport is disconnected.
    /// <para>在两个关机状态均被确认且传输被断开后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not PowerOff, NormalBoot or Fastboot.
    /// <para><paramref name="mode"/> 不是 PowerOff、NormalBoot 或 Fastboot。</para></exception>
    /// <exception cref="InvalidOperationException">Storage is not ready or the XFlash channel is gone.
    /// <para>存储未就绪或 XFlash 通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">The agent rejected a status; the session faults.
    /// <para>agent 拒绝了某个状态；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task ShutdownXFlashAsync(MtkXFlashShutdownMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode is not (MtkXFlashShutdownMode.PowerOff or MtkXFlashShutdownMode.NormalBoot or MtkXFlashShutdownMode.Fastboot))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            var parameters = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(parameters, mode == MtkXFlashShutdownMode.PowerOff ? 0u : 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(12), (uint)mode);
            try
            {
                await SendXFlashWordAsync(channel, 0x010007, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "shutdown request", cancellationToken).ConfigureAwait(false);
                await channel.SendAsync(1, parameters, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "shutdown completion", cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
            }
            catch
            {
                FaultXFlashStorage();
                throw;
            }
            _state = MtkSessionState.XFlashShutdownAcknowledged;
            _xflash = null;
            XFlashStorage = null;
            Transport.Disconnect();
        }
        finally
        {
            _operation.Release();
        }
    }
}