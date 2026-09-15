using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using NodifyM.Avalonia.ViewModelBase;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>节点视图模型，表示节点编辑器画布上的一个节点。实现 INodePosition 接口以支持节点定位。</para>
/// View model for a node, representing a single node on the node editor canvas. Implements INodePosition for node positioning.
/// </summary>
public partial class FlowNodeViewModel : ViewModelBase, INodePosition
{
    private string _title = string.Empty;
    private Point _location;
    private NodeCategory _category;
    private bool _isSelected;
    private NodeExecutionStatus _executionStatus = NodeExecutionStatus.Idle;
    private string _typeKey = string.Empty;

    /// <summary>
    /// <para>节点实例唯一标识。</para>
    /// Unique identifier of the node instance.
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// <para>获取或设置节点的显示标题。</para>
    /// Gets or sets the display title of the node.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// <para>获取或设置节点在画布上的位置。</para>
    /// Gets or sets the position of the node on the canvas.
    /// </summary>
    public Point Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    /// <summary>
    /// <para>获取或设置节点的类别。</para>
    /// Gets or sets the category of the node.
    /// </summary>
    public NodeCategory Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    /// <summary>
    /// <para>获取或设置节点是否被选中。</para>
    /// Gets or sets whether the node is selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// <para>获取或设置节点执行状态。</para>
    /// Gets or sets the node execution status.
    /// </summary>
    public NodeExecutionStatus ExecutionStatus
    {
        get => _executionStatus;
        set
        {
            if (SetProperty(ref _executionStatus, value))
            {
                OnPropertyChanged(nameof(StatusBorderBrush));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    /// <summary>
    /// <para>获取当前执行状态对应的边框画刷，用于画布节点高亮（Idle 透明、Running 主题强调色、
    /// Succeeded 绿、Failed 红、Cancelled/Skipped/Pending 灰）。</para>
    /// Gets the border brush for the current execution status, used to highlight nodes on the canvas
    /// (Idle transparent, Running theme accent, Succeeded green, Failed red, Cancelled/Skipped/Pending gray).
    /// </summary>
    public IBrush StatusBorderBrush => ExecutionStatus switch
    {
        NodeExecutionStatus.Running => AccentBorderBrush,
        NodeExecutionStatus.Succeeded => SuccessBorderBrush,
        NodeExecutionStatus.Failed => FailureBorderBrush,
        NodeExecutionStatus.Cancelled or NodeExecutionStatus.Skipped or NodeExecutionStatus.Pending => MutedBorderBrush,
        _ => IdleBorderBrush
    };

    /// <summary>
    /// <para>获取本地化的执行状态文本（如 “运行中” / “Running”）。</para>
    /// Gets the localized execution status text (e.g. “运行中” / “Running”).
    /// </summary>
    public string StatusDisplay => LanguageService.Instance.GetString("Node_Status_" + ExecutionStatus);

    private static readonly IBrush IdleBorderBrush = Brushes.Transparent;
    private static readonly IBrush SuccessBorderBrush = new SolidColorBrush(Colors.Green);
    private static readonly IBrush FailureBorderBrush = new SolidColorBrush(Colors.Red);
    private static readonly IBrush MutedBorderBrush = new SolidColorBrush(Colors.Gray);

    /// <summary>
    /// <para>从当前主题解析强调色画刷（近似 DynamicResource 语义），主题切换后重新求值。</para>
    /// Resolves the accent-color brush from the current theme (approximating DynamicResource semantics); re-evaluated after a theme switch.
    /// </summary>
    private static IBrush AccentBorderBrush
    {
        get
        {
            if (Application.Current is { } app
                && app.TryGetResource("AccentColor", app.ActualThemeVariant, out var value)
                && value is Color color)
                return new SolidColorBrush(color);
            // 主题色兜底值：与 Themes/AccentColors.axaml 的 AccentColor 保持一致。
            // Accent fallback, kept in sync with AccentColor in Themes/AccentColors.axaml.
            return new SolidColorBrush(Color.Parse("#FF4E45"));
        }
    }

    /// <summary>
    /// <para>获取或设置稳定节点类型键（对应 <see cref="NodeTypeRegistry"/> 注册键），用于工程持久化与加载重建。</para>
    /// Gets or sets the stable node type key (matching a <see cref="NodeTypeRegistry"/> key), used for
    /// project persistence and load-time reconstruction.
    /// </summary>
    public string TypeKey
    {
        get => _typeKey;
        set => SetProperty(ref _typeKey, value);
    }

    /// <summary>
    /// <para>获取或设置节点可配置值的类型（Text/Number/Boolean/Path），供常量等可配置节点在属性面板编辑。
    /// 默认 <c>Text</c>；非可配置节点忽略。</para>
    /// Gets or sets the configurable value type of the node (Text/Number/Boolean/Path), edited in the properties
    /// panel for configurable nodes such as constants. Defaults to <c>Text</c>; ignored by non-configurable nodes.
    /// </summary>
    public virtual string NodeValueType { get; set; } = "Text";

    /// <summary>
    /// <para>获取或设置节点可配置的值，供常量等可配置节点在属性面板编辑；非可配置节点忽略。</para>
    /// Gets or sets the configurable value of the node, edited in the properties panel for configurable nodes;
    /// ignored by non-configurable nodes.
    /// </summary>
    public virtual string NodeValue { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取节点是否为常量输入节点（属性面板据此显示常量编辑区）。</para>
    /// Gets whether the node is a constant-input node (the properties panel shows the constant editor for it).
    /// </summary>
    public virtual bool IsConstantNode => false;

    /// <summary>
    /// <para>获取节点是否为工程镜像引用节点（属性面板据此显示镜像选择编辑区）。</para>
    /// Gets whether the node is a project-image reference node (the properties panel shows the image picker for it).
    /// </summary>
    public virtual bool IsProjectImageNode => false;

    /// <summary>
    /// <para>获取或设置选中的工程镜像相对路径（ProjectImage 节点属性面板绑定）；非镜像节点忽略。</para>
    /// Gets or sets the selected project-image relative path (bound by the ProjectImage node's properties
    /// panel); ignored by non-image nodes.
    /// </summary>
    public virtual string SelectedImage { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取可选的工程镜像相对路径列表（ProjectImage 节点属性面板绑定）；非镜像节点返回空列表。</para>
    /// Gets the selectable project-image relative paths (bound by the ProjectImage node's properties panel);
    /// non-image nodes return an empty list.
    /// </summary>
    public virtual IReadOnlyList<string> AvailableImages => [];

    /// <summary>
    /// <para>可配置值类型的可选项（Text/Number/Boolean/Path），供属性面板下拉框使用。</para>
    /// The selectable value types (Text/Number/Boolean/Path), used by the properties-panel combo box.
    /// </summary>
    public static IReadOnlyList<string> ValueTypeOptions { get; } = ["Text", "Number", "Boolean", "Path"];

    /// <summary>
    /// <para>获取节点的输入连接器集合。</para>
    /// Gets the collection of input connectors for the node.
    /// </summary>
    public ObservableCollection<NodeConnectorViewModel> Input { get; } = [];

    /// <summary>
    /// <para>获取节点的输出连接器集合。</para>
    /// Gets the collection of output connectors for the node.
    /// </summary>
    public ObservableCollection<NodeConnectorViewModel> Output { get; } = [];

    /// <summary>
    /// <para>创建具有指定标题和类别的节点。</para>
    /// Creates a node with the specified title and category.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    /// <param name="category">
    /// <para>节点的类别。</para>
    /// The category of the node.
    /// </param>
    public FlowNodeViewModel(string title, NodeCategory category)
    {
        _title = title;
        _category = category;
    }

    /// <summary>
    /// <para>按端口标题读取输入值。若节点不存在同标题输入端口，返回 <c>default</c>。</para>
    /// Reads the input value by connector title. Returns the default value if no input connector matches.
    /// </summary>
    /// <param name="title">
    /// <para>输入端口标题。</para>
    /// The input connector title.
    /// </param>
    /// <returns>
    /// <para>端口承载的值；无匹配端口时返回 <c>null</c>。</para>
    /// The value carried by the port; <c>null</c> if no matching port exists.
    /// </returns>
    public object? ReadInput(string title)
    {
        var connector = Input.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.Ordinal));
        return connector?.Data;
    }

    /// <summary>
    /// <para>按端口标题把结果写入输出端口。</para>
    /// Writes a result to the output port by connector title.
    /// </summary>
    /// <param name="title">
    /// <para>输出端口标题。</para>
    /// The output connector title.
    /// </param>
    /// <param name="value">
    /// <para>要写入的值。</para>
    /// The value to write.
    /// </param>
    public void WriteOutput(string title, object? value)
    {
        var connector = Output.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.Ordinal));
        if (connector != null)
        {
            connector.Data = value;
        }
    }

    /// <summary>
    /// <para>执行节点业务逻辑。子类应重写此方法，通过 <see cref="ReadInput"/> 读取输入值并调用 <see cref="WriteOutput"/> 写入结果；默认实现仅记录一条日志。</para>
    /// Executes the node business logic. Subclasses should override this method, reading inputs via <see cref="ReadInput"/>
    /// and writing results via <see cref="WriteOutput"/>; the default implementation only writes a log entry.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文，提供日志、取消令牌与进度报告。</para>
    /// The execution context, providing logging, a cancellation token, and progress reporting.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public virtual Task ExecuteAsync(INodeExecutionContext context)
    {
        context.Log.Write(LogLevel.Info, Title, $"Node '{Title}' has no execution logic (no-op).  节点无执行逻辑。");
        return Task.CompletedTask;
    }
}