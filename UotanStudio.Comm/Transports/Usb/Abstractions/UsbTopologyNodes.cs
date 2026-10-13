using UotanStudio.Comm.Devices.Discovery;

namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Single entry point turning raw backend fields into the shared node shape: vendor, product, revision and interface number are derived from the instance id, the interface triple prefers explicit values with compatible-id fallback, and protocol naming is always delegated.
/// <para>把后端原始字段转换为共享节点形状的唯一入口：厂商、产品、修订号与接口号由实例 id 派生，接口三元组优先取显式值并以 compatible id 回退，协议命名一律委托。</para>
/// </summary>
public static class UsbTopologyNodes
{
    /// <summary>
    /// Device-interface class GUID that an ADB interface must register for a debug bridge to attach.
    /// <para>ADB 接口必须注册的设备接口类 GUID，调试桥据此挂载。</para>
    /// </summary>
    /// <value>The ADB interface class GUID. <para>ADB 接口类 GUID。</para></value>
    public static readonly Guid AdbInterfaceClass = new("f72fe0d4-cbcb-407d-8814-9ed673d0dd6b");

    /// <summary>
    /// Fills the derived fields of <paramref name="node"/> without overwriting what the backend already
    /// reported, and returns the resulting node.
    /// <para>补齐 <paramref name="node"/> 的派生字段，不覆盖后端已上报的值，并返回结果节点。</para>
    /// </summary>
    /// <param name="node">
    /// The raw node as read by a backend. <para>后端读到的原始节点。</para>
    /// </param>
    /// <returns>
    /// The node with identity, triple, kind and protocol derived. <para>已推导标识、三元组、角色与协议的节点。</para>
    /// </returns>
    public static UsbTopologyNode Create(UsbTopologyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var hardwareId = node.HardwareIds.Count > 0 ? node.HardwareIds[0] : node.InstanceId;
        var (parsedVendorId, parsedProductId, revision, interfaceNumber) = ParseIdentity(hardwareId);
        var vendorId = node.VendorId ?? parsedVendorId;
        var productId = node.ProductId ?? parsedProductId;
        var (ifaceClass, ifaceSubClass, ifaceProtocol) = node.InterfaceClass is not null
            ? (node.InterfaceClass, node.InterfaceSubClass, node.InterfaceProtocol)
            : ParseInterfaceTriple(node.CompatibleIds);
        var kind = node.Kind == UsbTopologyNodeKind.Unknown
            ? InferKind(node.InstanceId, ifaceClass, interfaceNumber, vendorId)
            : node.Kind;
        var protocol = vendorId is ushort vendor
            ? ClassifyProtocol(vendor, productId, ifaceClass, ifaceSubClass, ifaceProtocol) ?? node.Protocol
            : node.Protocol;

        return node with
        {
            Kind = kind,
            VendorId = vendorId,
            ProductId = productId,
            Revision = node.Revision ?? revision,
            InterfaceNumber = node.InterfaceNumber ?? interfaceNumber,
            InterfaceClass = ifaceClass,
            InterfaceSubClass = ifaceSubClass,
            InterfaceProtocol = ifaceProtocol,
            Protocol = protocol
        };
    }

    /// <summary>
    /// Reports whether any registered device-interface class grants ADB access.
    /// <para>已注册的设备接口类中是否有授予 ADB 访问的类。</para>
    /// </summary>
    /// <param name="node">
    /// The node to inspect. <para>待检查的节点。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the ADB interface class is registered.
    /// <para>注册了 ADB 接口类时返回 <see langword="true"/>。</para>
    /// </returns>
    public static bool RegistersAdbInterfaceClass(UsbTopologyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.DeviceInterfaceGuids.Contains(AdbInterfaceClass);
    }

    private static (ushort? VendorId, ushort? ProductId, string? Revision, byte? InterfaceNumber)
        ParseIdentity(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return (null, null, null, null);
        }

        var portion = hardwareId;
        var vendorId = ParseHex(portion, "VID_", 4);
        var productId = ParseHex(portion, "PID_", 4);
        if (vendorId is null && portion.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            vendorId = ParseHex(portion, "VEN_", 4);
            productId = ParseHex(portion, "DEV_", 4);
        }

        var revision = ParseHex(portion, "REV_", 4)?.ToString("X4");
        var interfaceNumber = ParseHexByte(portion, "MI_", 2);
        return (vendorId, productId, revision, interfaceNumber);
    }

    private static ushort? ParseHex(string portion, string marker, int digits)
    {
        var index = portion.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || portion.Length < index + marker.Length + digits)
        {
            return null;
        }

        var text = portion.AsSpan(index + marker.Length, digits);
        return ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static byte? ParseHexByte(string portion, string marker, int digits)
    {
        var value = ParseHex(portion, marker, digits);
        return value is ushort number && number <= byte.MaxValue ? (byte)number : null;
    }

    private static (byte? Class, byte? SubClass, byte? Protocol) ParseInterfaceTriple(
        IReadOnlyList<string> compatibleIds)
    {
        foreach (var compatibleId in compatibleIds)
        {
            if (string.IsNullOrWhiteSpace(compatibleId) ||
                !compatibleId.Contains("Class_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cls = ParseHexByte(compatibleId, "Class_", 2);
            var sub = ParseHexByte(compatibleId, "SubClass_", 2);
            var protocol = ParseHexByte(compatibleId, "Prot_", 2);
            if (cls is not null)
            {
                return (cls, sub, protocol);
            }
        }

        return (null, null, null);
    }

    private static UsbTopologyNodeKind InferKind(string instanceId, byte? interfaceClass,
        byte? interfaceNumber, ushort? vendorId)
    {
        var text = instanceId;
        if (text.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.RootHub;
        }

        if (text.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("USB4\\", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.HostController;
        }

        if (interfaceNumber is not null || text.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.Function;
        }

        if (interfaceClass == 0x09)
        {
            return UsbTopologyNodeKind.Hub;
        }

        return vendorId is not null ? UsbTopologyNodeKind.Device : UsbTopologyNodeKind.Unknown;
    }

    private static string? ClassifyProtocol(ushort? vendorId, ushort? productId, byte? interfaceClass,
        byte? interfaceSubClass, byte? interfaceProtocol)
        => vendorId is ushort vendor && UsbProtocolClassifier.Classify(vendor, productId ?? 0,
            interfaceClass, interfaceSubClass, interfaceProtocol) is string protocol
            ? protocol
            : null;
}