using System.Globalization;
using Microsoft.Win32;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// <para>Windows 设备节点原始属性的承载记录。字段保持后端逐项读到的独立值（描述来源、类、驱动、故障码、
/// 容器、硬件/兼容/位置标识、接口 GUID），而塑形与派生集中在 <see cref="WindowsUsbTopologyProvider.ShapeNode"/>，
/// 使纯形状逻辑可以被单元测试直接喂入合成值验证。</para>
/// The record carrying a Windows devnode's raw properties. Fields keep the separate values the backend
/// read per item (description sources, class, driver, problem code, container, hardware/compatible/location
/// ids, interface GUIDs) while shaping and derivation are centralized in
/// <see cref="WindowsUsbTopologyProvider.ShapeNode"/>, so the pure shaping logic can be fed synthetic values
/// directly from unit tests.
/// </summary>
internal sealed record UsbDevNodeRawRecord
{
    /// <summary>The device instance id. <para>设备实例标识。</para></summary>
    /// <value>The instance id. <para>实例标识。</para></value>
    public required string InstanceId { get; init; }

    /// <summary>The parent instance id, attached during the parent climb. <para>父实例标识，在父链上溯时写入。</para></summary>
    /// <value>The parent id or <see langword="null"/>. <para>父标识或 <see langword="null"/>。</para></value>
    public string? ParentInstanceId { get; init; }

    /// <summary>DEVPKEY_Device_DeviceDesc. <para>DEVPKEY_Device_DeviceDesc 值。</para></summary>
    /// <value>The device description or <see langword="null"/>. <para>设备描述或 <see langword="null"/>。</para></value>
    public string? DeviceDescription { get; init; }

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc. <para>DEVPKEY_Device_BusReportedDeviceDesc 值。</para></summary>
    /// <value>The bus-reported description or <see langword="null"/>. <para>总线报告描述或 <see langword="null"/>。</para></value>
    public string? BusReportedDeviceDescription { get; init; }

    /// <summary>DEVPKEY_Device_FriendlyName. <para>DEVPKEY_Device_FriendlyName 值。</para></summary>
    /// <value>The friendly name or <see langword="null"/>. <para>友好名称或 <see langword="null"/>。</para></value>
    public string? FriendlyName { get; init; }

    /// <summary>DEVPKEY_Device_Class. <para>DEVPKEY_Device_Class 值。</para></summary>
    /// <value>The installer class or <see langword="null"/>. <para>安装类或 <see langword="null"/>。</para></value>
    public string? ClassName { get; init; }

    /// <summary>DEVPKEY_Device_ClassGuid. <para>DEVPKEY_Device_ClassGuid 值。</para></summary>
    /// <value>The class GUID or <see langword="null"/>. <para>类 GUID 或 <see langword="null"/>。</para></value>
    public Guid? ClassGuid { get; init; }

    /// <summary>DEVPKEY_Device_Service. <para>DEVPKEY_Device_Service 值。</para></summary>
    /// <value>The kernel service or <see langword="null"/>. <para>内核服务或 <see langword="null"/>。</para></value>
    public string? DriverService { get; init; }

    /// <summary>DEVPKEY_Device_DriverInfPath, the published <c>oemNN.inf</c>. <para>DEVPKEY_Device_DriverInfPath，即已发布的 <c>oemNN.inf</c>。</para></summary>
    /// <value>The published INF name or <see langword="null"/>. <para>已发布 INF 名或 <see langword="null"/>。</para></value>
    public string? DriverInfPath { get; init; }

    /// <summary>DEVPKEY_Device_ProblemCode. <para>DEVPKEY_Device_ProblemCode 值。</para></summary>
    /// <value>The problem code or <see langword="null"/>. <para>故障码或 <see langword="null"/>。</para></value>
    public uint? ProblemCode { get; init; }

    /// <summary>DEVPKEY_Device_ContainerId. <para>DEVPKEY_Device_ContainerId 值。</para></summary>
    /// <value>The container id or <see langword="null"/>. <para>容器 ID 或 <see langword="null"/>。</para></value>
    public Guid? ContainerId { get; init; }

