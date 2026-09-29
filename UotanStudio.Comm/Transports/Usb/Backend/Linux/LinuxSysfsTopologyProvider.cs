using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

/// <summary>
/// <para>Linux 拓扑后端：读取 sysfs 的 USB 设备与接口目录，提供已绑定内核驱动与主机控制器祖先链。
/// sysfs 没有设备接口类 GUID 与 PnP 故障码概念。枚举与塑形都在
/// <see cref="LinuxSysfsTopology"/>，本类只负责默认根目录、可用性判断与实例入口。</para>
/// The Linux topology backend: reads the sysfs USB device and interface directories and reports the
/// bound kernel driver plus the host-controller ancestor chain. sysfs has no notion of device-interface
/// class GUIDs or PnP problem codes. Enumeration and shaping live in <see cref="LinuxSysfsTopology"/>;
/// this type only supplies the default root, the availability check and the shared instance.
/// </summary>
internal sealed class LinuxSysfsTopologyProvider : IUsbTopologyProvider
{
    /// <summary>The shared instance. <para>共享实例。</para></summary>
    public static LinuxSysfsTopologyProvider Instance { get; } = new();

    /// <inheritdoc />
    public UsbTopologyCapabilities Capabilities { get; } = new()
    {
        Backend = UsbTopologyBackendKind.LinuxSysfs,
        SupportsDriverInfo = true,
        SupportsInterfaceNodes = true,
        SupportsHostControllerNodes = true,
        SupportsDeviceInterfaceGuids = false,
        SupportsProblemCodes = false
    };

    /// <inheritdoc />
    public bool IsAvailable =>
        OperatingSystem.IsLinux() && Directory.Exists(LinuxSysfsTopology.DefaultDevicesRoot);

    /// <inheritdoc />
    public IReadOnlyList<UsbTopologyNode> ReadFlatNodes() =>
        LinuxSysfsTopology.ReadFlatNodes(LinuxSysfsTopology.DefaultDevicesRoot);
}