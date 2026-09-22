using System.Text;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>节点连接器视图模型，表示节点上的输入或输出端口。</para>
/// View model for a node connector, representing an input or output port on a node.
/// </summary>
public partial class NodeConnectorViewModel : ConnectorViewModelBase
{
    private Guid _id = Guid.NewGuid();
    private string _portId = string.Empty;
    private int _maxConnections;
    private object? _data;
    private object? _initialValue;

    /// <summary>
    /// <para>获取或设置连接器的唯一标识符。</para>
    /// Gets or sets the unique identifier of the connector.
    /// </summary>
    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    /// <summary>
    /// <para>获取或设置连接器允许的最大连接数，0 表示不限制。</para>
    /// Gets or sets the maximum number of connections allowed for this connector. 0 means unlimited.
    /// </summary>
    public int MaxConnections
    {
        get => _maxConnections;
        set => SetProperty(ref _maxConnections, value);
    }

    /// <summary>
    /// <para>获取或设置该端口承载的数据值。执行引擎将上游输出写入连接的下游输入，节点可在执行时读取输入值并把结果写入输出值。</para>
    /// Gets or sets the data value carried by this port. The execution engine copies upstream outputs into
    /// connected downstream inputs; nodes read input values and write results to output values during execution.
    /// </summary>
    public object? Data
    {
        get => _data;
        set
        {
            if (SetProperty(ref _data, value))
            {
                OnPropertyChanged(nameof(DataPreview));
            }
        }
    }

    /// <summary>Bounded display of an execution result; binary buffers are never formatted as text.</summary>
    public string DataPreview
    {
        get
        {
            var text = Data switch
            {
                null => string.Empty,
                string value => ValueResourcePrefix is null ? value : LanguageService.Instance.GetString(ValueResourcePrefix + value),
                bool value => LanguageService.Instance.GetString(value ? "Node_Conn_True" : "Node_Conn_False"),
                Services.Patching.PatchWorkspace workspace => workspace.Root,
                Services.Patching.PatchAsset asset => asset.Name,
                Services.Patching.PatchImageEdits edits => string.Join(Environment.NewLine,
                    edits.Items.Take(8).Select(edit => edit.Path[..Math.Min(edit.Path.Length, 160)])),
                IReadOnlyList<string> values => string.Join(Environment.NewLine, values.Take(8).Select(value => value[..Math.Min(value.Length, 160)])),
                byte[] bytes => $"{bytes.Length:N0} B",
                IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => string.Empty
            };
            return text.Length <= 800 ? text : text[..800] + "…";
        }
    }

    /// <summary>
    /// <para>获取或设置该端口的用户编辑初始值。执行引擎在端口未连接上游时，用此值填充 <see cref="Data"/>；
    /// 连接上游时，初始值被忽略（上游值优先）。用于在属性面板为未连接输入端口直接填写常量。</para>
    /// Gets or sets the user-edited initial value of this port. The engine fills <see cref="Data"/> with this
    /// value when the port has no upstream connection; a connected upstream value takes precedence. Used to
    /// provide a constant directly on an unconnected input port via the properties panel.
    /// </summary>
    public object? InitialValue
    {
        get => _initialValue;
        set
        {
            if (SetProperty(ref _initialValue, value))
            {
                OnPropertyChanged(nameof(SelectedChoice));
                OnPropertyChanged(nameof(InitialNumberValue));
                OnPropertyChanged(nameof(InitialBooleanValue));
            }
        }
    }

    /// <summary>Stable choices whose labels are localized without changing persisted values.</summary>
    public IReadOnlyList<NodePortChoice> Choices { get; init; } = [];

    /// <summary>Whether this input uses a selection editor.</summary>
    public bool HasChoices => Choices.Count > 0;

    /// <summary>Optional schema-owned guidance; it is not part of persisted workflow data.</summary>
    public string? HintResourceKey { get; init; }

    /// <summary>Optional localized display mapping for stable string output values.</summary>
    public string? ValueResourcePrefix { get; init; }