    /// <summary>DEVPKEY_Device_HardwareIds. <para>DEVPKEY_Device_HardwareIds 值。</para></summary>
    /// <value>The hardware ids. <para>硬件 ID 列表。</para></value>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>DEVPKEY_Device_CompatibleIds. <para>DEVPKEY_Device_CompatibleIds 值。</para></summary>
    /// <value>The compatible ids. <para>兼容 ID 列表。</para></value>
    public IReadOnlyList<string> CompatibleIds { get; init; } = [];

    /// <summary>DEVPKEY_Device_LocationPaths. <para>DEVPKEY_Device_LocationPaths 值。</para></summary>
    /// <value>The location paths. <para>物理位置路径列表。</para></value>
    public IReadOnlyList<string> LocationPaths { get; init; } = [];

    /// <summary>Interface class GUIDs registered under the node's registry Interfaces key.
    /// <para>节点注册表 Interfaces 子键下登记的接口类 GUID。</para></summary>
    /// <value>The interface GUIDs. <para>设备接口 GUID 列表。</para></value>
    public IReadOnlyList<Guid> DeviceInterfaceGuids { get; init; } = [];
}

/// <summary>
/// <para>Windows 拓扑后端：读取 PnP 设备树，提供驱动绑定、安装类、故障码与设备接口类 GUID。</para>
/// The Windows topology backend: reads the PnP device tree and reports driver binding, installer class,
/// problem codes and registered device-interface class GUIDs.
/// </summary>
internal sealed class WindowsUsbTopologyProvider : IUsbTopologyProvider
{
    /// <summary>The shared instance. <para>共享实例。</para></summary>
    public static WindowsUsbTopologyProvider Instance { get; } = new();

    /// <inheritdoc />
    public UsbTopologyCapabilities Capabilities { get; } = new()
    {
        Backend = UsbTopologyBackendKind.WindowsPnp,
        SupportsDriverInfo = true,
        SupportsInterfaceNodes = true,
        SupportsHostControllerNodes = true,
        SupportsDeviceInterfaceGuids = true,
        SupportsProblemCodes = true
    };

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsWindows() && UsbPnpNative.CanReadDeviceIdList();

