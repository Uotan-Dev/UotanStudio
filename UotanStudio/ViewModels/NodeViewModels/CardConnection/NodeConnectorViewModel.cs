using System.Text;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// View model for a node connector, representing an input or output port on a node.
/// <para>节点连接器视图模型，表示节点上的输入或输出端口。</para>
/// </summary>
public partial class NodeConnectorViewModel : ConnectorViewModelBase
{
    internal bool IsEditingLocked { get; set; }

    /// <summary>Whether the current invocation obtains this input from an upstream connection.</summary>
    internal bool HasRuntimeSource { get; set; }

    internal bool IsRuntimeSensitive
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(DataPreview));
            }
        }
    }

    /// <summary>
    /// Gets or sets the unique identifier of the connector.
    /// <para>获取或设置连接器的唯一标识符。</para>
    /// </summary>
    public Guid Id
    {
        get => field;
        set => SetProperty(ref field, value);
    } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the maximum number of connections allowed for this connector. 0 means unlimited.
    /// <para>获取或设置连接器允许的最大连接数，0 表示不限制。</para>
    /// </summary>
    public int MaxConnections
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Gets or sets the data value carried by this port. The execution engine copies upstream outputs into
    /// connected downstream inputs; nodes read input values and write results to output values during execution.
    /// <para>获取或设置该端口承载的数据值。执行引擎将上游输出写入连接的下游输入，节点可在执行时读取输入值并把结果写入输出值。</para>
    /// </summary>
    public object? Data
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
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
            if ((IsSensitive || IsRuntimeSensitive) && Data is not null)
            {
                return LanguageService.Instance.GetString("Interaction_SensitiveValue");
            }
            return NodeValueFormatter.Describe(Data, ValueResourcePrefix);
        }
    }

    /// <summary>
    /// Gets or sets the user-edited initial value of this port. The engine fills <see cref="Data"/> with this
    /// value when the port has no upstream connection; a connected upstream value takes precedence. Used to
    /// provide a constant directly on an unconnected input port via the properties panel.
    /// <para>获取或设置该端口的用户编辑初始值。执行引擎在端口未连接上游时，用此值填充 <see cref="Data"/>；
    /// 连接上游时，初始值被忽略（上游值优先）。用于在属性面板为未连接输入端口直接填写常量。</para>
    /// </summary>
    public object? InitialValue
    {
        get;
        set
        {
            if (!IsEditingLocked && SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(SelectedChoice));
                OnPropertyChanged(nameof(InitialNumberValue));
                OnPropertyChanged(nameof(InitialBooleanValue));
            }
        }
    }

    /// <summary>Stable choices whose labels are localized without changing persisted values.</summary>
    public IReadOnlyList<NodePortChoice> Choices { get; init; } = [];

    /// <summary>Whether execution requires a bound value or a configured default.</summary>
    public bool IsRequired { get; set; }

    /// <summary>Whether encapsulation should publish this parameter on the composite interface.</summary>
    public bool ExposeOnComposite { get; set; }

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
    public bool UsesTextEditor => IsTextOrPathPort && !HasChoices && !HasConnectedSource;

    /// <summary>
    /// Whether a non-null initial value worth persisting exists (explicitly set by the user). Output ports and
    /// Flow/Any ports have no initial-value semantics and return <c>false</c>.
    /// <para>是否存在需要持久化的非空初始值（用户显式填写过）。Output 端口或 Flow/Any 端口无初始值语义，返回 <c>false</c>。</para>
    /// </summary>
    public bool HasPersistableInitialValue
        => !IsSensitive && InitialValue is not null
           && IsInput
           && PortType is NodePortType.Path or NodePortType.Text or NodePortType.Number or NodePortType.Boolean;

    /// <summary>Sensitive values are excluded from saved initial values and result previews.</summary>
    public bool IsSensitive
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(DataPreview));
                OnPropertyChanged(nameof(HasPersistableInitialValue));
                OnPropertyChanged(nameof(EditorPasswordChar));
            }
        }
    }

    /// <summary>Masks sensitive literal editing without affecting the underlying value.</summary>
    public char EditorPasswordChar => IsSensitive ? '●' : '\0';

    /// <summary>
    /// Whether this is a text or path input port (renders a text box in the properties panel).
    /// <para>是否为文本或路径输入端口（在属性面板渲染文本框）。</para>
    /// </summary>
    public bool IsTextOrPathPort
        => PortType is NodePortType.Text or NodePortType.Path;

    /// <summary>
    /// Whether this is a path input port (shows a path-browse button).
    /// <para>是否为路径输入端口（显示路径浏览按钮）。</para>
    /// </summary>
    public bool IsPathPort => PortType == NodePortType.Path;

    /// <summary>
    /// Whether this is a numeric input port (renders a numeric box).
    /// <para>是否为数值输入端口（渲染数值框）。</para>
    /// </summary>
    public bool IsNumberPort => PortType == NodePortType.Number;

    /// <summary>
    /// Whether this is a boolean input port (renders a toggle).
    /// <para>是否为布尔输入端口（渲染开关）。</para>
    /// </summary>
    public bool IsBooleanPort => PortType == NodePortType.Boolean;

    /// <summary>
    /// Initial value of a numeric port expressed as <c>double</c>, for properties-panel two-way binding.
    /// <para>数值端口的初始值（double 表示），供属性面板双向绑定。</para>
    /// </summary>
    public double InitialNumberValue
    {
        get => InitialValue is double d ? d : NodeDataUtil.ToDouble(InitialValue, 0);
        set
        {
            InitialValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Initial value of a boolean port, for properties-panel two-way binding.
    /// <para>布尔端口的初始值，供属性面板双向绑定。</para>
    /// </summary>
    public bool InitialBooleanValue
    {
        get => InitialValue is bool b ? b : NodeDataUtil.ToBool(InitialValue, false);
        set
        {
            InitialValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets whether this port is an input port (from the <see cref="ConnectorViewModelBase"/> IsInput semantics).
    /// <para>获取该端口是否为输入端口（来自 <see cref="ConnectorViewModelBase"/> 的 IsInput 属性语义）。</para>
    /// </summary>
    public bool IsInput { get; set; }

    /// <summary>
    /// Gets or sets the port type, used to constrain connection compatibility; defaults to <see cref="NodePortType.Any"/>.
    /// <para>获取或设置该端口的类型，用于约束连接兼容性；默认 <see cref="NodePortType.Any"/>。</para>
    /// </summary>
    public NodePortType PortType { get; set; } = NodePortType.Any;

    /// <summary>Schema-provided display label, independent of runtime endpoint identity.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Localized label override without changing a persisted port title or identity.</summary>
    public string? DisplayResourceKey { get; init; }

    /// <summary>Gets the localized connector name used by the node editor.</summary>
    public string LocalizedTitle
    {
        get
        {
            if (DisplayName is not null)
            {
                return DisplayName;
            }
            var key = DisplayResourceKey ?? "Node_Conn_" + GetResourceSuffix(Title);
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
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>Configured stable id, or a deterministic compatibility id derived from the legacy title.</summary>
    public string StableId => string.IsNullOrWhiteSpace(PortId) ? CreateStableId(Title) : PortId;

    /// <summary>Creates a deterministic ASCII id for legacy connectors that only have a title.</summary>
    public static string CreateStableId(string? title)
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
    /// Gets the display text of the port type (used for the type label on the node icon).
    /// <para>获取端口类型的显示文本（用于节点图标上的类型标签）。</para>
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