using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>USB link speeds the XFlash download agent can report.
/// <para>XFlash 下载代理可能上报的 USB 链路速度。</para></summary>
public enum MtkXFlashUsbSpeedKind
{
    /// <summary>Full-speed operation; the client reconnects after raising it.
    /// <para>全速运行；本客户端会在提速后重连。</para></summary>
    FullSpeed,
    /// <summary>High-speed operation.
    /// <para>高速运行。</para></summary>
    HighSpeed,
    /// <summary>Super-speed operation.
    /// <para>超高速运行。</para></summary>
    SuperSpeed
}

/// <summary>The USB speed the XFlash agent reports after a speed switch.
/// <para>XFlash 代理在速度切换后上报的 USB 速度。</para></summary>
/// <param name="Kind">The decoded speed. <para>解码后的速度。</para></param>
/// <param name="Text">The exact ASCII speed token returned by the agent.
/// <para>代理返回的精确 ASCII 速度标记。</para></param>
public sealed record MtkXFlashUsbSpeed(MtkXFlashUsbSpeedKind Kind, string Text);

public sealed partial class MtkSession
{
    private const uint XFlashTargetSystemOsLinux = 1;

    private const uint XFlashSync = 0x434E5953;
    private const uint XFlashSwitchUsbSpeed = 0x01000B;
    private const uint XFlashUsbSpeedVendorMagic = 0x0E8D2001;
    private const uint XFlashGetUsbSpeed = 0x04000B;

    /// <summary>The download-agent status that reports an unsupported device-control code.
    /// <para>下载代理上报"设备控制码不受支持"的状态。</para></summary>
    /// <remarks>
    /// The vendor tool treats exactly this status as "the query does not apply here" and continues, while
    /// every other failure aborts. Keeping the value named stops the SLA probe from turning an older agent
    /// into a dead session.
    /// <para>厂商工具只把这一状态当作"该查询在此不适用"并继续，其余失败一律中止。为该取值命名，
    /// 可避免 SLA 探测把较旧的代理变成失效会话。</para>
    /// </remarks>
    private const uint XFlashUnsupportedControlCode = 0xC0010004;
    private MtkXFlashChannel? _xflash;
    private MtkXFlashConnectionAgent? _xflashConnectionAgent;
    private bool _xflashExternalRamInitialized;

    /// <summary>Queries whether DA1 was entered through Boot ROM or an initialized preloader.
    /// <para>查询 DA1 是通过 Boot ROM 还是已初始化的 preloader 进入的。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The exact recognized connection agent; unknown replies are rejected.
    /// <para>精确识别的连接 agent；未知回复会被拒绝。</para></returns>
    /// <exception cref="InvalidOperationException">DA1 is not stage-one ready or the channel is gone.
    /// <para>DA1 未处于 stage-one ready 状态或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">The reply was not exactly <c>brom</c> or <c>preloader</c>, or a control frame was malformed; the session faults.
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

