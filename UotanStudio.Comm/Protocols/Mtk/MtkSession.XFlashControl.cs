using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Boot mode flag triple the reference sends with the meta command.
/// <para>参考随 meta 命令发送的启动模式标志三元组。</para></summary>
/// <remarks>
/// The values reproduce <c>MtkBootModeFlag</c> of Library/DA/xflash/xflash_lib.py:208-232: the boot
/// mode byte (0 normal, 1 meta), the connection type byte (0 unknown, 1 UART, 2 USB) and the
/// always-zero interface byte.
/// 取值复现 Library/DA/xflash/xflash_lib.py:208-232 的 <c>MtkBootModeFlag</c>：启动模式字节
/// （0 正常，1 meta）、连接类型字节（0 未知，1 UART，2 USB）与恒为零的接口字节。
/// </remarks>
public enum MtkXFlashMetaBootMode
{
    /// <summary>Normal boot, unknown connection type (reference <c>off</c>).
    /// <para>正常启动，未知连接类型。</para></summary>
    Off,
    /// <summary>Meta boot over UART (reference <c>uart</c>).
    /// <para>经 UART 的 meta 启动。</para></summary>
    Uart,
    /// <summary>Meta boot over USB (reference <c>usb</c>).
    /// <para>经 USB 的 meta 启动。</para></summary>
    Usb
}

/// <summary>
/// Device-control write commands of the XFlash download agent: the meta boot mode and eMMC
/// hardware-reset-pin selectors the reference documents, plus the parameterless download-info and
/// hardware-reset-pin disable markers.
/// <para>XFlash 下载代理的器件控制写入命令：参考记载的 meta 启动模式与 eMMC 硬件复位引脚选择器，
/// 以及无参数的下载信息与硬件复位引脚禁用标记。</para>
/// </summary>
/// <remarks>
/// <para>Every SET exchange mirrors <c>send_devctrl</c> with a parameter
/// (Library/DA/xflash/xflash_lib.py:190-206) and <c>send_param</c> (:163-182): device-control
/// selector, one status, the control code, one status, the parameter packet with the shared frame
/// magic, and one completion status. The parameterless 0x0800xx markers follow the established
/// parameterless device-control exchange of this session — the reference defines their codes
/// (Library/DA/xflash/xflash_param.py:60-62) but keeps every call commented out
/// (Library/DA/xflash/xflash_lib.py:856), so no reply layout is invented beyond the shared framing.</para>
/// <para>每个 SET 交互对应带参数的 <c>send_devctrl</c>（Library/DA/xflash/xflash_lib.py:190-206）与
/// <c>send_param</c>（:163-182）：器件控制选择器、一个状态、控制码、一个状态、携带共享帧魔数的参数包，
/// 以及一个完成状态。无参数的 0x0800xx 标记遵循本会话已确立的无参数器件控制交互——参考只定义了它们的
/// 控制码（Library/DA/xflash/xflash_param.py:60-62），所有调用都处于注释状态
/// （Library/DA/xflash/xflash_lib.py:856），因此除共享帧结构外不发明任何回复布局。</para>
/// </remarks>
public sealed partial class MtkSession
{
    private const uint XFlashSetMetaBootMode = 0x020006;
    private const uint XFlashSetEmmcHwResetPin = 0x020007;
    private const uint XFlashStartDlInfo = 0x080001;
    private const uint XFlashEndDlInfo = 0x080002;
    private const uint XFlashDisableEmmcHwResetPin = 0x080004;

