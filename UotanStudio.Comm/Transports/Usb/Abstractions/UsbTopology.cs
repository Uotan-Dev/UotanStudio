namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// <para>USB 总线拓扑中一类节点的角色。角色只描述该节点在拓扑里的位置，不携带平台专有概念，
/// 以便 Windows、Linux、macOS、libusb 与 HarmonyOS 后端共用同一棵树。</para>
/// The role of a node in the USB bus topology. The role only describes where the node sits in the
/// tree and carries no platform-specific notion, so the Windows, Linux, macOS, libusb and HarmonyOS
/// backends can share one tree shape.
/// </summary>
public enum UsbTopologyNodeKind
{
    /// <summary>Host controller (PCI or SoC USB controller). <para>主机控制器（PCI 或 SoC USB 控制器）。</para></summary>
    HostController,

    /// <summary>Controller-owned root hub. <para>控制器自有的根集线器。</para></summary>
    RootHub,

    /// <summary>External hub device. <para>外接集线器设备。</para></summary>
    Hub,

    /// <summary>Device-level node with its own VID/PID. <para>拥有独立 VID/PID 的设备级节点。</para></summary>
    Device,

    /// <summary>Interface or function child of a device. <para>设备的接口或功能子节点。</para></summary>
    Function,

    /// <summary>Role could not be determined from the available evidence. <para>依据现有证据无法判定角色。</para></summary>
    Unknown
}

/// <summary>
/// <para>提供拓扑数据的后端。每个后端能填满的字段不同，能力差异通过
/// <see cref="UsbTopologyCapabilities"/> 明示，界面不得把「后端不提供」渲染成「设备没有」。</para>
/// The backend that produced the topology. Backends fill different field sets; the differences are
/// declared through <see cref="UsbTopologyCapabilities"/> so the UI never renders "not provided by
/// this backend" as "absent on the device".
/// </summary>
public enum UsbTopologyBackendKind
{
    /// <summary>Windows PnP device tree via cfgmgr32 and the device registry. <para>经 cfgmgr32 与设备注册表读取的 Windows PnP 设备树。</para></summary>
    WindowsPnp,

    /// <summary>Linux <c>/sys/bus/usb</c> topology. <para>Linux 的 <c>/sys/bus/usb</c> 拓扑。</para></summary>
    LinuxSysfs,

    /// <summary>macOS I/O Registry USB plane. <para>macOS 的 I/O Registry USB 层。</para></summary>
    MacOSIoRegistry,

    /// <summary>libusb device list, reconstructed into a bus/port tree. <para>libusb 设备清单，重建为总线/端口树。</para></summary>
    LibUsb,

    /// <summary>HarmonyOS USB DDK device list. <para>HarmonyOS USB DDK 设备清单。</para></summary>
    HarmonyOS
}

/// <summary>
/// <para>后端能力声明。字段为 <see langword="false"/> 表示该后端在原理上拿不到这项数据，
/// 而不是本次枚举失败。</para>
/// Backend capability declaration. A <see langword="false"/> field means the backend cannot obtain
/// that data in principle, not that this particular enumeration failed.
/// </summary>
public sealed record UsbTopologyCapabilities
{
    /// <summary>The reporting backend. <para>上报该能力的后端。</para></summary>
    public required UsbTopologyBackendKind Backend { get; init; }

    /// <summary>Whether driver binding (class, service, INF) is reported. <para>是否上报驱动绑定（安装类、服务、INF）。</para></summary>
    public bool SupportsDriverInfo { get; init; }

    /// <summary>Whether interface/function child nodes are reported. <para>是否上报接口/功能子节点。</para></summary>
    public bool SupportsInterfaceNodes { get; init; }

    /// <summary>Whether host-controller and root-hub nodes are reported. <para>是否上报主机控制器与根集线器节点。</para></summary>
    public bool SupportsHostControllerNodes { get; init; }

    /// <summary>Whether registered device-interface GUIDs are reported. <para>是否上报已注册的设备接口 GUID 列表。</para></summary>
    public bool SupportsDeviceInterfaceGuids { get; init; }

    /// <summary>Whether PnP problem codes are reported. <para>是否上报 PnP 故障码。</para></summary>
    public bool SupportsProblemCodes { get; init; }
}