    /// <inheritdoc />
    public IReadOnlyList<UsbTopologyNode> ReadFlatNodes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var seeds = UsbPnpNative.ReadPresentDeviceIds();
        var devInstById = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var parentById = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed) || !UsbPnpNative.TryLocateDevNode(seed, out var seedDevInst))
            {
                continue;
            }

            if (!devInstById.ContainsKey(seed))
            {
                devInstById[seed] = seedDevInst;
                parentById[seed] = null;
            }

            var currentId = seed;
            var currentDevInst = seedDevInst;
            for (var depth = 0; depth < 64; depth++)
            {
                if (!UsbPnpNative.TryGetParent(currentDevInst, out var parentDevInst) ||
                    UsbPnpNative.GetDeviceId(parentDevInst) is not { Length: > 0 } parentId)
                {
                    break;
                }

                var parentIsUsb = parentId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                    parentId.StartsWith("USB4\\", StringComparison.OrdinalIgnoreCase);
                if (!parentIsUsb && !parentId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                parentById[currentId] = parentId;
                if (!devInstById.ContainsKey(parentId))
                {
                    devInstById[parentId] = parentDevInst;
                    parentById[parentId] = null;
                }

                if (!parentIsUsb)
                {
                    break;
                }

                currentId = parentId;
                currentDevInst = parentDevInst;
            }
        }

        var interfaceClasses = ReadRegisteredInterfaceClasses();
        var nodes = new List<UsbTopologyNode>(devInstById.Count);
        foreach (var (instanceId, devInst) in devInstById)
        {
            var raw = UsbPnpNative.ReadProperties(devInst, instanceId) with
            {
                ParentInstanceId = parentById.TryGetValue(instanceId, out var parent) ? parent : null,
                DeviceInterfaceGuids = interfaceClasses.TryGetValue(instanceId, out var classes)
                    ? classes
                    : []
            };
            nodes.Add(ShapeNode(raw));
        }

        return nodes;
    }

    /// <summary>
    /// <para>把一份原始属性记录映射成统一扁平节点。描述按友好名称、总线报告、设备描述回退；USB 标识
    /// （厂商/产品/版本/接口号）、接口三元组、角色与协议在此显式给出，因为 Windows 的 PCI 祖先节点需要按
    /// 枚举前缀判角色，而共享推导只按通用标记工作。共享的 <see cref="UsbTopologyNodes.Create"/> 会以
    /// 「不覆盖后端已给值」的规则原样保留这些字段，并对未填的字段做同样的推导。</para>
    /// Maps a raw property record into the shared flat node. The description falls back through friendly
    /// name, bus-reported and device description; the USB identity (vendor, product, revision, interface
    /// number), the interface triple, the kind and the protocol are stated explicitly here because Windows
    /// PCI ancestors need enumerator-prefix role inference that the generic derivation does not apply.
    /// <see cref="UsbTopologyNodes.Create"/> keeps these values under its "never overwrite what the backend
    /// reported" rule and derives the same fields for anything left unset.
    /// </summary>
    /// <param name="raw">
    /// The raw record to shape. <para>待塑形的原始记录。</para>
    /// </param>
    /// <returns>
    /// The fully shaped flat node. <para>完成推导的扁平节点。</para>
    /// </returns>
    internal static UsbTopologyNode ShapeNode(UsbDevNodeRawRecord raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var identitySource = raw.HardwareIds.Count > 0 ? raw.HardwareIds[0] : raw.InstanceId;
        var vendorId = ToUInt16(ParseHexToken(identitySource, "VID_"));
        var productId = ToUInt16(ParseHexToken(identitySource, "PID_"));
        var revision = ParseHexToken(identitySource, "REV_")?.ToString("X4");
        var interfaceNumber = ToByte(ParseHexToken(identitySource, "MI_"));
        var (ifaceClass, ifaceSubClass, ifaceProtocol) = ParseInterfaceTriple(raw.CompatibleIds);
        var kind = InferKind(raw.InstanceId, vendorId, interfaceNumber, ifaceClass);
        var protocol = vendorId is { } vendor
            ? UsbProtocolClassifier.Classify(vendor, productId, ifaceClass, ifaceSubClass, ifaceProtocol)
            : null;

        return new UsbTopologyNode
        {
            InstanceId = raw.InstanceId,
            ParentInstanceId = raw.ParentInstanceId,
            Kind = kind,
            Description = raw.FriendlyName ?? raw.BusReportedDeviceDescription ?? raw.DeviceDescription,
            FriendlyName = raw.FriendlyName,
            ClassName = raw.ClassName,
            ClassGuid = raw.ClassGuid,
            DriverService = raw.DriverService,
            DriverInfPath = raw.DriverInfPath,
            ProblemCode = raw.ProblemCode,
            ContainerId = raw.ContainerId,
            VendorId = vendorId,
            ProductId = productId,
            Revision = revision,
            InterfaceNumber = interfaceNumber,
            InterfaceClass = ifaceClass,
            InterfaceSubClass = ifaceSubClass,
            InterfaceProtocol = ifaceProtocol,
            HardwareIds = raw.HardwareIds,
            CompatibleIds = raw.CompatibleIds,
            LocationPaths = raw.LocationPaths,
            DeviceInterfaceGuids = raw.DeviceInterfaceGuids,
            Protocol = protocol
        };
    }

    private static UsbTopologyNodeKind InferKind(string instanceId, ushort? vendorId, byte? interfaceNumber,
        byte? interfaceClass)
    {
        if (instanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.RootHub;
        }

        if (instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            instanceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ||
            instanceId.StartsWith("USB4\\", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.HostController;
        }

        if (interfaceNumber is not null || instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        {
            return UsbTopologyNodeKind.Function;
        }

        if (interfaceClass == 0x09)
        {
            return UsbTopologyNodeKind.Hub;
        }

        return vendorId is not null ? UsbTopologyNodeKind.Device : UsbTopologyNodeKind.Unknown;
    }

    private static (byte? Class, byte? SubClass, byte? Protocol) ParseInterfaceTriple(
        IReadOnlyList<string> compatibleIds)
    {
        foreach (var compatibleId in compatibleIds)
        {
            if (ToByte(ParseHexToken(compatibleId, "Class_")) is { } cls)
            {
                return (cls, ToByte(ParseHexToken(compatibleId, "SubClass_")),
                    ToByte(ParseHexToken(compatibleId, "Prot_")));
            }
        }

        return (null, null, null);
    }

    private static ushort? ToUInt16(uint? value)
        => value is { } number && number <= ushort.MaxValue ? (ushort)number : null;

    private static byte? ToByte(uint? value)
        => value is { } number && number <= byte.MaxValue ? (byte)number : null;

    private static uint? ParseHexToken(string? text, string marker)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var search = 0;
        while (true)
        {
            var index = text.IndexOf(marker, search, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var boundaryOk = index == 0 || text[index - 1] is '&' or '\\' or '/' or ':';
            if (boundaryOk)
            {
                var start = index + marker.Length;
                var end = start;
                while (end < text.Length && end - start < 8 && IsHexDigit(text[end]))
                {
                    end++;
                }

                if (end > start &&
                    uint.TryParse(text.AsSpan(start, end - start), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            search = index + marker.Length;
        }
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// <para>扫描 <c>Control\DeviceClasses</c> 建立「设备实例标识 → 已注册设备接口类 GUID」映射。注册接口
    /// 的权威存储在这里，而不是 <c>Enum\&lt;实例&gt;\Interfaces</c>——后者在实机上并不存在。该映射决定界面能否
    /// 如实说出「adb 接口类未注册」，因此宁可多花一次注册表遍历也不能返回空集。</para>
    /// Builds the "device instance id to registered device-interface class GUID" map by sweeping
    /// <c>Control\DeviceClasses</c>. That is where registered interfaces actually live;
    /// <c>Enum\&lt;instance&gt;\Interfaces</c> does not exist on real machines. The map decides whether the UI
    /// can truthfully state "the ADB interface class is not registered", so one extra registry sweep is
    /// cheaper than returning an empty set.
    /// </summary>
    /// <returns>
    /// Registered class GUIDs per USB device instance id; empty when the registry cannot be read.
    /// <para>按 USB 设备实例标识归集的已注册类 GUID；注册表不可读时为空。</para>
    /// </returns>
    private static Dictionary<string, List<Guid>> ReadRegisteredInterfaceClasses()
    {
        var classesByDevice = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return classesByDevice;
        }

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceClasses");
            if (root is null)
            {
                return classesByDevice;
            }

            foreach (var classKey in root.GetSubKeyNames())
            {
                if (!Guid.TryParse(classKey, out var classGuid))
                {
                    continue;
                }

                using var registered = root.OpenSubKey(classKey);
                if (registered is null)
                {
                    continue;
                }

                foreach (var interfaceName in registered.GetSubKeyNames())
                {
                    if (ToDeviceInstanceId(interfaceName) is not { Length: > 0 } deviceId)
                    {
                        continue;
                    }

                    if (!classesByDevice.TryGetValue(deviceId, out var classes))
                    {
                        classes = [];
                        classesByDevice[deviceId] = classes;
                    }

                    if (!classes.Contains(classGuid))
                    {
                        classes.Add(classGuid);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or ArgumentException)
        {
            return classesByDevice;
        }

        return classesByDevice;
    }

    private static string? ToDeviceInstanceId(string interfaceName)
    {
        const string prefix = "##?#";
        if (!interfaceName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var tail = interfaceName.LastIndexOf("#{", StringComparison.Ordinal);
        if (tail <= prefix.Length)
        {
            return null;
        }

        var escaped = interfaceName[prefix.Length..tail];
        if (!escaped.StartsWith("USB#", StringComparison.OrdinalIgnoreCase) &&
            !escaped.StartsWith("USB4#", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return escaped.Replace('#', '\\').TrimEnd('\\');
    }
}