    /// <summary>Initializes an explicitly selected XFlash DA1 after its bootloader jump. This does not initialize external RAM, upload DA2 or make storage ready.
    /// <para>在 bootloader 跳转之后初始化显式选定的 XFlash DA1。本方法不会初始化 外部 RAM、上传 DA2 或使存储就绪。</para></summary>
    /// <param name="logLevel">The DA UART logging level sent unchanged to the selected agent.
    /// <para>原样发送给所选 agent 的 DA UART 日志级别。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the environment and hardware setup are acknowledged and DA1 confirms synchronization.
    /// <para>在环境与硬件设置被确认、且 DA1 确认同步后返回的异步任务。</para></returns>
    /// <exception cref="InvalidOperationException">The agent has not been started or the session is disposed.
    /// <para>agent 尚未启动或会话已被释放。</para></exception>
    /// <exception cref="MtkProtocolException">The startup marker, a status or the synchronization word is wrong; the session faults.
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
                BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(8), XFlashTargetSystemOsLinux);
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

    /// <summary>Switches the XFlash agent to a higher USB speed and reports the speed it applies.
    /// <para>将 XFlash 代理切换到更高的 USB 速度，并报告其实际生效的速度。</para></summary>
    /// <remarks>
    /// The switch sends the <c>SWITCH_USB_SPEED</c> code as a bare protocol word, each send is followed by
    /// a status, and the second frame carries the <c>0x0E8D2001</c> vendor magic. This path then issues
    /// the documented <c>GET_USB_SPEED</c> device-control query so the caller receives the speed token the
    /// agent actually applies. The exchange belongs to the stage-two connection flow, so it is refused
    /// before stage two.
    /// <para>切换过程把 <c>SWITCH_USB_SPEED</c> 码作为裸协议字发送，每次发送后读取状态，第二帧携带
    /// <c>0x0E8D2001</c> 厂商魔数。本路径随后发起已记载的 <c>GET_USB_SPEED</c> 器件控制查询，让调用方拿到
    /// 代理实际应用的速度标记。该交换属于二阶段连接流程，因此在阶段一之前会被拒绝。</para>
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The decoded speed together with the exact agent token.
    /// <para>解码后的速度以及代理返回的精确标记。</para></returns>
    /// <exception cref="InvalidOperationException">DA2 has not started, the transport is disconnected
    /// or the channel is gone. <para>DA2 未启动、传输已断开或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected or the speed token is unknown;
    /// the XFlash family faults. <para>某状态被拒绝或速度标记未知；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkXFlashUsbSpeed> SetXFlashUsbSpeedAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXFlashSecurityCustomStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SendXFlashWordAsync(channel, XFlashSwitchUsbSpeed, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "USB speed switch", cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, XFlashUsbSpeedVendorMagic, cancellationToken)
                    .ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "USB speed vendor identifier", cancellationToken)
                    .ConfigureAwait(false);
                var reply = await QueryXFlashControlAsync(channel, XFlashGetUsbSpeed, cancellationToken, 64)
                    .ConfigureAwait(false);
                var speed = DecodeXFlashUsbSpeed(reply);
                if (speed.Kind != MtkXFlashUsbSpeedKind.FullSpeed)
                {
                    // The agent has already re-enumerated at the new rate, so the open handle still talks at
                    // the old one. The host has to reopen the device and restart the agent channel; when no
                    // reopener is available the speed simply stays as reported instead of being claimed.
                    // 代理已按新速率重新枚举，而当前句柄仍以旧速率通信。主机必须重新打开设备并重建代理通道；
                    // 没有重开器时保持已报告的速度，而不是宣称切换成功。
                    if (!await TryReopenXFlashChannelAsync(speed, cancellationToken).ConfigureAwait(false))
                    {
                        return speed;
                    }
                }

                RecordSecurityEvidence("xflash-usb-speed", CommandOutcomeKind.Completed,
                    $"The agent switched USB speed and reports {speed.Text}.", Convert.ToHexString(reply));
                return speed;
            }
            catch
            {
                FaultXFlashSecurity();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Reopens the transport after the agent re-enumerated at a higher USB rate and rebuilds the agent
    /// channel on it.
    /// <para>在代理以更高 USB 速率重新枚举之后重开传输，并在其上重建代理通道。</para>
    /// </summary>
    /// <remarks>
    /// Failure is reported as <see langword="false"/> and restores the session to its previous stage so the
    /// caller keeps a usable full-speed session; it never leaves a half-switched channel behind.
    /// <para>失败时返回 <see langword="false"/> 并恢复会话到先前阶段，使调用方仍保有可用的全速会话；
    /// 不会留下半切换的通道。</para>
    /// </remarks>
    /// <param name="speed">The speed the agent reported before the reopen.
    /// <para>重开前代理报告的速度。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns><see langword="true"/> when a new transport and channel are in place.
    /// <para>新的传输与通道已就位时为 <see langword="true"/>。</para></returns>
    private async Task<bool> TryReopenXFlashChannelAsync(MtkXFlashUsbSpeed speed, CancellationToken cancellationToken)
    {
        if (_reopener is not { } reopener)
        {
            RecordSecurityEvidence("xflash-usb-speed", CommandOutcomeKind.Partial,
                $"The agent reports {speed.Text} but no transport reopener is attached, so the host keeps the " +
                "existing handle and the higher rate is not applied.");
            return false;
        }

        var previousStage = _state;
        ICommTransport? replacement = null;
        try
        {
            replacement = await reopener.ReopenAsync(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(replacement);
            if (!replacement.IsConnected)
            {
                throw new InvalidOperationException("The reopened transport is not connected.");
            }

            var previous = Transport;
            Transport = replacement;
            replacement = null;
            previous.Dispose();
            _xflash = new MtkXFlashChannel(Transport, _timeoutMs);
            _state = previousStage;
            RecordSecurityEvidence("xflash-usb-speed", CommandOutcomeKind.Completed,
                $"The host reopened the device and rebuilt the agent channel at {speed.Text}.");
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            replacement?.Dispose();
            _state = previousStage;
            RecordSecurityEvidence("xflash-usb-speed", CommandOutcomeKind.Partial,
                $"The host could not reopen the device after the {speed.Text} switch ({error.Message}); the " +
                "existing handle is kept and the higher rate is not applied.");
            return false;
        }
    }

    private static MtkXFlashUsbSpeed DecodeXFlashUsbSpeed(byte[] reply)
    {
        if (reply.AsSpan().SequenceEqual("full-speed"u8))
        {
            return new MtkXFlashUsbSpeed(MtkXFlashUsbSpeedKind.FullSpeed, "full-speed");
        }
        if (reply.AsSpan().SequenceEqual("high-speed"u8))
        {
            return new MtkXFlashUsbSpeed(MtkXFlashUsbSpeedKind.HighSpeed, "high-speed");
        }
        if (reply.AsSpan().SequenceEqual("super-speed"u8))
        {
            return new MtkXFlashUsbSpeed(MtkXFlashUsbSpeedKind.SuperSpeed, "super-speed");
        }
        throw new MtkProtocolException(
            $"Unrecognized XFlash USB speed reply ({reply.Length} bytes); expected full-speed, high-speed " +
            "or super-speed.");
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
        if (frame.DataType != 1 || frame.Payload.Length < 2 || frame.Payload.Length == 3)
        {
            throw new MtkProtocolException($"Invalid XFlash status frame during {operation}.");
        }
        if (frame.Payload.Length == 2)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
    }
}