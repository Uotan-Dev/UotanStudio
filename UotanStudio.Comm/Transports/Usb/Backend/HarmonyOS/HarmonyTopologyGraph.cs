using System.Globalization;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

/// <summary>
/// The plain data shape of one interface from a HarmonyOS USB DDK configuration descriptor: the interface
/// number plus the class/subclass/protocol triple. Endpoints are outside the topology contract, so no
/// endpoint data is carried here.
/// <para>HarmonyOS USB DDK 配置描述符里一个接口的纯数据形状：接口号与类/子类/协议三元组。端点不属于
/// 拓扑契约的范围，因此这里不携带端点。</para>
/// </summary>
internal sealed record HarmonyTopologyInterface
{
    /// <summary>The bInterfaceNumber of the interface. <para>接口的 <c>bInterfaceNumber</c>。</para></summary>
    /// <value>The interface number as the descriptor reports it. <para>描述符报告的接口号。</para></value>
    internal required byte Number { get; init; }

    /// <summary>The bInterfaceClass of the interface. <para>接口的 <c>bInterfaceClass</c>。</para></summary>
    /// <value>The raw class code. <para>原始类代码。</para></value>
    internal byte Class { get; init; }

    /// <summary>The bInterfaceSubClass of the interface. <para>接口的 <c>bInterfaceSubClass</c>。</para></summary>
    /// <value>The raw subclass code. <para>原始子类代码。</para></value>
    internal byte SubClass { get; init; }

    /// <summary>The bInterfaceProtocol of the interface. <para>接口的 <c>bInterfaceProtocol</c>。</para></summary>
    /// <value>The raw protocol code. <para>原始协议代码。</para></value>
    internal byte Protocol { get; init; }
}

/// <summary>
/// The plain data shape the DDK probed for one device: device id, the VID/PID/bcdDevice/bcdUSB/
/// bDeviceClass fields of the device descriptor, the serial and the interface list. The DDK exposes no
/// port chain and no string descriptor that is readable without opening a session, so the serial
/// normally comes from caller-supplied information or stays <see langword="null"/>. Fields carry raw
/// values only; completion and protocol classification are unified by <see cref="UsbTopologyNodes.Create"/>.
/// <para>DDK 对一个设备探测到的纯数据形状：设备 ID、设备描述符的 VID/PID/bcdDevice/bcdUSB/bDeviceClass、
/// 序列号与接口列表。该 DDK 不暴露端口链，也没有不打开会话就能读出的字符串描述符，因此序列号通常来自
/// 调用方已有的信息或保持 <see langword="null"/>。字段只承载原始值，补齐与协议分类由
/// <see cref="UsbTopologyNodes.Create"/> 统一完成。</para>
/// </summary>
internal sealed record HarmonyTopologyDescriptor
{
    /// <summary>The DDK device id used as the stable node identity. <para>作为节点稳定标识的 DDK 设备 ID。</para></summary>
    /// <value>The raw device id. <para>原始设备 ID。</para></value>
    internal required ulong DeviceId { get; init; }

    /// <summary>The device descriptor vendor id. <para>设备描述符的厂商 ID。</para></summary>
    /// <value>The raw vendor id. <para>原始厂商 ID。</para></value>
    internal required ushort VendorId { get; init; }

    /// <summary>The device descriptor product id. <para>设备描述符的产品 ID。</para></summary>
    /// <value>The raw product id. <para>原始产品 ID。</para></value>
    internal required ushort ProductId { get; init; }

    /// <summary>The device descriptor release (bcdDevice). <para>设备描述符的版本号（bcdDevice）。</para></summary>
    /// <value>The raw release or <see langword="null"/> when unreported. <para>原始版本；未报告时为 <see langword="null"/>。</para></value>
    internal ushort? BcdDevice { get; init; }

    /// <summary>The device descriptor USB specification release (bcdUSB). <para>设备描述符的 USB 规范版本（bcdUSB）。</para></summary>
    /// <value>The raw value or <see langword="null"/>; the speed is only inferred when it is present. <para>原始值或 <see langword="null"/>；仅在存在时才推断速度。</para></value>
    internal ushort? BcdUsb { get; init; }

    /// <summary>The device descriptor class (bDeviceClass). <para>设备描述符的类（bDeviceClass）。</para></summary>
    /// <value>The raw class code or <see langword="null"/>; <c>0x09</c> marks a hub. <para>原始类代码或 <see langword="null"/>；<c>0x09</c> 表示集线器。</para></value>
    internal byte? DeviceClass { get; init; }

