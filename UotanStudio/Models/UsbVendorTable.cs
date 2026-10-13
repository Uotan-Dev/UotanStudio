using System.Collections.Frozen;

namespace UotanStudio.Models;

/// <summary>
/// Provides a built-in USB Vendor ID (VID) to vendor name lookup for Android OEMs, SoC vendors,
/// and the gadget or serial bridge chips seen in ADB, Fastboot, EDL and serial debugging.
/// <para>提供内置的 USB 厂商 ID（VID）到厂商名查找，覆盖 Android OEM、SoC 厂商，
/// 以及 ADB、Fastboot、EDL 与串口调试中常见的 USB gadget / 串口桥芯片。</para>
/// </summary>
public static class UsbVendorTable
{
    private static readonly FrozenDictionary<ushort, string> _vendors = new Dictionary<ushort, string>
    {
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

        [0x05C6] = "Qualcomm",
        [0x1782] = "MediaTek",
        [0x0E8D] = "MediaTek",
        [0x2207] = "Rockchip",
        [0x29A5] = "UNISOC",
        [0x1F3A] = "Allwinner",
        [0x2F66] = "Allwinner",
        [0x1B8E] = "Amlogic",
        [0x2483] = "Actions",

        [0x1D6B] = "Linux Foundation",

        [0x10C4] = "Silicon Labs",
        [0x1A86] = "QinHeng",
        [0x0403] = "FTDI",
        [0x067B] = "Prolific",
        [0x0483] = "STMicroelectronics",
    }.ToFrozenDictionary();

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