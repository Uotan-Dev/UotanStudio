using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// The macOS topology backend: walks the USB plane of the I/O Registry and reports the
/// controller/device/interface parentage together with the matched driver class. The I/O Registry has no
/// Windows-style device-interface class GUIDs or PnP problem codes. Enumeration and reading live in
/// <see cref="MacIoRegistryNative"/> and shaping in <see cref="MacIoRegistryTopology"/>; this type only
/// supplies the availability check, the parentage lookup and the shared instance.
/// <para>macOS 拓扑后端：遍历 I/O Registry 的 USB 平面，提供控制器/设备/接口父子链与已匹配的
/// 驱动类。I/O Registry 不暴露 Windows 意义上的设备接口类 GUID 或 PnP 故障码。枚举与读取都在
/// <see cref="MacIoRegistryNative"/>，塑形在 <see cref="MacIoRegistryTopology"/>，本类只负责
/// 可用性判断、父链查找与实例入口。</para>
/// </summary>
internal sealed class MacIoRegistryTopologyProvider : IUsbTopologyProvider
{
    /// <summary>The shared instance. <para>共享实例。</para></summary>
    public static MacIoRegistryTopologyProvider Instance { get; } = new();

    /// <inheritdoc />
    public UsbTopologyCapabilities Capabilities { get; } = new()
    {
        Backend = UsbTopologyBackendKind.MacOSIoRegistry,
        SupportsDriverInfo = true,
        SupportsInterfaceNodes = true,
        SupportsHostControllerNodes = true,
        SupportsDeviceInterfaceGuids = false,
        SupportsProblemCodes = false
    };

    /// <inheritdoc />
    public bool IsAvailable => MacIoRegistryNative.IsAvailable;

    /// <inheritdoc />
    public IReadOnlyList<UsbTopologyNode> ReadFlatNodes()
    {
        var bags = MacIoRegistryNative.Enumerate();
        if (bags.Count == 0)
        {
            return [];
        }

        var byId = new Dictionary<string, MacIoRegistryEntry>(StringComparer.Ordinal);
        foreach (var bag in bags)
        {
            byId[bag.InstanceId] = bag;
        }

        var nodes = new List<UsbTopologyNode>(bags.Count);
        foreach (var bag in bags)
        {
            var parent = bag.ParentInstanceId is { Length: > 0 } parentId &&
                byId.TryGetValue(parentId, out var parentBag)
                ? parentBag
                : null;
            nodes.Add(MacIoRegistryTopology.Shape(bag, parent));
        }

        return nodes;
    }
}