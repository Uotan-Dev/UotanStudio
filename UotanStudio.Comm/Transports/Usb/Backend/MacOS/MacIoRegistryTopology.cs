using System.Globalization;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// The raw property bag read from a single I/O Registry entry. The keys follow the IOKit property names and
/// each value is converted to a nullable managed type during the read, so an unreadable property stays
/// <see langword="null"/> rather than being invented. <see cref="IOClass"/> drives the node role and class
/// name; the rest are device and interface descriptor fields. The type holds no IOKit handle, so it can be
/// built and tested without IOKit.
/// <para>从单个 I/O Registry 条目读到的原始属性袋：键名沿用 IOKit 的属性名，取值在读取阶段就转成
/// 托管可空类型，读不到的属性保持 <see langword="null"/> 而不伪造默认值。<see cref="IOClass"/>
/// 决定节点角色与类名，其余为设备/接口描述符字段。本类型不涉及任何 IOKit 句柄，因此可在没有 IOKit
/// 的环境下构造并测试。</para>
/// </summary>
internal sealed record MacIoRegistryEntry
{
    /// <summary>The registry service path used as the stable node identity. <para>作为节点稳定标识的注册表服务路径。</para></summary>
    /// <value>The absolute IOService-plane path. <para>IOService 平面上的绝对路径。</para></value>
    internal required string InstanceId { get; init; }

    /// <summary>The parent service path, or <see langword="null"/> for a top-level node. <para>父服务路径；顶层节点为 <see langword="null"/>。</para></summary>
    /// <value>The parent identity or <see langword="null"/>. <para>父标识或 <see langword="null"/>。</para></value>
    internal string? ParentInstanceId { get; init; }

    /// <summary>The entry's own <c>IOClass</c> name. <para>条目自身的 <c>IOClass</c> 名。</para></summary>
    /// <value>The class name or <see langword="null"/>. <para>类名或 <see langword="null"/>。</para></value>
    internal string? IOClass { get; init; }

    /// <summary>The <c>idVendor</c> number. <para><c>idVendor</c> 数值。</para></summary>
    /// <value>The vendor id or <see langword="null"/>. <para>厂商 ID 或 <see langword="null"/>。</para></value>
    internal ushort? VendorId { get; init; }

    /// <summary>The <c>idProduct</c> number. <para><c>idProduct</c> 数值。</para></summary>
    /// <value>The product id or <see langword="null"/>. <para>产品 ID 或 <see langword="null"/>。</para></value>
    internal ushort? ProductId { get; init; }

    /// <summary>The <c>bcdDevice</c> release number. <para><c>bcdDevice</c> 版本号。</para></summary>
    /// <value>The release or <see langword="null"/>. <para>版本或 <see langword="null"/>。</para></value>
    internal ushort? BcdDevice { get; init; }

    /// <summary>The <c>bDeviceClass</c> number, used to recognise hubs. <para><c>bDeviceClass</c> 数值，用于识别集线器。</para></summary>
    /// <value>The device class or <see langword="null"/>. <para>设备类或 <see langword="null"/>。</para></value>
    internal byte? DeviceClass { get; init; }

    /// <summary>The <c>USB Serial Number</c> or <c>serial number</c> string. <para><c>USB Serial Number</c> 或 <c>serial number</c> 字符串。</para></summary>
    /// <value>The serial text or <see langword="null"/>. <para>序列号文本或 <see langword="null"/>。</para></value>
    internal string? SerialNumber { get; init; }

    /// <summary>The <c>USB Vendor Name</c> string. <para><c>USB Vendor Name</c> 字符串。</para></summary>
    /// <value>The vendor name text or <see langword="null"/>. <para>厂商名字符串或 <see langword="null"/>。</para></value>
    internal string? VendorName { get; init; }

    /// <summary>The <c>USB Product Name</c> or <c>kUSBProductString</c> string. <para><c>USB Product Name</c> 或 <c>kUSBProductString</c> 字符串。</para></summary>
    /// <value>The product name text or <see langword="null"/>. <para>产品名字符串或 <see langword="null"/>。</para></value>
    internal string? ProductName { get; init; }