    /// <summary>Sets the meta boot mode flag triple through the documented control code.
    /// <para>通过已记载的控制码设置 meta 启动模式标志三元组。</para></summary>
    /// <remarks>
    /// The payload reproduces <c>set_meta</c> (Library/DA/xflash/xflash_lib.py:206-239): three bytes
    /// of boot mode, connection type and interface id, where UART and USB select meta boot and
    /// <see cref="MtkXFlashMetaBootMode.Off"/> restores the normal boot flags.
    /// 载荷复现 <c>set_meta</c>（Library/DA/xflash/xflash_lib.py:206-239）：启动模式、连接类型与接口 id
    /// 三个字节，UART 与 USB 选择 meta 启动，<see cref="MtkXFlashMetaBootMode.Off"/> 恢复正常启动标志。
    /// </remarks>
    /// <param name="mode">The boot mode to request. 要请求的启动模式。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the agent acknowledged the flag triple.
    /// <para>在代理确认标志三元组后完成的任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined boot
    /// mode; no byte was sent. <para><paramref name="mode"/> 不是已定义的启动模式；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected; the XFlash family faults.
    /// <para>某状态被拒绝；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task SetXFlashMetaBootModeAsync(MtkXFlashMetaBootMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var bootMode = mode == MtkXFlashMetaBootMode.Off ? (byte)0 : (byte)1;
        var connectionType = mode switch
        {
            MtkXFlashMetaBootMode.Off => (byte)0,
            MtkXFlashMetaBootMode.Uart => (byte)1,
            _ => (byte)2
        };
        return SetXFlashDeviceControlParameterAsync(XFlashSetMetaBootMode,
            [bootMode, connectionType, 0], "xflash-set-meta", $"Meta boot mode {mode}.", cancellationToken);
    }

    /// <summary>Enables or disables the eMMC hardware reset pin through the documented control code.
    /// <para>通过已记载的控制码启用或禁用 eMMC 硬件复位引脚。</para></summary>
    /// <remarks>
    /// The reference defines the control code (Library/DA/xflash/xflash_param.py:31) without any call
    /// site, so the payload follows the SET-family little-endian word convention with no further
    /// layout invented.
    /// 参考只定义了该控制码（Library/DA/xflash/xflash_param.py:31），没有任何调用点，因此载荷遵循 SET 家族
    /// 的小端字约定，不再发明其他布局。
    /// </remarks>
    /// <param name="enable"><see langword="true"/> enables the hardware reset pin.
    /// <para><see langword="true"/> 启用硬件复位引脚。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the agent acknowledged the flag.
    /// <para>在代理确认该标志后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected; the XFlash family faults.
    /// <para>某状态被拒绝；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task SetXFlashEmmcHwResetPinAsync(bool enable, CancellationToken cancellationToken = default) =>
        SetXFlashDeviceControlWordAsync(XFlashSetEmmcHwResetPin, enable ? 1u : 0u, "xflash-set-emmc-hwreset-pin",
            $"eMMC hardware reset pin {(enable ? "enabled" : "disabled")}.", cancellationToken);

    /// <summary>Marks the start of one download through the documented 0x080001 marker.
    /// <para>通过已记载的 0x080001 标记声明一次下载开始。</para></summary>
    /// <remarks>
    /// The reference keeps its only call commented out (Library/DA/xflash/xflash_lib.py:856), so this
    /// path follows the established parameterless device-control exchange and keeps the reply bytes
    /// opaque; the marker changes no storage content by itself.
    /// 参考唯一的调用处于注释状态（Library/DA/xflash/xflash_lib.py:856），因此本路径遵循已确立的无参数器件
    /// 控制交互并把回复字节保持为不透明内容；该标记本身不改变任何存储内容。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact reply bytes. 精确的回复字节。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected or the reply is malformed; the
    /// XFlash family faults. <para>某状态被拒绝或回复格式错误；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<byte[]> StartXFlashDownloadInfoAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashStartDlInfo, MaximumXFlashOpaqueDeviceControlBytes,
            "xflash-start-dl-info", reply => reply, reply => $"{reply.Length} opaque bytes", cancellationToken);

    /// <summary>Marks the end of one download through the documented 0x080002 marker.
    /// <para>通过已记载的 0x080002 标记声明一次下载结束。</para></summary>
    /// <remarks>
    /// The reference defines the code without a call site, so this path follows the established
    /// parameterless device-control exchange and keeps the reply bytes opaque.
    /// 参考只定义了该控制码而没有调用点，因此本路径遵循已确立的无参数器件控制交互，并把回复字节保持为
    /// 不透明内容。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact reply bytes. 精确的回复字节。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected or the reply is malformed; the
    /// XFlash family faults. <para>某状态被拒绝或回复格式错误；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<byte[]> EndXFlashDownloadInfoAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashEndDlInfo, MaximumXFlashOpaqueDeviceControlBytes,
            "xflash-end-dl-info", reply => reply, reply => $"{reply.Length} opaque bytes", cancellationToken);

    /// <summary>Disables the eMMC hardware reset pin through the documented 0x080004 marker.
    /// <para>通过已记载的 0x080004 标记禁用 eMMC 硬件复位引脚。</para></summary>
    /// <remarks>
    /// The reference defines the code without a call site (Library/DA/xflash/xflash_param.py:62), so
    /// this path follows the established parameterless device-control exchange and keeps the reply
    /// bytes opaque.
    /// 参考只定义了该控制码而没有调用点（Library/DA/xflash/xflash_param.py:62），因此本路径遵循已确立的
    /// 无参数器件控制交互，并把回复字节保持为不透明内容。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact reply bytes. 精确的回复字节。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected or the reply is malformed; the
    /// XFlash family faults. <para>某状态被拒绝或回复格式错误；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<byte[]> DisableXFlashEmmcHwResetPinAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashDisableEmmcHwResetPin, MaximumXFlashOpaqueDeviceControlBytes,
            "xflash-disable-emmc-hwreset-pin", reply => reply, reply => $"{reply.Length} opaque bytes",
            cancellationToken);

    /// <summary>Sends one SET device-control exchange carrying a little-endian word parameter.
    /// <para>发送一个携带小端字参数的 SET 器件控制交互。</para></summary>
    /// <param name="controlCode">The SET control code. SET 控制码。</param>
    /// <param name="value">The parameter word. 参数字。</param>
    /// <param name="evidencePhase">The neutral evidence phase name. 与本地化无关的证据阶段名。</param>
    /// <param name="describe">The neutral completion description. 与本地化无关的完成描述。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the completion status was accepted.
    /// <para>在完成状态被接受后完成的任务。</para></returns>
    private Task SetXFlashDeviceControlWordAsync(uint controlCode, uint value, string evidencePhase,
        string describe, CancellationToken cancellationToken)
    {
        var parameter = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(parameter, value);
        return SetXFlashDeviceControlParameterAsync(controlCode, parameter, evidencePhase, describe,
            cancellationToken);
    }

    /// <summary>Sends one SET device-control exchange: selector, control code, parameter packet and
    /// completion status. <para>发送一个 SET 器件控制交互：选择器、控制码、参数包与完成状态。</para></summary>
    /// <param name="controlCode">The SET control code. SET 控制码。</param>
    /// <param name="parameter">The parameter packet payload. 参数包载荷。</param>
    /// <param name="evidencePhase">The neutral evidence phase name. 与本地化无关的证据阶段名。</param>
    /// <param name="describe">The neutral completion description. 与本地化无关的完成描述。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the completion status was accepted.
    /// <para>在完成状态被接受后完成的任务。</para></returns>
    private async Task SetXFlashDeviceControlParameterAsync(uint controlCode, byte[] parameter,
        string evidencePhase, string describe, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireXFlashSecurityControlStage();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SendXFlashSecurityControlCodeAsync(channel, controlCode, cancellationToken)
                    .ConfigureAwait(false);
                await channel.SendAsync(1, parameter, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, $"device-control write 0x{controlCode:X8}", cancellationToken)
                    .ConfigureAwait(false);
                RecordSecurityEvidence(evidencePhase, CommandOutcomeKind.Completed,
                    $"Control code 0x{controlCode:X8}: {describe}", Convert.ToHexString(parameter));
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
}