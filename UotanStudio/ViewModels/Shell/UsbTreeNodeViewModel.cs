using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Models;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// View model for one node of the USB topology tree. Every displayed text is generated against the
/// backend capabilities: fields the backend cannot obtain in principle render as "not provided by this
/// backend", and only values the backend really reported as empty render as "none", so the UI never
/// turns a collection limit into a claim about the device.
/// <para>USB 拓扑树中一个节点的视图模型。所有显示文本都按后端能力生成：后端原理上拿不到的字段显示
/// 「该后端不提供」，只有后端确实上报且为空时才显示「无」，避免界面把采集能力说成设备事实。</para>
/// </summary>
public sealed partial class UsbTreeNodeViewModel : ObservableObject
{
    private const string AdbProtocol = "adb";
    private const string TextSeparator = " · ";

    private readonly UsbTopologyCapabilities _capabilities;
    private readonly IReadOnlyList<string> _protocols;

    /// <summary><para>节点是否展开，与 <c>TreeViewItem.IsExpanded</c> 双向绑定。</para>
    /// Whether the node is expanded, bound two-way to <c>TreeViewItem.IsExpanded</c>.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Creates a topology node view model.
    /// <para>创建拓扑节点视图模型。</para>
    /// </summary>
    /// <param name="node">
    /// The underlying flat node.
    /// <para>底层扁平节点。</para>
    /// </param>
    /// <param name="capabilities">
    /// The capabilities of the backend that produced this node, which decide the "not provided" wording.
    /// <para>产出该节点的后端能力，决定各字段的「不提供」措辞。</para>
    /// </param>
    /// <param name="children">
    /// The already-built child view models.
    /// <para>已构建的子节点视图模型。</para>
    /// </param>
    /// <param name="depth">
    /// The node depth with the root at 0, used by the default expansion policy.
    /// <para>节点深度，根为 0；用于默认展开策略。</para>
    /// </param>
    public UsbTreeNodeViewModel(UsbTopologyNode node, UsbTopologyCapabilities capabilities,
        IReadOnlyList<UsbTreeNodeViewModel> children, int depth)
    {
        Node = node;
        _capabilities = capabilities;
        Children = children;
        _protocols = CollectProtocols(node, children);
        IsExpanded = depth <= 1 || _protocols.Count > 0;
    }

    /// <summary><para>底层扁平节点，供详情面板与文本导出读取原始字段。</para>
    /// The underlying flat node, read by the detail panel and the text export.</summary>
    public UsbTopologyNode Node { get; }

    /// <summary><para>子节点视图模型集合，绑定到 <c>TreeDataTemplate.ItemsSource</c>。</para>
    /// Child view models bound to <c>TreeDataTemplate.ItemsSource</c>.</summary>
    public IReadOnlyList<UsbTreeNodeViewModel> Children { get; }

    /// <summary><para>节点标题：描述 → 友好名 → 安装类 → 实例标识，取第一个非空值。</para>
    /// The node title: description, then friendly name, then class, then instance id.</summary>
    public string DisplayText => FirstText(Node.Description, Node.FriendlyName, Node.ClassName, Node.InstanceId);

    /// <summary><para>十六进制 <c>VID:PID</c>；节点不带厂商 ID 时为空。</para>
    /// The hexadecimal <c>VID:PID</c>; empty when the node carries no vendor id.</summary>
    public string IdentityText => Node.VendorId is not ushort vendor
        ? string.Empty
        : Node.ProductId is ushort product ? $"{vendor:X4}:{product:X4}" : $"{vendor:X4}";

    /// <summary><para>USB 序列号原文；未上报时为空。</para>
    /// The raw USB serial number; empty when not reported.</summary>
    public string SerialText => Node.SerialNumber ?? string.Empty;

    /// <summary><para>行内速度文本；后端未报告速度时为空，不写成「未知」以外的断言。</para>
    /// The row speed text; empty when the backend reported no speed.</summary>
    public string SpeedText => Node.Speed == UsbDeviceSpeed.Unknown ? string.Empty : Localize(Node.Speed);

