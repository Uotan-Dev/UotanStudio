using UotanStudio.Comm;
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
/// </summary>
internal static class FastbootSerialReader
{
    internal static string? Read(ICommKit kit, UsbDeviceInfo device)
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
        catch (Exception)
        {
            return null;
        }
        finally
        {
            fastboot?.Dispose();
            if (fastboot is null) session?.Dispose();
        }
    }

    private static bool IsUsable(string serial) =>
        serial.Length is > 0 and <= 128 &&
        serial.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}