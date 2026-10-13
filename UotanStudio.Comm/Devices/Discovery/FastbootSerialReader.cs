using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Reads a fastboot device's serial during discovery via one briefly claimed
/// <c>getvar serialno</c> probe.
/// <para>发现阶段通过一次短暂认领接口的 <c>getvar serialno</c> 探测读取 fastboot 设备的序列号。</para>
/// Any failure yields <c>null</c> and is reported as an error line, keeping discovery best-effort.
/// <para>任何失败都返回 <c>null</c> 并作为错误行上报，使发现过程保持尽力而为。</para>
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
    /// <returns>The usable serial, or <c>null</c> when no session matches, the device is not a
    /// fastboot interface, or the serial is malformed.
    /// <para>可用的序列号；无会话匹配、设备非 fastboot 接口或序列号畸形时返回 <c>null</c>。</para></returns>
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