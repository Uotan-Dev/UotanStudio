using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const uint XFlashSync = 0x434E5953;
    private MtkXFlashChannel? _xflash;
    private MtkXFlashConnectionAgent? _xflashConnectionAgent;
    private bool _xflashExternalRamInitialized;

    /// <summary>Queries whether DA1 was entered through Boot ROM or an initialized preloader.
    /// <para>查询 DA1 是通过 Boot ROM 还是已初始化的 preloader 进入的。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact recognized connection agent; unknown replies are rejected.
    /// <para>精确识别的连接 agent；未知回复会被拒绝。</para></returns>
    /// <exception cref="InvalidOperationException">DA1 is not stage-one ready or the channel is gone.
    /// <para>DA1 未处于 stage-one ready 状态或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">The reply was not exactly <c>brom</c> or <c>preloader</c>,
    /// or a control frame was malformed; the session faults.
    /// <para>回复不是精确的 <c>brom</c> 或 <c>preloader</c>，或控制帧格式错误；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkXFlashConnectionAgent> GetXFlashConnectionAgentAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageOneReady);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                var reply = await QueryXFlashControlAsync(channel, 0x04000A, cancellationToken).ConfigureAwait(false);
                if (reply.AsSpan().SequenceEqual("brom"u8))
                {
                    _xflashConnectionAgent = MtkXFlashConnectionAgent.BootRom;
                    return MtkXFlashConnectionAgent.BootRom;
                }
                if (reply.AsSpan().SequenceEqual("preloader"u8))
                {
                    _xflashConnectionAgent = MtkXFlashConnectionAgent.Preloader;
                    return MtkXFlashConnectionAgent.Preloader;
                }
                throw new MtkProtocolException("Unrecognized XFlash connection-agent reply.");
            }
            catch
            {
                _xflash = null;
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Initializes an explicitly selected XFlash DA1 after its bootloader jump.
    /// This does not initialize external RAM, upload DA2 or make storage ready.
    /// <para>在 bootloader 跳转之后初始化显式选定的 XFlash DA1。本方法不会初始化
    /// 外部 RAM、上传 DA2 或使存储就绪。</para>
    /// </summary>
    /// <param name="logLevel">The DA UART logging level sent unchanged to the selected agent.
    /// <para>原样发送给所选 agent 的 DA UART 日志级别。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the environment and hardware setup are acknowledged
    /// and DA1 confirms synchronization.
    /// <para>在环境与硬件设置被确认、且 DA1 确认同步后返回的异步任务。</para></returns>
    /// <exception cref="InvalidOperationException">The agent has not been started or the session is disposed.
    /// <para>agent 尚未启动或会话已被释放。</para></exception>
    /// <exception cref="MtkProtocolException">The startup marker, a status or the synchronization word is wrong;
    /// the session faults.
    /// <para>启动标记、某个状态或同步字不正确；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task InitializeXFlashAsync(uint logLevel = 2, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.AgentStarted);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var marker = await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false);
                if (marker[0] != 0xC0)
                {
                    throw new MtkProtocolException("The selected agent did not return the XFlash startup marker.");
                }

                var channel = new MtkXFlashChannel(Transport, _timeoutMs);
                await SendXFlashWordAsync(channel, XFlashSync, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0x010100, cancellationToken).ConfigureAwait(false);
                var environment = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(environment, logLevel);
                BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(8), OperatingSystem.IsWindows() ? 0u : 1u);
                await channel.SendAsync(1, environment, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "environment setup", cancellationToken).ConfigureAwait(false);

                await SendXFlashWordAsync(channel, 0x010101, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "hardware setup", cancellationToken).ConfigureAwait(false);
                var sync = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (sync.DataType != 1 || sync.Payload.Length != 4
                    || BinaryPrimitives.ReadUInt32LittleEndian(sync.Payload) != XFlashSync)
                {
                    throw new MtkProtocolException("XFlash DA1 did not confirm synchronization.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xflash = channel;
                _state = MtkSessionState.XFlashStageOneReady;
            }
            catch
            {
                _xflash = null;
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task SendXFlashWordAsync(MtkXFlashChannel channel, uint value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        await channel.SendAsync(1, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadXFlashStatusAsync(MtkXFlashChannel channel, string operation, CancellationToken cancellationToken,
        bool allowSync = false)
    {
        var status = await ReadXFlashValueAsync(channel, operation, cancellationToken).ConfigureAwait(false);
        if (status != 0 && status != 0xFEEEEEEF && !(allowSync && status == XFlashSync))
        {
            throw new MtkProtocolException($"XFlash rejected {operation} (0x{status:X8}).", status);
        }
    }

    private async Task<uint> ReadXFlashValueAsync(MtkXFlashChannel channel, string operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var frame = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame.DataType != 1 || frame.Payload.Length is not (2 or 4))
        {
            throw new MtkProtocolException($"Invalid XFlash status frame during {operation}.");
        }
        return frame.Payload.Length == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload)
            : BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
    }
}