    /// <summary>The <c>bInterfaceClass</c> number of an interface entry. <para>接口条目的 <c>bInterfaceClass</c> 数值。</para></summary>
    /// <value>The interface class or <see langword="null"/>. <para>接口类或 <see langword="null"/>。</para></value>
    internal byte? InterfaceClass { get; init; }

    /// <summary>The <c>bInterfaceSubClass</c> number of an interface entry. <para>接口条目的 <c>bInterfaceSubClass</c> 数值。</para></summary>
    /// <value>The interface subclass or <see langword="null"/>. <para>接口子类或 <see langword="null"/>。</para></value>
    internal byte? InterfaceSubClass { get; init; }

    /// <summary>The <c>bInterfaceProtocol</c> number of an interface entry. <para>接口条目的 <c>bInterfaceProtocol</c> 数值。</para></summary>
    /// <value>The interface protocol or <see langword="null"/>. <para>接口协议或 <see langword="null"/>。</para></value>
    internal byte? InterfaceProtocol { get; init; }

    /// <summary>The <c>bInterfaceNumber</c> of an interface entry. <para>接口条目的 <c>bInterfaceNumber</c>。</para></summary>
    /// <value>The interface number or <see langword="null"/>. <para>接口号或 <see langword="null"/>。</para></value>
    internal byte? InterfaceNumber { get; init; }

    /// <summary>The published interface name of an interface entry. <para>接口条目公布的接口名。</para></summary>
    /// <value>The interface name text or <see langword="null"/>. <para>接口名文本或 <see langword="null"/>。</para></value>
    internal string? InterfaceName { get; init; }

    /// <summary>The <c>kUSBDeviceSpeed</c> text, tolerating an absent value. <para><c>kUSBDeviceSpeed</c> 文本，允许缺失。</para></summary>
    /// <value>A speed token such as <c>High</c>, a numeric rate, or <see langword="null"/>. <para>形如 <c>High</c> 的速度词、数值速率，或 <see langword="null"/>。</para></value>
    internal string? Speed { get; init; }
}