/// <summary>
/// <para>拓扑中的一个扁平节点。所有后端共用该形状：后端只填自己拿得到的字段，其余保持
/// <see langword="null"/>；父子关系用 <see cref="ParentInstanceId"/> 表达，由
/// <c>UsbTopologyTree</c> 统一成树。</para>
/// One flat node of the topology. Every backend shares this shape: a backend fills only the fields it
/// can obtain and leaves the rest <see langword="null"/>; parentage is expressed through
/// <see cref="ParentInstanceId"/> and unified into a tree by <c>UsbTopologyTree</c>.
/// </summary>
public sealed record UsbTopologyNode
{
    /// <summary>
    /// Backend-native stable identifier of this node, unique within one enumeration.
    /// <para>后端原生的稳定节点标识，单次枚举内唯一。</para>
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>Parent node identifier, or <see langword="null"/> for a top-level node. <para>父节点标识；顶层节点为 <see langword="null"/>。</para></summary>
    public string? ParentInstanceId { get; init; }

    /// <summary>Role of the node in the tree. <para>节点在树中的角色。</para></summary>
    public required UsbTopologyNodeKind Kind { get; init; }

    /// <summary>Human-readable device description, e.g. a product string. <para>可读的设备描述，例如产品字符串。</para></summary>
    public string? Description { get; init; }

    /// <summary>User-assigned or platform friendly name. <para>用户或平台给出的友好名称。</para></summary>
    public string? FriendlyName { get; init; }

    /// <summary>Manufacturer name when the backend can read it. <para>后端可读时给出的厂商名。</para></summary>
    public string? ManufacturerName { get; init; }

    /// <summary>Installer class (Windows), bound driver name (Linux) or provider class (macOS).
    /// <para>安装类（Windows）、已绑定驱动名（Linux）或 provider class（macOS）。</para></summary>
    public string? ClassName { get; init; }

    /// <summary>Installer class GUID, Windows only. <para>安装类 GUID，仅 Windows 提供。</para></summary>
    public Guid? ClassGuid { get; init; }

    /// <summary>Kernel service backing the node, e.g. <c>WINUSB</c> or <c>libusb0</c>.
    /// <para>节点背后的内核服务，例如 <c>WINUSB</c> 或 <c>libusb0</c>。</para></summary>
    public string? DriverService { get; init; }

    /// <summary>Published driver INF name when the backend exposes it. <para>后端暴露时给出的已发布驱动 INF 名。</para></summary>
    public string? DriverInfPath { get; init; }

    /// <summary>USB vendor id, when the node carries one. <para>节点携带时的 USB 厂商 ID。</para></summary>
    public ushort? VendorId { get; init; }

    /// <summary>USB product id, when the node carries one. <para>节点携带时的 USB 产品 ID。</para></summary>
    public ushort? ProductId { get; init; }

    /// <summary>Device release (bcdDevice) text. <para>设备版本号（bcdDevice）文本。</para></summary>
    public string? Revision { get; init; }

    /// <summary>USB serial number text. <para>USB 序列号文本。</para></summary>
    public string? SerialNumber { get; init; }

    /// <summary>Interface number on function nodes. <para>功能节点上的接口号。</para></summary>
    public byte? InterfaceNumber { get; init; }

    /// <summary>bInterfaceClass on function nodes. <para>功能节点的 bInterfaceClass。</para></summary>
    public byte? InterfaceClass { get; init; }

    /// <summary>bInterfaceSubClass on function nodes. <para>功能节点的 bInterfaceSubClass。</para></summary>
    public byte? InterfaceSubClass { get; init; }

    /// <summary>bInterfaceProtocol on function nodes. <para>功能节点的 bInterfaceProtocol。</para></summary>
    public byte? InterfaceProtocol { get; init; }

    /// <summary>Negotiated device speed. <para>协商出的设备速度。</para></summary>
    public UsbDeviceSpeed Speed { get; init; } = UsbDeviceSpeed.Unknown;

    /// <summary>Container id grouping functions of one physical device. <para>把同一物理设备的功能归到一起的容器 ID。</para></summary>
    public Guid? ContainerId { get; init; }

    /// <summary>Hardware ids as reported by the backend. <para>后端上报的硬件 ID 列表。</para></summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Compatible ids as reported by the backend. <para>后端上报的兼容 ID 列表。</para></summary>
    public IReadOnlyList<string> CompatibleIds { get; init; } = [];

