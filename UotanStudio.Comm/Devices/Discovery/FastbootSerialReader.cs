using UotanStudio.Comm;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Reads a fastboot device's serial number during discovery when the USB descriptor did not expose one.
/// <para>在发现阶段，当 USB 描述符未提供序列号时，读取 fastboot 设备的序列号。</para>
/// This briefly claims the fastboot interface to issue a single <c>getvar serialno</c>; it never selects a
/// different device or route, and any failure yields <c>null</c> so discovery stays best-effort.
/// <para>它只会短暂认领 fastboot 接口以发起一次 <c>getvar serialno</c>；绝不选择其他设备或路由，
/// 任何失败都返回 <c>null</c>，使发现过程保持尽力而为。</para>
/// Failures that collapse into that <c>null</c> are reported as error lines through
/// <see cref="UsbTrace"/> so a silently lost probe (claim rejection, device busy, permission denied)
/// stays diagnosable while the caller-visible contract is unchanged.
/// <para>折叠为该 <c>null</c> 的失败会作为错误行经 <see cref="UsbTrace"/> 上报，使被静默丢弃的探测
/// （认领被拒、设备繁忙、权限不足）保持可诊断，而对调用方可见的返回契约不变。</para>
/// </summary>
internal static class FastbootSerialReader
{
    /// <summary>
    /// Probes one discovered device for its fastboot serial number.
    /// <para>探测单个已发现设备的 fastboot 序列号。</para>
    /// </summary>
    /// <param name="kit">The communication kit used to open the USB session by device key.
    /// <para>用于按设备键打开 USB 会话的通信套件。</para></param>
    /// <param name="device">The discovered USB device descriptor. <para>已发现的 USB 设备描述符。</para></param>
    /// <param name="failureSink">
    /// Optional diagnostics sink that receives the probe-failure line verbatim; when <see langword="null"/>,
    /// the failure is reported through <see cref="UsbTrace.LogError(string)"/> instead.
    /// <para>可选的诊断汇，逐字接收探测失败行；为 <see langword="null"/> 时失败改由
    /// <see cref="UsbTrace.LogError(string)"/> 上报。</para>
    /// </param>
    /// <returns>The usable serial number, or <c>null</c> when no session matches the key, the device is
    /// not a fastboot interface, the probe threw, or the reported serial is malformed.
    /// <para>可用的序列号；无会话匹配该键、设备非 fastboot 接口、探测抛出异常或上报序列号畸形时返回 <c>null</c>。</para></returns>
    internal static string? Read(ICommKit kit, UsbDeviceInfo device, IUsbLogger? failureSink = null)
    {
        ArgumentNullException.ThrowIfNull(kit);
        ArgumentNullException.ThrowIfNull(device);
        IUsbDeviceSession? session = null;
        FastbootSession? fastboot = null;
        try
        {
            session = kit.OpenUsbDeviceSessionByKey(device.DeviceKey, device.SourceApiKind);
            if (session is null || !new FastbootFactory(2000).TryCreate(session, out var protocol) ||
                protocol is not FastbootSession created)
            {
                return null;
            }

            fastboot = created;
            var serial = fastboot.GetVariable("serialno", useCache: false).Trim();
            return IsUsable(serial) ? serial : null;
        }
        catch (Exception ex)
        {
            ReportProbeFailure(device, ex, failureSink);
            return null;
        }
        finally
        {
            fastboot?.Dispose();
            if (fastboot is null) session?.Dispose();
        }
    }

    /// <summary>
    /// Turns a probe failure that collapsed into a <c>null</c> return into a diagnosable error line.
    /// <para>把已折叠为 <c>null</c> 返回值的探测失败转成可诊断的错误行。</para>
    /// </summary>
    /// <param name="device">The device whose probe failed. <para>探测失败的设备。</para></param>
    /// <param name="ex">The exception that was swallowed by the best-effort contract.
    /// <para>被尽力而为契约吞掉的异常。</para></param>
    /// <param name="failureSink">Optional verbatim diagnostics sink; see <see cref="Read(ICommKit,UsbDeviceInfo,IUsbLogger?)"/>.
    /// <para>可选的逐字诊断汇；参见 <see cref="Read(ICommKit,UsbDeviceInfo,IUsbLogger?)"/>。</para></param>
    /// <remarks>
    /// The sink branch re-emits the exact <c>[ERROR] </c> prefix that <see cref="UsbTrace.LogError(string)"/>
    /// prepends, so an injected sink observes the identical text the production route would write and the
    /// diagnostics format stays pinned by tests without touching the global <see cref="UsbTrace.Logger"/>.
    /// <para>诊断汇分支会复刻 <see cref="UsbTrace.LogError(string)"/> 添加的 <c>[ERROR] </c> 前缀，
    /// 使注入的诊断汇观测到与生产路径逐字相同的文本，从而由测试钉住诊断格式且无需触碰全局的
    /// <see cref="UsbTrace.Logger"/>。</para>
    /// </remarks>
    private static void ReportProbeFailure(UsbDeviceInfo device, Exception ex, IUsbLogger? failureSink)
    {
        var message = $"Fastboot serialno probe failed for device {device.DeviceKey} (api {device.SourceApiKind}): {ex.GetType().Name}: {ex.Message}";
        if (failureSink is { } sink)
        {
            sink.Log($"[ERROR] {message}");
            return;
        }

        UsbTrace.LogError(message);
    }

    private static bool IsUsable(string serial) =>
        serial.Length is > 0 and <= 128 &&
        serial.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}