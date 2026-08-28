namespace UotanStudio.Models;

/// <summary>
/// Provides a built-in USB Vendor ID (VID) to vendor name lookup for the USB devices
/// most commonly encountered by the toolbox: Android OEMs, SoC vendors (Qualcomm /
/// MediaTek / Rockchip / UNISOC / Allwinner / Amlogic) and the USB gadget / serial
/// bridge chips used for ADB, Fastboot, EDL and serial debugging.
/// <para>提供内置的 USB 厂商 ID (VID) 到厂商名查找，覆盖工具箱最常见的 USB 设备：
/// Android OEM、SoC 厂商（Qualcomm / MediaTek / Rockchip / 紫光展锐 / 全志 / 晶晨）
/// 以及用于 ADB、Fastboot、EDL 与串口调试的 USB gadget / 串口桥芯片。</para>
/// </summary>
/// <remarks>
/// <para>该表仅收录高频出现的厂商，并非完整 USB ID 数据库；未命中的 VID 返回空。</para>
/// This table only includes frequently seen vendors and is not a complete USB ID
/// database; an unmatched VID returns empty.
/// </remarks>
public static class UsbVendorTable
{
    private static readonly Dictionary<ushort, string> _vendors = new()
    {
        // Android OEMs
        [0x04E8] = "Samsung",
        [0x18D1] = "Google",
        [0x22B8] = "Motorola",
        [0x0BB4] = "HTC",
        [0x0FCE] = "Sony",
        [0x1004] = "LG",
        [0x2A70] = "OnePlus",
        [0x2717] = "Xiaomi",
        [0x19D2] = "ZTE",
        [0x12D1] = "Huawei",
        [0x2B4C] = "vivo",
        [0x22D9] = "OPPO",
        [0x2DE3] = "HMD Global",
        [0x0421] = "Nokia",

        // SoC vendors (EDL / bootrom / RKDL 常见)
        [0x05C6] = "Qualcomm",
        [0x1782] = "MediaTek",
        [0x0E8D] = "MediaTek",
        [0x2207] = "Rockchip",
        [0x29A5] = "UNISOC",
        [0x1F3A] = "Allwinner",
        [0x2F66] = "Allwinner",
        [0x1B8E] = "Amlogic",
        [0x2483] = "Actions",

        // USB gadget / host bridges (ADB gadget, fastbootd, serial)
        [0x1D6B] = "Linux Foundation",

        // Common USB-serial bridge chips (Serial 模式)
        [0x10C4] = "Silicon Labs",
        [0x1A86] = "QinHeng",
        [0x0403] = "FTDI",
        [0x067B] = "Prolific",
        [0x0483] = "STMicroelectronics",
    };

    /// <summary>
    /// Tries to look up the vendor name for the given USB Vendor ID.
    /// <para>尝试按 USB 厂商 ID 查找厂商名。</para>
    /// </summary>
    /// <param name="vendorId">The USB Vendor ID. <para>USB 厂商 ID。</para></param>
    /// <returns>
    /// The vendor name if known; otherwise <see cref="string.Empty"/>.
    /// <para>已知时返回厂商名；否则返回 <see cref="string.Empty"/>。</para>
    /// </returns>
    public static string TryGetVendor(ushort vendorId)
        => _vendors.TryGetValue(vendorId, out var name) ? name : string.Empty;
}