    /// <summary>Stable physical location paths of the node. <para>节点的稳定物理位置路径。</para></summary>
    public IReadOnlyList<string> LocationPaths { get; init; } = [];

    /// <summary>Device-interface class GUIDs registered by the node. <para>节点注册的设备接口类 GUID。</para></summary>
    public IReadOnlyList<Guid> DeviceInterfaceGuids { get; init; } = [];

    /// <summary>PnP problem code, when the backend reports status. <para>后端上报状态时给出的 PnP 故障码。</para></summary>
    public uint? ProblemCode { get; init; }

    /// <summary>
    /// Debug-bridge or download protocol name from <c>UsbProtocolClassifier</c>, or
    /// <see langword="null"/> when the node identifies none.
    /// <para>来自 <c>UsbProtocolClassifier</c> 的调试桥或下载协议名；节点无此特征时为 <see langword="null"/>。</para>
    /// </summary>
    public string? Protocol { get; init; }
}

/// <summary>
/// <para>树中的一个分支：节点加其子分支。由 <c>UsbTopologyTree.Build</c> 生成，供界面直接绑定。</para>
/// A branch of the tree: a node plus its child branches. Produced by <c>UsbTopologyTree.Build</c> and
/// bound directly by the UI.
/// </summary>
/// <param name="Node">
/// The branch node. <para>分支节点。</para>
/// </param>
/// <param name="Children">
/// Child branches in display order. <para>按显示顺序排列的子分支。</para>
/// </param>
public sealed record UsbTopologyBranch(UsbTopologyNode Node, IReadOnlyList<UsbTopologyBranch> Children);

/// <summary>
/// <para>一次拓扑读取的结果：能力声明、树、原始扁平节点与耗时。保留扁平节点是为了让界面在
/// 「按树显示」与「按列表显示」之间切换时不必重新枚举。</para>
/// One topology read: capabilities, tree, the raw flat nodes and elapsed time. The flat nodes are kept
/// so the UI can switch between tree and list views without re-enumerating.
/// </summary>
/// <param name="Capabilities">
/// What the reporting backend can fill. <para>上报后端能填充的范围。</para>
/// </param>
/// <param name="Roots">
/// Tree roots in display order. <para>按显示顺序排列的树根。</para>
/// </param>
/// <param name="Nodes">
/// The flat nodes the tree was built from. <para>构树所用的扁平节点。</para>
/// </param>
/// <param name="Elapsed">
/// Enumeration duration. <para>枚举耗时。</para>
/// </param>
public sealed record UsbTopologyResult(
    UsbTopologyCapabilities Capabilities,
    IReadOnlyList<UsbTopologyBranch> Roots,
    IReadOnlyList<UsbTopologyNode> Nodes,
    TimeSpan Elapsed)
{
    /// <summary>Total node count of this enumeration. <para>本次枚举的节点总数。</para></summary>
    /// <value>The number of flat nodes. <para>扁平节点数量。</para></value>
    public int NodeCount => Nodes.Count;
}

/// <summary>
/// <para>USB 拓扑后端的最小契约：只读扁平节点，成树由共享的 <c>UsbTopologyTree</c> 负责，
/// 因此各平台不会各自实现排序、孤儿提升与环检测。</para>
/// The minimal contract for a USB topology backend: read flat nodes only. Tree construction is shared
/// through <c>UsbTopologyTree</c>, so no platform re-implements ordering, orphan hoisting or cycle handling.
/// </summary>
public interface IUsbTopologyProvider
{
    /// <summary>What this backend can report. <para>该后端可上报的范围。</para></summary>
    /// <value>The capability set. <para>能力集合。</para></value>
    UsbTopologyCapabilities Capabilities { get; }

    /// <summary>Whether this backend can run on the current platform and reach its data source.
    /// <para>该后端能否在当前平台运行并访问其数据源。</para></summary>
    /// <value><see langword="true"/> when flat nodes can be read. <para>可读取扁平节点时为 <see langword="true"/>。</para></value>
    bool IsAvailable { get; }

    /// <summary>
    /// Reads every topology node the backend can see, without nesting.
    /// <para>读取该后端可见的全部拓扑节点，不做嵌套。</para>
    /// </summary>
    /// <returns>Flat nodes; empty when nothing is present. <para>扁平节点；无内容时为空。</para></returns>
    IReadOnlyList<UsbTopologyNode> ReadFlatNodes();
}