    /// <summary>Whether this connector has format-specific editing guidance.</summary>
    public bool HasHint => HintResourceKey is not null;

    /// <summary>Localized guidance describing the accepted value or format dialect.</summary>
    public string LocalizedHint => HintResourceKey is null ? string.Empty : LanguageService.Instance.GetString(HintResourceKey);

    /// <summary>Selected option mapped to the ordinary persisted initial value.</summary>
    public NodePortChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(choice => string.Equals(choice.Value, Convert.ToString(InitialValue), StringComparison.Ordinal));
        set
        {
            if (value is not null)
            {
                InitialValue = value.Value;
            }
        }
    }

    /// <summary>Whether text accepts line breaks in the properties editor.</summary>
    public bool IsMultiline { get; init; }

    /// <summary>Minimum height for single-line or multiline input.</summary>
    public double EditorMinHeight => IsMultiline ? 76 : 24;

    /// <summary>Whether the browse action must select a folder.</summary>
    public bool IsDirectoryPath { get; init; }

    /// <summary>Suggested suffix used by output file pickers.</summary>
    public string OutputExtension { get; init; } = "img";

    /// <summary>Default output name when no path has been entered.</summary>
    public string SuggestedOutputName { get; init; } = "super.img";

    /// <summary>Whether a free-text editor is used rather than a choice editor.</summary>
    public bool UsesTextEditor => IsTextOrPathPort && !HasChoices;

    /// <summary>
    /// <para>是否存在需要持久化的非空初始值（用户显式填写过）。Output 端口或 Flow/Any 端口无初始值语义，返回 <c>false</c>。</para>
    /// Whether a non-null initial value worth persisting exists (explicitly set by the user). Output ports and
    /// Flow/Any ports have no initial-value semantics and return <c>false</c>.
    /// </summary>
    public bool HasPersistableInitialValue
        => _initialValue is not null
           && IsInput
           && PortType is NodePortType.Path or NodePortType.Text or NodePortType.Number or NodePortType.Boolean;

    /// <summary>
    /// <para>是否为文本或路径输入端口（在属性面板渲染文本框）。</para>
    /// Whether this is a text or path input port (renders a text box in the properties panel).
    /// </summary>
    public bool IsTextOrPathPort
        => PortType is NodePortType.Text or NodePortType.Path;

    /// <summary>
    /// <para>是否为路径输入端口（显示路径浏览按钮）。</para>
    /// Whether this is a path input port (shows a path-browse button).
    /// </summary>
    public bool IsPathPort => PortType == NodePortType.Path;

    /// <summary>
    /// <para>是否为数值输入端口（渲染数值框）。</para>
    /// Whether this is a numeric input port (renders a numeric box).
    /// </summary>
    public bool IsNumberPort => PortType == NodePortType.Number;

    /// <summary>
    /// <para>是否为布尔输入端口（渲染开关）。</para>
    /// Whether this is a boolean input port (renders a toggle).
    /// </summary>
    public bool IsBooleanPort => PortType == NodePortType.Boolean;

    /// <summary>
    /// <para>数值端口的初始值（double 表示），供属性面板双向绑定。</para>
    /// Initial value of a numeric port expressed as <c>double</c>, for properties-panel two-way binding.
    /// </summary>
    public double InitialNumberValue
    {
        get => _initialValue is double d ? d : NodeDataUtil.ToDouble(_initialValue, 0);
        set
        {
            InitialValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// <para>布尔端口的初始值，供属性面板双向绑定。</para>
    /// Initial value of a boolean port, for properties-panel two-way binding.
    /// </summary>
    public bool InitialBooleanValue
    {
        get => _initialValue is bool b ? b : NodeDataUtil.ToBool(_initialValue, false);
        set
        {
            InitialValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// <para>获取该端口是否为输入端口（来自 <see cref="ConnectorViewModelBase"/> 的 IsInput 属性语义）。</para>
    /// Gets whether this port is an input port (from the <see cref="ConnectorViewModelBase"/> IsInput semantics).
    /// </summary>
    public bool IsInput { get; set; }

    /// <summary>
    /// <para>获取或设置该端口的类型，用于约束连接兼容性；默认 <see cref="NodePortType.Any"/>。</para>
    /// Gets or sets the port type, used to constrain connection compatibility; defaults to <see cref="NodePortType.Any"/>.
    /// </summary>
    public NodePortType PortType { get; set; } = NodePortType.Any;

    /// <summary>Gets the localized connector name used by the node editor.</summary>
    public string LocalizedTitle
    {
        get
        {
            var key = "Node_Conn_" + GetResourceSuffix(Title);
            var localized = LanguageService.Instance.GetString(key);
            return string.Equals(localized, key, StringComparison.Ordinal) ? Title : localized;
        }
    }

    /// <summary>Gets the localized port data type used by the node editor.</summary>
    public string LocalizedPortTypeDisplay => LanguageService.Instance.GetString("Node_PortType_" + PortType);

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(LocalizedTitle));
        OnPropertyChanged(nameof(LocalizedPortTypeDisplay));
        OnPropertyChanged(nameof(DataPreview));
        OnPropertyChanged(nameof(LocalizedHint));
        foreach (var choice in Choices)
        {
            choice.RefreshLocalization();
        }
    }

    /// <summary>Stable non-localized identifier used by portable workflows and visual plugins.</summary>
    public string PortId
    {
        get => _portId;
        set => SetProperty(ref _portId, value);
    }

    /// <summary>Configured stable id, or a deterministic compatibility id derived from the legacy title.</summary>
    public string StableId => string.IsNullOrWhiteSpace(_portId) ? CreateCompatibilityId(Title) : _portId;

    /// <summary>Creates a deterministic ASCII id for legacy connectors that only have a title.</summary>
    public static string CreateCompatibilityId(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "port";
        var builder = new StringBuilder(Math.Min(title.Length, 128));
        var separator = false;
        foreach (var character in title)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else if (!separator && builder.Length > 0)
            {
                builder.Append('-');
                separator = true;
            }
            if (builder.Length == 128) break;
        }
        if (builder is [.., '-']) builder.Length--;
        return builder.Length == 0 ? "port" : builder.ToString();
    }

    private static string GetResourceSuffix(string title) => title switch
    {
        "Archive Path" => "ArchivePath",
        "Device Size" => "DeviceSize",
        "Group Name" => "GroupName",
        "Milliseconds" => "Milliseconds",
        "Output Archive" => "OutputArchive",
        "Output Path" => "OutputPath",
        "Signed APK" => "SignedApk",
        "Slot A" => "SlotA",
        "Slot B" => "SlotB",
        "Super Path" => "SuperPath",
        "To Sparse" => "ToSparse",
        "Transfer List" => "TransferList",
        "Branch A" => "BranchA",
        "Branch B" => "BranchB",
        "File" => "File",
        "Key" => "Key",
        "Name" => "Name",
        "Value" => "Value",
        "Text" => "Text",
        "True" => "True",
        "False" => "False",
        "Body" => "Body",
        "Completed" => "Completed",
        "Index" => "Index",
        "Reason" => "Reason",
        "Merged" => "Merged",
        "Delta" => "Delta",
        "Source" => "Source",
        "Target" => "Target",
        _ => title.Replace(" ", string.Empty, StringComparison.Ordinal)
    };

    /// <summary>
    /// <para>获取端口类型的显示文本（用于节点图标上的类型标签）。</para>
    /// Gets the display text of the port type (used for the type label on the node icon).
    /// </summary>
    public string PortTypeDisplay => PortType switch
    {
        NodePortType.Any => "Any",
        NodePortType.Flow => "Flow",
        NodePortType.Path => "Path",
        NodePortType.Data => "Data",
        NodePortType.Text => "Text",
        NodePortType.Number => "Number",
        NodePortType.Boolean => "Bool",
        NodePortType.PatchWorkspace => "PatchWorkspace",
        NodePortType.ImageEdits => "ImageEdits",
        NodePortType.PatchAsset => "PatchAsset",
        _ => "Any"
    };
}