    /// <summary><para>详情面板的速度文本；未报告时显示本地化的「未知」。</para>
    /// The detail-panel speed text, showing the localized "unknown" when nothing was reported.</summary>
    public string SpeedDetailText => Node.Speed == UsbDeviceSpeed.Unknown
        ? Localized("UsbTree_Speed_Unknown")
        : Localize(Node.Speed);

    /// <summary><para>本节点与全部后代节点聚合出的协议名，复合设备因此显示其 adb 功能。</para>
    /// The protocol names aggregated from this node and every descendant, so a composite device shows its
    /// adb function.</summary>
    public string ProtocolText => string.Join("/", _protocols);

    /// <summary><para>是否存在可显示的协议标记。</para>
    /// Whether a protocol badge has anything to show.</summary>
    public bool HasProtocol => _protocols.Count > 0;

    /// <summary><para>行内标识信息：VID:PID、序列号与速度。</para>
    /// The row identity line: VID:PID, serial number and speed.</summary>
    public string MetaText => JoinNonEmpty(IdentityText, SerialText, SpeedText);

    /// <summary>Whether the row identity line has anything to show. <para>行内是否存在标识信息。</para></summary>
    public bool HasMeta => MetaText.Length > 0;

    /// <summary>
    /// The driver-binding text (kernel service and INF). Renders "not provided by this backend" instead
    /// of a blank when <see cref="UsbTopologyCapabilities.SupportsDriverInfo"/> is
    /// <see langword="false"/>.
    /// <para>驱动绑定文本（内核服务与 INF）。<see cref="UsbTopologyCapabilities.SupportsDriverInfo"/> 为
    /// <see langword="false"/> 时给出「该后端不提供」而非空白。</para>
    /// </summary>
    public string DriverText => !_capabilities.SupportsDriverInfo
        ? Localized("UsbTree_NotProvided")
        : JoinNonEmpty(Node.DriverService ?? string.Empty, Node.DriverInfPath ?? string.Empty) is
        { Length: > 0 } text ? text : Localized("UsbTree_None");

    /// <summary><para>接口诊断文本，例如 <c>MI_01 ff/42/1</c>；节点无接口三元组时为空。</para>
    /// The interface diagnostics, e.g. <c>MI_01 ff/42/1</c>; empty without an interface triple.</summary>
    public string InterfaceText
    {
        get
        {
            if (Node.InterfaceClass is not byte cls)
            {
                return string.Empty;
            }

            var triple = FormatTriple(cls, Node.InterfaceSubClass, Node.InterfaceProtocol);
            return Node.InterfaceNumber is byte number ? $"MI_{number:X2} {triple}" : triple;
        }
    }

    /// <summary><para>行内第二行文本：驱动与接口诊断。</para>
    /// The second row line: driver and interface diagnostics.</summary>
    public string DiagnosticsText => JoinNonEmpty(DriverText, InterfaceText);

    /// <summary>Whether the second row line has anything to show. <para>行内第二行是否存在内容。</para></summary>
    public bool HasDiagnostics => DiagnosticsText.Length > 0;

    /// <summary>The instance id text. <para>实例标识文本。</para></summary>
    public string InstanceIdText => Node.InstanceId;

    /// <summary><para>父实例标识文本；顶层节点显示「无」。</para>
    /// The parent instance id text; a root node shows "none".</summary>
    public string ParentInstanceIdText => OrNone(Node.ParentInstanceId);

    /// <summary>The localized node role text. <para>本地化的节点角色文本。</para></summary>
    public string KindText => Localized(Node.Kind switch
    {
        UsbTopologyNodeKind.HostController => "UsbTree_Kind_HostController",
        UsbTopologyNodeKind.RootHub => "UsbTree_Kind_RootHub",
        UsbTopologyNodeKind.Hub => "UsbTree_Kind_Hub",
        UsbTopologyNodeKind.Device => "UsbTree_Kind_Device",
        UsbTopologyNodeKind.Function => "UsbTree_Kind_Function",
        _ => "UsbTree_Kind_Unknown"
    });