    /// <summary>The serial number string when the backend already has it. <para>后端已持有时的序列号文本。</para></summary>
    /// <value>The serial text or <see langword="null"/>. <para>序列号文本或 <see langword="null"/>。</para></value>
    internal string? Serial { get; init; }

    /// <summary>The interfaces of configuration index zero. <para>配置索引 0 的接口列表。</para></summary>
    /// <value>The interface list; empty when the configuration descriptor could not be read. <para>接口列表；配置描述符读不到时为空。</para></value>
    internal IReadOnlyList<HarmonyTopologyInterface> Interfaces { get; init; } = [];
}

/// <summary>
/// The pure shaping layer of the HarmonyOS USB DDK: turns descriptor records into flat topology nodes.
/// The DDK exposes only device-id probing, the device descriptor and the configuration descriptor, and
/// never a parent chain, so every node keeps <c>ParentInstanceId = null</c> explicitly — the shared
/// <see cref="UsbTopologyTree"/> therefore shows device and interface nodes as roots on that platform.
/// Roles are always stated: a device with <c>bDeviceClass == 0x09</c> becomes
/// <see cref="UsbTopologyNodeKind.Hub"/>, other devices become <see cref="UsbTopologyNodeKind.Device"/>
/// and interfaces become <see cref="UsbTopologyNodeKind.Function"/>; protocol classification stays with
/// <see cref="UsbTopologyNodes.Create"/>. Duplicate device ids collapse to the first occurrence. This
/// layer references no DDK type and is fully testable without the native library.
/// <para>HarmonyOS USB DDK 的纯塑形层：把设备描述符记录列表转换成扁平拓扑节点。该 DDK 只提供设备 ID
/// 探测、设备描述符与配置描述符，完全不暴露父子链，因此每个节点都显式保持
/// <c>ParentInstanceId = null</c>——共享的 <see cref="UsbTopologyTree"/> 会因此把设备与接口节点在该平台
/// 上全部呈现为根。角色一律显式标注：<c>bDeviceClass == 0x09</c> 的设备为
/// <see cref="UsbTopologyNodeKind.Hub"/>，其余设备为 <see cref="UsbTopologyNodeKind.Device"/>，接口为
/// <see cref="UsbTopologyNodeKind.Function"/>；协议分类仍由 <see cref="UsbTopologyNodes.Create"/> 完成。
/// 重复的设备 ID 折叠到首次出现。本层不引用任何 DDK 类型，可在没有原生库的机器上完整测试。</para>
/// </summary>
internal static class HarmonyTopologyGraph
{
    /// <summary>
    /// Builds the stable instance id of the device node for one DDK device id.
    /// <para>由 DDK 设备 ID 构造设备节点的稳定实例标识。</para>
    /// </summary>
    /// <param name="deviceId">
    /// The DDK device id. <para>DDK 设备 ID。</para>
    /// </param>
    /// <returns>
    /// The <c>harmony-device:&lt;id&gt;</c> identifier. <para><c>harmony-device:&lt;id&gt;</c> 标识。</para>
    /// </returns>
    internal static string DeviceInstanceId(ulong deviceId)
        => "harmony-device:" + deviceId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the instance id of a function node below the given device identity.
    /// <para>由设备标识构造其下方功能节点的实例标识。</para>
    /// </summary>
    /// <param name="deviceInstanceId">
    /// The parent device identity. <para>父设备标识。</para>
    /// </param>
    /// <param name="interfaceNumber">
    /// The interface number. <para>接口号。</para>
    /// </param>
    /// <returns>
    /// The device identity extended with <c>&amp;MI_</c> and a two-digit hex number.
    /// <para>设备标识后接 <c>&amp;MI_</c> 与两位十六进制数。</para>
    /// </returns>
    internal static string InterfaceInstanceId(string deviceInstanceId, byte interfaceNumber)
        => $"{deviceInstanceId}&MI_{interfaceNumber:X2}";

