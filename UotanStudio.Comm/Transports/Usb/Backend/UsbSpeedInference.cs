using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

/// <summary>
/// Shared USB speed inference helpers used by backends that do not report the negotiated
/// link speed directly (macOS, HarmonyOS, Linux fallback).
/// <para>供不直接报告协商链路速度的后端（macOS、HarmonyOS、Linux 回退）使用的共享
/// USB 速度推断工具。</para>
/// </summary>
internal static class UsbSpeedInference
{
    /// <summary>
    /// Approximates the USB speed from the device descriptor's BcdUSB version.
    /// <para>根据设备描述符的 BcdUSB 版本近似推断 USB 速度。</para>
    /// This reflects the device's declared USB spec version rather than the negotiated
    /// link speed; good enough for discovery hints (e.g. EDL USB3 vs USB2 paths).
    /// <para>反映设备声明的 USB 规范版本而非协商链路速度；作为发现提示足够
    /// （例如 EDL 区分 USB3/USB2 路径）。</para>
    /// </summary>
    /// <param name="bcdUsb">The BcdUSB field of the device descriptor. <para>设备描述符的 BcdUSB 字段。</para></param>
    /// <returns>The inferred speed. <para>推断出的速度。</para></returns>
    public static UsbDeviceSpeed FromBcdUsb(ushort bcdUsb)
    {
        return bcdUsb >= 0x0301
            ? UsbDeviceSpeed.SuperPlus
            : bcdUsb >= 0x0300 ? UsbDeviceSpeed.Super : bcdUsb >= 0x0200 ? UsbDeviceSpeed.High : UsbDeviceSpeed.Full;
    }

    /// <summary>
    /// Maps a negotiated link speed in Mbps (as reported by the sysfs <c>speed</c>
    /// attribute) to a <see cref="UsbDeviceSpeed"/>.
    /// <para>将协商链路速度（Mbps，由 sysfs <c>speed</c> 属性报告）映射到
    /// <see cref="UsbDeviceSpeed"/>。</para>
    /// </summary>
    /// <param name="mbps">The link speed in Mbps. <para>链路速度（Mbps）。</para></param>
    /// <returns>The mapped speed. <para>映射后的速度。</para></returns>
    public static UsbDeviceSpeed FromMbps(double mbps)
    {
        return mbps <= 2
            ? UsbDeviceSpeed.Low
            : mbps <= 12
            ? UsbDeviceSpeed.Full
            : mbps <= 480 ? UsbDeviceSpeed.High : mbps <= 5000 ? UsbDeviceSpeed.Super : UsbDeviceSpeed.SuperPlus;
    }
}