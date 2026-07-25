using System.Collections.Generic;

namespace UotanStudio.Abstractions.Devices;

/// <summary>
/// <para>设备能力描述，记录已连接设备支持的相关特性，UI 可据此动态调整。</para>
/// Device capability descriptor, recording features supported by the connected device; the UI adjusts dynamically based on this.
/// </summary>
public sealed class DeviceCapabilities
{
    /// <summary>
    /// <para>支持的协议集合。</para>
    /// Set of supported protocols.
    /// </summary>
    public HashSet<DeviceProtocol> SupportedProtocols { get; } = new();

    /// <summary>
    /// <para>是否支持 ADB shell。</para>
    /// Whether ADB shell is supported.
    /// </summary>
    public bool SupportsAdbShell { get; set; }

    /// <summary>
    /// <para>是否支持 fastboot 刷写。</para>
    /// Whether fastboot flashing is supported.
    /// </summary>
    public bool SupportsFastbootFlash { get; set; }

    /// <summary>
    /// <para>是否支持 OTA sideload。</para>
    /// Whether OTA sideload is supported.
    /// </summary>
    public bool SupportsOtaSideload { get; set; }

    /// <summary>
    /// <para>是否支持 A/B 双槽位。</para>
    /// Whether A/B dual-slot is supported.
    /// </summary>
    public bool SupportsDualSlot { get; set; }

    /// <summary>
    /// <para>是否支持分区擦除。</para>
    /// Whether partition erase is supported.
    /// </summary>
    public bool SupportsPartitionErase { get; set; }

    /// <summary>
    /// <para>是否支持 boot/recovery 重启切换。</para>
    /// Whether boot/recovery reboot switching is supported.
    /// </summary>
    public bool SupportsRebootModes { get; set; }

    /// <summary>
    /// <para>是否需要解锁 bootloader。</para>
    /// Whether bootloader unlock is required.
    /// </summary>
    public bool RequiresUnlock { get; set; }

    /// <summary>
    /// <para>是否已解锁 bootloader。</para>
    /// Whether the bootloader is unlocked.
    /// </summary>
    public bool IsBootloaderUnlocked { get; set; }

    /// <summary>
    /// <para>是否支持 EDL 模式。</para>
    /// Whether EDL mode is supported.
    /// </summary>
    public bool SupportsEdl { get; set; }

    /// <summary>
    /// <para>是否支持 MTK Brom 下载。</para>
    /// Whether MTK Brom download is supported.
    /// </summary>
    public bool SupportsMtkBrom { get; set; }

    /// <summary>
    /// <para>是否支持 Rockchip 升级协议。</para>
    /// Whether Rockchip upgrade protocol is supported.
    /// </summary>
    public bool SupportsRkUpgrade { get; set; }

    /// <summary>
    /// <para>已识别的分区名集合。</para>
    /// Set of recognized partition names.
    /// </summary>
    public HashSet<string> KnownPartitions { get; } = new();

    /// <summary>
    /// <para>支持的固件镜像格式（如 <c>raw</c>、<c>sparse</c>、<c>erofs</c>）。</para>
    /// Supported firmware image formats (e.g. <c>raw</c>, <c>sparse</c>, <c>erofs</c>).
    /// </summary>
    public HashSet<string> SupportedImageFormats { get; } = new();
}
