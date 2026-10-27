using System.Text;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.Services;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

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

    /// <summary>Bounded display of an execution result; binary buffers are never formatted as text.
    /// <para>执行结果的有界显示；二进制缓冲区从不按文本格式化。</para>
    /// </summary>
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

    /// <summary>
    /// Gets or sets the stable choices whose labels are localized without changing persisted values. The list is
    /// assigned after construction when the available options depend on process state rather than on the port
    /// declaration, so the dependent editor flags are raised here as well.
    /// <para>获取或设置稳定选项列表，标签本地化不影响持久化取值。可选值依赖进程状态而非端口声明时，列表会在构造之后才被赋值，
    /// 因此依赖它的编辑器标志也在这里一并通知。</para>
    /// </summary>
    /// <value>The current choice snapshot; empty when the port uses a text editor.
    /// <para>当前选项快照；端口使用文本编辑器时为空。</para></value>
    public IReadOnlyList<NodePortChoice> Choices
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value ?? []))
            {
                return;
            }
            OnPropertyChanged(nameof(HasChoices));
            OnPropertyChanged(nameof(SelectedChoice));
            OnPropertyChanged(nameof(UsesTextEditor));
            OnPropertyChanged(nameof(CanEditChoice));
        }
    } = [];

    /// <summary>Locale suffix used by every localized choice label.</summary>
    private const string DefaultChoiceResourcePrefix = "Node_Choice_";

    /// <summary>
    /// Makes <see cref="Choices"/> track the process-level resource candidates. The snapshot is taken at once and
    /// every later publication refreshes the list with the dependent editor flags; because the source keeps weak
    /// interest, a released port is collected without explicit unsubscription.
    /// <para>让 <see cref="Choices"/> 跟踪进程级资源候选：立即取一次快照，此后每次发布都会连同依赖的编辑器标志一起刷新列表；
    /// 来源只保留弱引用，已释放的端口无需显式退订即可回收。</para>
    /// </summary>
    internal void TrackResourceChoices()
    {
        Choices = NodePortChoiceSource.Resources;
        NodePortChoiceSource.Track(this);
    }

    /// <summary>Receives a replacement of the process-level resource candidates.</summary>
    /// <param name="choices">The newly published candidates.
    /// <para>新发布的候选。</para></param>
    internal void ApplyTrackedChoices(IReadOnlyList<NodePortChoice> choices) => Choices = choices;

    /// <summary>Whether execution requires a bound value or a configured default.
    /// <para>执行时是否必须有绑定值或已配置默认值。</para>
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Gets or sets the control-flow role of a flow output port. The engine reads this role instead of the port
    /// title, so renaming a branch output never changes dispatch and a plugin node can declare the same roles.
    /// <para>获取或设置流输出端口的控制流角色。引擎读取该角色而非端口标题，因此重命名分支输出不会改变分派，
    /// 插件节点也能声明相同角色。</para>
    /// </summary>
    /// <value>The declared role; <see cref="NodeFlowRole.None"/> for non-flow or role-free ports.
    /// <para>声明的角色；非流端口或无角色端口为 <see cref="NodeFlowRole.None"/>。</para></value>
    public NodeFlowRole FlowRole { get; set; }

    /// <summary>Whether encapsulation should publish this parameter on the composite interface.
    /// <para>封装时是否把该参数发布到组合节点接口。</para>
    /// </summary>
    public bool ExposeOnComposite { get; set; }

    /// <summary>Whether this input uses a selection editor.
    /// <para>该输入是否使用选择编辑器。</para>
    /// </summary>
    public bool HasChoices => Choices.Count > 0;

    /// <summary>Optional schema-owned guidance; it is not part of persisted workflow data.
    /// <para>由 schema 提供的可选说明；不属于持久化的工作流数据。</para>
    /// </summary>
    public string? HintResourceKey { get; init; }

    /// <summary>Optional localized display mapping for stable string output values.
    /// <para>稳定字符串输出值的可选本地化显示映射。</para>
    /// </summary>
    public string? ValueResourcePrefix { get; init; }

    /// <summary>Whether this connector has format-specific editing guidance.
    /// <para>该连接器是否有特定于格式的编辑说明。</para>
    /// </summary>
    public bool HasHint => HintResourceKey is not null;

    /// <summary>Localized guidance describing the accepted value or format dialect.
    /// <para>描述可接受取值或格式方言的本地化说明。</para>
    /// </summary>
    public string LocalizedHint => HintResourceKey is null ? string.Empty : LanguageService.Instance.GetString(HintResourceKey);

    /// <summary>Selected option mapped to the ordinary persisted initial value.
    /// <para>选中项到普通持久化初始值的映射。</para>
    /// </summary>
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

    /// <summary>Whether text accepts line breaks in the properties editor.
    /// <para>属性编辑器中文本是否接受换行。</para>
    /// </summary>
    public bool IsMultiline { get; init; }

    /// <summary>Minimum height for single-line or multiline input.
    /// <para>单行或多行输入的最小高度。</para>
    /// </summary>
    public double EditorMinHeight => IsMultiline ? 76 : 24;

    /// <summary>Whether the browse action must select a folder.
    /// <para>浏览操作是否必须选择目录。</para>
    /// </summary>
    public bool IsDirectoryPath { get; init; }

    /// <summary>Suggested suffix used by output file pickers.
    /// <para>输出文件选择器使用的建议后缀。</para>
    /// </summary>
    public string OutputExtension { get; init; } = "img";

    /// <summary>Default output name when no path has been entered.
    /// <para>未输入路径时使用的默认输出名。</para>
    /// </summary>
    public string SuggestedOutputName { get; init; } = "super.img";

    /// <summary>Whether a free-text editor is used rather than a choice editor.
    /// <para>是否使用自由文本编辑器而非选项编辑器。</para>
    /// </summary>
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

    /// <summary>Sensitive values are excluded from saved initial values and result previews.
    /// <para>敏感值不写入保存的初始值，也不出现在结果预览中。</para>
    /// </summary>
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

    /// <summary>Masks sensitive literal editing without affecting the underlying value.
    /// <para>对敏感字面量的编辑做遮蔽，不改变底层取值。</para>
    /// </summary>
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

    /// <summary>Schema-provided display label, independent of runtime endpoint identity.
    /// <para>schema 提供的显示标签，独立于运行时端点标识。</para>
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>Localized label override without changing a persisted port title or identity.
    /// <para>本地化标签覆盖，不改变已持久化的端口标题或标识。</para>
    /// </summary>
    public string? DisplayResourceKey { get; init; }

    /// <summary>Gets the localized connector name used by the node editor.
    /// <para>获取节点编辑器使用的本地化连接器名称。</para>
    /// </summary>
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

    /// <summary>Gets the localized port data type used by the node editor.
    /// <para>获取节点编辑器使用的本地化端口数据类型。</para>
    /// </summary>
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

    /// <summary>Stable non-localized identifier used by portable workflows and visual plugins.
    /// <para>可移植工作流与可视化插件使用的稳定非本地化标识。</para>
    /// </summary>
    public string PortId
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// Configured stable id, or a deterministic compatibility id derived from the title.
    /// <para>已配置的稳定 id；未配置时为由标题推导的确定性兼容 id。</para>
    /// </summary>
    public string StableId => string.IsNullOrWhiteSpace(PortId) ? CreateStableId(Title) : PortId;

    /// <summary>
    /// Creates a deterministic ASCII id for connectors that only have a title.
    /// <para>为只有标题的连接器生成确定性 ASCII id。</para>
    /// </summary>
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