/// <summary>
/// The pure layer that shapes an I/O Registry property bag into the shared topology node without touching
/// any IOKit handle, so it is fully testable with a synthetic property bag on any host. The role is always
/// stated explicitly from the entry's own <c>IOClass</c> (with <c>bDeviceClass==0x09</c>): an interface
/// class is a function, a root-hub class is a root hub, a controller or PCI class is a host controller, a
/// hub device class is a hub, and everything else is a device. <c>ClassName</c> is the entry's own
/// <c>IOClass</c> rather than the parent driver's <c>IOProviderClass</c>, because macOS has no
/// Windows-style device-interface class GUIDs or PnP problem codes and those fields stay
/// <see langword="null"/>. The vendor, product, release and interface triple fallbacks and protocol
/// classification are still delegated to <see cref="UsbTopologyNodes.Create"/>; this layer writes only the
/// values it read.
/// <para>把 I/O Registry 的原始属性袋塑形成统一拓扑节点的纯逻辑层，不触碰任何 IOKit 句柄，因此可用
/// 合成属性袋在无 macOS 的环境中完整测试。角色一律从条目自身的 <c>IOClass</c>（辅以
/// <c>bDeviceClass==0x09</c>）显式判定：接口类为功能节点、根集线器类为根集线器、控制器/PCI 类为主机
/// 控制器、集线器设备类为集线器，其余为设备。<c>ClassName</c> 取条目自身的 <c>IOClass</c>（而非父驱动
/// 的 <c>IOProviderClass</c>），因为 macOS 没有 Windows 风格的设备接口类 GUID 与 PnP 故障码，这些字段
/// 保持 <see langword="null"/>。厂商/产品/版本/接口三元组的兜底与协议分类仍交给
/// <see cref="UsbTopologyNodes.Create"/>，本层只写自己读到的原始值。</para>
/// </summary>
internal static class MacIoRegistryTopology
{
    /// <summary>
    /// Shapes one registry property bag into the flat contract node, inheriting from
    /// <paramref name="parent"/> only the identity an interface entry cannot expose itself (vendor, product,
    /// release, serial, vendor name and the negotiated speed). macOS publishes no Windows-style hardware ids,
    /// so <c>HardwareIds</c> carries a synthetic <c>usb:vVVVVpPPPP</c> identifier when the vendor is known.
    /// <para>把单个注册表属性袋塑形为扁平契约节点，仅从 <paramref name="parent"/> 继承接口条目自身无法给出的
    /// 标识（厂商/产品/版本/序列号/厂商名，以及协商速率）。macOS 不发布 Windows 风格的硬件 ID，因此在厂商
    /// 已知时 <c>HardwareIds</c> 使用合成的 <c>usb:vVVVVpPPPP</c> 标识。</para>
    /// </summary>
    /// <param name="entry">
    /// The property bag as read. <para>按原样读到的属性袋。</para>
    /// </param>
    /// <param name="parent">
    /// The parent property bag when known, otherwise <see langword="null"/>. <para>已知时的父属性袋，否则为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// The flat node with the role stated explicitly and unreadable attributes left <see langword="null"/>.
    /// <para>显式标注角色、不可读属性保持 <see langword="null"/> 的扁平节点。</para>
    /// </returns>
    internal static UsbTopologyNode Shape(MacIoRegistryEntry entry, MacIoRegistryEntry? parent)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var kind = ResolveKind(entry.IOClass, entry.DeviceClass);
        var function = kind == UsbTopologyNodeKind.Function;
        var source = function ? parent : null;
        var vendorId = entry.VendorId ?? source?.VendorId;
        var productId = entry.ProductId ?? source?.ProductId;
        var bcdDevice = entry.BcdDevice ?? source?.BcdDevice;
        var serialNumber = entry.SerialNumber ?? source?.SerialNumber;
        var vendorName = entry.VendorName ?? source?.VendorName;
        var speed = MapSpeed(entry.Speed);
        if (speed == UsbDeviceSpeed.Unknown && source is not null)
        {
            speed = MapSpeed(source.Speed);
        }