    /// <summary><para>厂商名，来自内置 USB 厂商 ID 表；未知厂商回退为十六进制 VID。</para>
    /// The vendor name from the built-in vendor id table, falling back to the hexadecimal VID.</summary>
    public string VendorText => Node.VendorId is not ushort vendor
        ? Localized("UsbTree_None")
        : UsbVendorTable.TryGetVendor(vendor) is { Length: > 0 } name ? name : $"{vendor:X4}";

    /// <summary>The installer class, gated by the driver capability. <para>安装类名，受驱动能力门控。</para></summary>
    public string ClassNameText => GateDriverInfo(Node.ClassName);

    /// <summary><para>安装类 GUID，受驱动能力门控。</para>
    /// The installer class GUID, gated by the driver capability.</summary>
    public string ClassGuidText => !_capabilities.SupportsDriverInfo
        ? Localized("UsbTree_NotProvided")
        : Node.ClassGuid is Guid guid ? guid.ToString("D") : Localized("UsbTree_None");

    /// <summary><para>内核驱动服务名，受驱动能力门控。</para>
    /// The kernel driver service, gated by the driver capability.</summary>
    public string DriverServiceText => GateDriverInfo(Node.DriverService);

    /// <summary><para>驱动 INF 名，受驱动能力门控。</para>
    /// The driver INF name, gated by the driver capability.</summary>
    public string DriverInfText => GateDriverInfo(Node.DriverInfPath);

    /// <summary><para>PnP 故障码文本，受故障码能力门控。</para>
    /// The PnP problem code text, gated by the problem-code capability.</summary>
    public string ProblemCodeText => !_capabilities.SupportsProblemCodes
        ? Localized("UsbTree_NotProvided")
        : Node.ProblemCode is uint code ? $"0x{code:X8}" : Localized("UsbTree_None");

    /// <summary>The container id text. <para>容器 ID 文本。</para></summary>
    public string ContainerIdText => Node.ContainerId is Guid container
        ? container.ToString("D")
        : Localized("UsbTree_None");

    /// <summary>The physical location paths, one per line. <para>物理位置路径列表，逐行展示。</para></summary>
    public string LocationPathsText => JoinLines(Node.LocationPaths);

    /// <summary>The hardware ids, one per line. <para>硬件 ID 列表，逐行展示。</para></summary>
    public string HardwareIdsText => JoinLines(Node.HardwareIds);

    /// <summary>The compatible ids, one per line. <para>兼容 ID 列表，逐行展示。</para></summary>
    public string CompatibleIdsText => JoinLines(Node.CompatibleIds);

    /// <summary>
    /// The registered device-interface GUIDs, gated by the device-interface capability.
    /// <para>已注册的设备接口类 GUID 列表，受接口 GUID 能力门控。</para>
    /// </summary>
    public string DeviceInterfaceGuidsText => Node.DeviceInterfaceGuids.Count > 0
        ? string.Join("\n", Node.DeviceInterfaceGuids.Select(guid => guid.ToString("D")))
        : _capabilities.SupportsDeviceInterfaceGuids
            ? Localized("UsbTree_None")
            : Localized("UsbTree_NotProvided");

    /// <summary>The interface-triple detail text. <para>接口三元组详情文本。</para></summary>
    public string InterfaceDetailText => Node.InterfaceClass is { } cls
        ? FormatTriple(cls, Node.InterfaceSubClass, Node.InterfaceProtocol)
        : _capabilities.SupportsInterfaceNodes
            ? Localized("UsbTree_None")
            : Localized("UsbTree_NotProvided");

    /// <summary>
    /// The "ADB interface class not registered" warning, possible only when the backend really reports
    /// device-interface GUIDs; otherwise the UI would be lying about the driver registration.
    /// <para>「ADB 接口类未注册」告警。只有在后端确实上报设备接口 GUID 时才可能给出，否则界面会对
    /// 驱动注册情况说谎。</para>
    /// </summary>
    public string AdbWarningText => _capabilities.SupportsDeviceInterfaceGuids &&
        string.Equals(Node.Protocol, AdbProtocol, StringComparison.OrdinalIgnoreCase) &&
        !UsbTopologyNodes.RegistersAdbInterfaceClass(Node)
            ? Localized("UsbTree_AdbInterfaceMissing")
            : string.Empty;