    /// <summary>
    /// Shapes descriptor records into flat device and function nodes, all parentless, keeping every
    /// distinct device id once.
    /// <para>把设备描述符记录塑形为扁平的设备与功能节点，全部无父链，每个不同设备 ID 只保留一次。</para>
    /// </summary>
    /// <param name="descriptors">
    /// The plain descriptor records in probe order. <para>按探测顺序给出的纯描述符记录。</para>
    /// </param>
    /// <returns>
    /// Flat nodes ready for <see cref="UsbTopologyTree.Build"/>. <para>可直接交给 <see cref="UsbTopologyTree.Build"/> 的扁平节点。</para>
    /// </returns>
    internal static IReadOnlyList<UsbTopologyNode> Shape(IReadOnlyList<HarmonyTopologyDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var nodes = new List<UsbTopologyNode>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var instanceId = DeviceInstanceId(descriptor.DeviceId);
            if (!emitted.Add(instanceId))
            {
                continue;
            }

            nodes.Add(ShapeDevice(descriptor, instanceId));
            foreach (var iface in descriptor.Interfaces)
            {
                var interfaceId = InterfaceInstanceId(instanceId, iface.Number);
                if (emitted.Add(interfaceId))
                {
                    nodes.Add(ShapeInterface(descriptor, interfaceId, iface));
                }
            }
        }

        return nodes;
    }

    private static UsbTopologyNode ShapeDevice(HarmonyTopologyDescriptor descriptor, string instanceId) => new()
    {
        InstanceId = instanceId,
        ParentInstanceId = null,
        Kind = descriptor.DeviceClass == 0x09
            ? UsbTopologyNodeKind.Hub
            : UsbTopologyNodeKind.Device,
        VendorId = descriptor.VendorId,
        ProductId = descriptor.ProductId,
        Revision = descriptor.BcdDevice?.ToString("X4", CultureInfo.InvariantCulture),
        SerialNumber = descriptor.Serial,
        Speed = MapSpeed(descriptor.BcdUsb),
        HardwareIds = [BuildHardwareId(descriptor, null)]
    };

    private static UsbTopologyNode ShapeInterface(HarmonyTopologyDescriptor descriptor, string instanceId,
        HarmonyTopologyInterface iface) => new()
        {
            InstanceId = instanceId,
            ParentInstanceId = null,
            Kind = UsbTopologyNodeKind.Function,
            VendorId = descriptor.VendorId,
            ProductId = descriptor.ProductId,
            Revision = descriptor.BcdDevice?.ToString("X4", CultureInfo.InvariantCulture),
            SerialNumber = descriptor.Serial,
            InterfaceNumber = iface.Number,
            InterfaceClass = iface.Class,
            InterfaceSubClass = iface.SubClass,
            InterfaceProtocol = iface.Protocol,
            Speed = MapSpeed(descriptor.BcdUsb),
            HardwareIds = [BuildHardwareId(descriptor, iface.Number)]
        };

    /// <summary>
    /// Infers the link speed from bcdUSB through <see cref="UsbSpeedInference"/>, the same read-only
    /// inference the HarmonyOS finder uses; an absent bcdUSB stays
    /// <see cref="UsbDeviceSpeed.Unknown"/>.
    /// <para>经 <see cref="UsbSpeedInference"/> 由 bcdUSB 推断链路速度，与 HarmonyOS 发现器使用的只读推断一致；
    /// 缺失的 bcdUSB 保持 <see cref="UsbDeviceSpeed.Unknown"/>。</para>
    /// </summary>
    private static UsbDeviceSpeed MapSpeed(ushort? bcdUsb)
        => bcdUsb is { } version ? UsbSpeedInference.FromBcdUsb(version) : UsbDeviceSpeed.Unknown;

    /// <summary>
    /// Builds the shared identity hardware id so <see cref="UsbTopologyNodes.Create"/> can see the same
    /// values the records carry explicitly: <c>USB\VID_&lt;vvvv&gt;&amp;PID_&lt;pppp&gt;</c>, extended by
    /// <c>&amp;REV_&lt;rrrr&gt;</c> on devices and <c>&amp;MI_&lt;nn&gt;</c> on interface functions.
    /// <para>构造共享的身份硬件 ID，使 <see cref="UsbTopologyNodes.Create"/> 能看到与记录里显式值一致的内容：
    /// <c>USB\VID_&lt;vvvv&gt;&amp;PID_&lt;pppp&gt;</c>，设备追加 <c>&amp;REV_&lt;rrrr&gt;</c>，接口功能追加
    /// <c>&amp;MI_&lt;nn&gt;</c>。</para>
    /// </summary>
    private static string BuildHardwareId(HarmonyTopologyDescriptor descriptor, byte? interfaceNumber)
    {
        var text = $"USB\\VID_{descriptor.VendorId:X4}&PID_{descriptor.ProductId:X4}";
        if (interfaceNumber is { } number)
        {
            return text + $"&MI_{number:X2}";
        }

        return descriptor.BcdDevice is { } release
            ? text + $"&REV_{release:X4}"
            : text;
    }
}