        return new UsbTopologyNode
        {
            InstanceId = entry.InstanceId,
            ParentInstanceId = entry.ParentInstanceId,
            Kind = kind,
            Description = function ? entry.InterfaceName ?? entry.ProductName : entry.ProductName,
            ManufacturerName = vendorName,
            ClassName = entry.IOClass,
            VendorId = vendorId,
            ProductId = productId,
            Revision = bcdDevice is { } release
                ? release.ToString("X4", CultureInfo.InvariantCulture)
                : null,
            SerialNumber = serialNumber,
            InterfaceNumber = function ? entry.InterfaceNumber : null,
            InterfaceClass = function ? entry.InterfaceClass : null,
            InterfaceSubClass = function ? entry.InterfaceSubClass : null,
            InterfaceProtocol = function ? entry.InterfaceProtocol : null,
            Speed = speed,
            HardwareIds = BuildHardwareIds(function, vendorId, productId, entry)
        };
    }

    /// <summary>
    /// States the topology role from the entry's own <c>IOClass</c> and the device class: a class carrying
    /// <c>Interface</c> is a function, <c>RootHub</c> is a root hub, a controller or <c>PCI</c> class is a
    /// host controller, a device class of <c>0x09</c> is a hub, and anything else is a device.
    /// <para>从条目自身的 <c>IOClass</c> 与设备类直接确定角色：含 <c>Interface</c> 的类为功能节点，
    /// <c>RootHub</c> 为根集线器，控制器或 <c>PCI</c> 类为主机控制器，设备类为 <c>0x09</c> 时为集线器，
    /// 其余为设备。</para>
    /// </summary>
    /// <param name="ioClass">
    /// The entry's <c>IOClass</c> text, or <see langword="null"/>. <para>条目的 <c>IOClass</c> 文本，或 <see langword="null"/>。</para>
    /// </param>
    /// <param name="deviceClass">
    /// The <c>bDeviceClass</c> number, or <see langword="null"/>. <para><c>bDeviceClass</c> 数值，或 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// The role stated by the registry. <para>注册表已明确给出的角色。</para>
    /// </returns>
    internal static UsbTopologyNodeKind ResolveKind(string? ioClass, byte? deviceClass)
    {
        var value = ioClass ?? string.Empty;
        if (value.Contains("Interface", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.Function;
        }

        if (value.Contains("RootHub", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.RootHub;
        }

        if (IsControllerClass(value))
        {
            return UsbTopologyNodeKind.HostController;
        }

        return deviceClass == 0x09 ? UsbTopologyNodeKind.Hub : UsbTopologyNodeKind.Device;
    }

    /// <summary>
    /// Maps the <c>kUSBDeviceSpeed</c> text onto <see cref="UsbDeviceSpeed"/>: an absent or unparsable value
    /// and the explicit <c>Unknown</c> token are <see cref="UsbDeviceSpeed.Unknown"/>; otherwise a speed word
    /// (<c>Low</c>, <c>Full</c>, <c>High</c>, <c>Super</c>/<c>SuperPlus</c>) selects the bucket, and a numeric
    /// rate falls back to the shared Mbps thresholds.
    /// <para>把 <c>kUSBDeviceSpeed</c> 文本映射到 <see cref="UsbDeviceSpeed"/>：缺失、不可解析或显式的
    /// <c>Unknown</c> 词均为 <see cref="UsbDeviceSpeed.Unknown"/>；否则由速度词（<c>Low</c>、<c>Full</c>、
    /// <c>High</c>、<c>Super</c>/<c>SuperPlus</c>）决定档位，数值速率回退到共享的 Mbps 阈值。</para>
    /// </summary>
    /// <param name="text">
    /// The raw speed text. <para>原始速度文本。</para>
    /// </param>
    /// <returns>
    /// The mapped speed. <para>映射后的速度。</para>
    /// </returns>
    internal static UsbDeviceSpeed MapSpeed(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return UsbDeviceSpeed.Unknown;
        }

        if (value.Contains("super", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("plus", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("10", StringComparison.Ordinal)
                ? UsbDeviceSpeed.SuperPlus
                : UsbDeviceSpeed.Super;
        }

        if (value.Contains("high", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceSpeed.High;
        }

        if (value.Contains("full", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceSpeed.Full;
        }

        if (value.Contains("low", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDeviceSpeed.Low;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) && mbps > 0
            ? UsbSpeedInference.FromMbps(mbps)
            : UsbDeviceSpeed.Unknown;
    }

    private static bool IsControllerClass(string ioClass)
        => ioClass.Contains("XHCI", StringComparison.OrdinalIgnoreCase) ||
            ioClass.Contains("EHCI", StringComparison.OrdinalIgnoreCase) ||
            ioClass.Contains("UHCI", StringComparison.OrdinalIgnoreCase) ||
            ioClass.Contains("OHCI", StringComparison.OrdinalIgnoreCase) ||
            ioClass.Contains("HostController", StringComparison.OrdinalIgnoreCase) ||
            ioClass.Contains("USBController", StringComparison.OrdinalIgnoreCase) ||
            ioClass.Contains("PCI", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildHardwareIds(bool function, ushort? vendorId, ushort? productId,
        MacIoRegistryEntry entry)
    {
        if (vendorId is not { } vendor)
        {
            return [];
        }

        var identity = productId is { } product
            ? $"usb:v{vendor:X4}p{product:X4}"
            : $"usb:v{vendor:X4}";
        if (function && entry.InterfaceClass is { } ifaceClass)
        {
            identity += $"ic{ifaceClass:X2}isc{entry.InterfaceSubClass ?? 0:X2}ip{entry.InterfaceProtocol ?? 0:X2}";
        }

        return [identity];
    }
}