    /// <summary>Whether the ADB interface-class warning shows. <para>是否需要显示 ADB 接口类告警。</para></summary>
    public bool HasAdbWarning => AdbWarningText.Length > 0;

    /// <summary>
    /// Recomputes every localization-dependent display text after a language change.
    /// <para>语言切换后重算所有依赖本地化的显示文本。</para>
    /// </summary>
    internal void RefreshTexts()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(SpeedDetailText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(DriverText));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(ParentInstanceIdText));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(VendorText));
        OnPropertyChanged(nameof(ClassNameText));
        OnPropertyChanged(nameof(ClassGuidText));
        OnPropertyChanged(nameof(DriverServiceText));
        OnPropertyChanged(nameof(DriverInfText));
        OnPropertyChanged(nameof(ProblemCodeText));
        OnPropertyChanged(nameof(ContainerIdText));
        OnPropertyChanged(nameof(LocationPathsText));
        OnPropertyChanged(nameof(HardwareIdsText));
        OnPropertyChanged(nameof(CompatibleIdsText));
        OnPropertyChanged(nameof(DeviceInterfaceGuidsText));
        OnPropertyChanged(nameof(InterfaceDetailText));
        OnPropertyChanged(nameof(AdbWarningText));
        OnPropertyChanged(nameof(HasAdbWarning));
    }

    /// <summary>
    /// Recomputes the localization-dependent display texts of this node and every descendant after a
    /// language change.
    /// <para>语言切换后重算本节点与全部后代节点依赖本地化的显示文本。</para>
    /// </summary>
    internal void RefreshTextsRecursively()
    {
        RefreshTexts();
        foreach (var child in Children)
        {
            child.RefreshTextsRecursively();
        }
    }

    private static IReadOnlyList<string> CollectProtocols(UsbTopologyNode node,
        IReadOnlyList<UsbTreeNodeViewModel> children)
    {
        List<string> protocols = [];
        AddProtocol(protocols, node.Protocol);
        foreach (var child in children)
        {
            foreach (var protocol in child._protocols)
            {
                AddProtocol(protocols, protocol);
            }
        }

        return protocols;
    }

    private static void AddProtocol(List<string> protocols, string? name)
    {
        if (name is { Length: > 0 } value && !protocols.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            protocols.Add(value);
        }
    }

    private string GateDriverInfo(string? value) => !_capabilities.SupportsDriverInfo
        ? Localized("UsbTree_NotProvided")
        : OrNone(value);

    private static string OrNone(string? value) => value is { Length: > 0 }
        ? value
        : Localized("UsbTree_None");

    private static string FormatTriple(byte cls, byte? subClass, byte? protocol)
        => $"{cls:x2}/{subClass?.ToString("x2") ?? "--"}/{protocol?.ToString("x2") ?? "--"}";

    private static string Localize(UsbDeviceSpeed speed) => Localized(speed switch
    {
        UsbDeviceSpeed.Low => "UsbTree_Speed_Low",
        UsbDeviceSpeed.Full => "UsbTree_Speed_Full",
        UsbDeviceSpeed.High => "UsbTree_Speed_High",
        UsbDeviceSpeed.Super => "UsbTree_Speed_Super",
        UsbDeviceSpeed.SuperPlus => "UsbTree_Speed_SuperPlus",
        _ => "UsbTree_Speed_Unknown"
    });

    private static string FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;

    private static string JoinNonEmpty(params string[] values)
        => string.Join(TextSeparator, values.Where(value => value.Length > 0));

    private static string JoinLines(IReadOnlyList<string> values)
        => values.Count == 0 ? Localized("UsbTree_None") : string.Join("\n", values);

    private static string Localized(string key) => LanguageService.Instance.GetString(key);
}