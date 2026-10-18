using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Value source node view model: one node that produces either a fixed value or the absolute path of the image
/// selected in the project image panel, chosen by a <c>Source</c> selector.
/// <para>值来源节点视图模型：由 <c>Source</c> 选择器决定节点输出一个固定值，或输出工程镜像面板中选中镜像的绝对路径。</para>
/// </summary>
public class ValueSourceNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Stable node type key of the merged value source node.
    /// <para>合并后的值来源节点的稳定类型键。</para>
    /// </summary>
    public const string ValueSourceTypeKey = "Data.Value.Source";

    private const string SourceConstant = "Constant";
    private const string SourceProjectImage = "ProjectImage";
    private const string SourcePortId = "source";
    private const string ValuePortId = "value";
    private const string ValueTypePortId = "value-type";
    private const string OutputPortId = "value-output";

    private IReadOnlyList<string>? _cachedAvailableImages;

    /// <summary>
    /// Creates a value source node with the specified title.
    /// <para>创建具有指定标题的值来源节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public ValueSourceNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
        if (ImageViewModel.Instance?.Images is { } images)
        {
            images.CollectionChanged += (_, _) => _cachedAvailableImages = null;
        }
    }

    /// <summary>
    /// Creates a default value source node that outputs a <c>Text</c>-typed constant; switching the source to
    /// <c>ProjectImage</c> makes it output the selected project image path instead.
    /// <para>创建默认的值来源节点，输出 <c>Text</c> 类型常量；把来源切换为 <c>ProjectImage</c> 后改为输出选中工程镜像的绝对路径。</para>
    /// </summary>
    /// <returns>
    /// The configured node with its <c>Source</c>, <c>Value</c> and <c>Value Type</c> inputs and its <c>Value</c> output.
    /// <para>配置完成的节点，含 <c>Source</c>、<c>Value</c>、<c>Value Type</c> 三个输入与 <c>Value</c> 输出。</para>
    /// </returns>
    public static ValueSourceNodeViewModel CreateValueSourceNode()
    {
        var node = new ValueSourceNodeViewModel("Value Source")
        {
            Location = new Point(100, 100),
            TypeKey = ValueSourceTypeKey,
            NodeValueType = "Text"
        };
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Source",
            PortId = SourcePortId,
            PortType = NodePortType.Text,
            InitialValue = SourceConstant,
            Choices = [new NodePortChoice(SourceConstant), new NodePortChoice(SourceProjectImage)]
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Value",
            PortId = ValuePortId,
            PortType = NodePortType.Text,
            IsVisibleWhen = IsConstantSource
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Value Type",
            PortId = ValueTypePortId,
            PortType = NodePortType.Text,
            InitialValue = "Text",
            IsVisibleWhen = IsConstantSource,
            Choices = [.. ValueTypeOptions.Select(value => new NodePortChoice(value, "Node_PortType_"))]
        });
        node.Output.Add(new NodeConnectorViewModel
        {
            Title = "Value",
            PortId = OutputPortId,
            PortType = NodePortType.Text
        });
        return node;
    }

    /// <summary>
    /// Gets whether the constant editor applies; the properties panel shows it while the source is <c>Constant</c>.
    /// <para>获取常量编辑区是否适用；来源为 <c>Constant</c> 时属性面板据此显示常量编辑区。</para>
    /// </summary>
    public override bool IsConstantNode => IsConstantSource(this);

    /// <summary>
    /// Gets whether the project image picker applies; the properties panel shows it while the source is
    /// <c>ProjectImage</c>.
    /// <para>获取工程镜像选择区是否适用；来源为 <c>ProjectImage</c> 时属性面板据此显示镜像选择编辑区。</para>
    /// </summary>
    public override bool IsProjectImageNode => !IsConstantSource(this);

    /// <summary>
    /// Gets the project image relative path selected by this node. The single <see cref="FlowNodeViewModel.SelectedImage"/>
    /// slot carries the value for either source, so the edited selection survives a switch between sources and round
    /// trips through project persistence.
    /// <para>获取该节点选中的工程镜像相对路径。两种来源共用 <see cref="FlowNodeViewModel.SelectedImage"/> 这一个存放位，
    /// 因此切换来源以及工程持久化往返都不会丢失已选镜像。</para>
    /// </summary>
    public override string SelectedImage
    {
        get => base.SelectedImage;
        set
        {
            if (IsEditingLocked || string.Equals(base.SelectedImage, value, StringComparison.Ordinal))
            {
                return;
            }
            base.SelectedImage = value;
            OnPropertyChanged(nameof(SelectedImage));
        }
    }

    /// <summary>
    /// Gets the relative image paths available in the project image panel (each taken from
    /// <see cref="ImageEntryViewModel.RelativePath"/>).
    /// <para>获取工程镜像面板中的可用镜像相对路径列表（每项取自 <see cref="ImageEntryViewModel.RelativePath"/>）。</para>
    /// </summary>
    public override IReadOnlyList<string> AvailableImages => _cachedAvailableImages ??= ComputeAvailableImages();

    /// <summary>
    /// Gets or sets the constant's value type (Text/Number/Boolean/Path); the output port type follows while the
    /// source is <c>Constant</c>. The type is accepted for either source so that switching back to <c>Constant</c>
    /// restores the chosen port type.
    /// <para>获取或设置常量值的类型（Text/Number/Boolean/Path）；来源为 <c>Constant</c> 时输出端口类型随之变化。
    /// 两种来源都接受该类型，以便切回 <c>Constant</c> 时恢复所选端口类型。</para>
    /// </summary>
    public override string NodeValueType
    {
        get => base.NodeValueType;
        set
        {
            if (IsEditingLocked || string.Equals(base.NodeValueType, value, StringComparison.Ordinal))
            {
                return;
            }
            base.NodeValueType = value;
            ApplyOutputPortType();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the constant value written while the source is <c>Constant</c>. The single
    /// <see cref="FlowNodeViewModel.NodeValue"/> slot carries the value for either source so that the configured text
    /// survives switching to <c>ProjectImage</c> and project persistence.
    /// <para>获取或设置来源为 <c>Constant</c> 时写出的常量值。两种来源共用 <see cref="FlowNodeViewModel.NodeValue"/>
    /// 这一个存放位，因此切到 <c>ProjectImage</c> 以及工程持久化后配置文本仍然保留。</para>
    /// </summary>
    public override string NodeValue
    {
        get => base.NodeValue;
        set
        {
            if (IsEditingLocked || string.Equals(base.NodeValue, value, StringComparison.Ordinal))
            {
                return;
            }
            base.NodeValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Executes the node by its <c>Source</c> selector: <c>Constant</c> writes the configured value resolved by its
    /// value type, <c>ProjectImage</c> writes the absolute path of the selected project image.
    /// <para>按 <c>Source</c> 选择器执行节点：<c>Constant</c> 按值类型解析并写出配置值，<c>ProjectImage</c> 写出选中工程镜像的绝对路径。</para>
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// <para>执行上下文。</para>
    /// </param>
    /// <returns>
    /// A task representing the asynchronous execution.
    /// <para>表示异步执行的任务。</para>
    /// </returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        if (IsConstantSource(this))
        {
            ExecuteConstant(context);
        }
        else
        {
            ExecuteProjectImage(context);
        }
        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the configured value type to the output port type; a value type the node does not publish falls back
    /// to text, matching the constant node contract.
    /// <para>把配置的值类型解析为输出端口类型；节点未发布的值类型回退为文本，与常量节点的约定一致。</para>
    /// </summary>
    /// <param name="typeName">The value type name. <para>值类型名。</para></param>
    /// <returns>The matching port type. <para>对应的端口类型。</para></returns>
    private static NodePortType ResolvePortType(string? typeName) => typeName switch
    {
        "Number" => NodePortType.Number,
        "Boolean" => NodePortType.Boolean,
        "Path" => NodePortType.Path,
        _ => NodePortType.Text
    };

    /// <summary>
    /// The source selector is decided at run time; a connected upstream may select <c>Constant</c>, so the constant
    /// inputs stay visible. Only the explicit <c>ProjectImage</c> selection hides them.
    /// <para>来源由运行期决定；连接上游时上游可能选择 <c>Constant</c>，因此常量输入保持可见。只有显式选择
    /// <c>ProjectImage</c> 时才隐藏。</para>
    /// </summary>
    /// <param name="node">The node owning the ports. <para>拥有这些端口的节点。</para></param>
    /// <returns><c>true</c> when the constant inputs apply. <para>常量输入适用时为 <c>true</c>。</para></returns>
    private static bool IsConstantSource(FlowNodeViewModel node)
    {
        if (NodeAt(node, "Source") is { } selector && selector.HasConnectedSource)
        {
            return true;
        }
        return !string.Equals(ReadSource(node), SourceProjectImage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the effective source of a node: the live input value when present, otherwise the connector initial value.
    /// <para>读取节点的有效来源：优先取输入数据，缺省时取连接器初始值。</para>
    /// </summary>
    /// <param name="node">The node to read. <para>要读取的节点。</para></param>
    /// <returns>The source name; unknown sources fall back to <c>Constant</c>. <para>来源名称；未知来源回退为 <c>Constant</c>。</para></returns>
    private static string ReadSource(FlowNodeViewModel node)
    {
        if (NodeAt(node, "Source") is not { } selector)
        {
            return SourceConstant;
        }
        var text = Trimmed(selector.Data) ?? Trimmed(selector.InitialValue);
        return text is SourceProjectImage ? SourceProjectImage : SourceConstant;
    }

    private static NodeConnectorViewModel? NodeAt(FlowNodeViewModel node, string title)
        => node.Input.FirstOrDefault(port => string.Equals(port.Title, title, StringComparison.Ordinal));

    private static string? Trimmed(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private void ApplyOutputPortType()
    {
        if (Output.Count > 0)
        {
            var portType = IsConstantSource(this) ? ResolvePortType(base.NodeValueType) : NodePortType.Path;
            if (Output[0].PortType != portType)
            {
                Output[0].PortType = portType;
            }
        }
    }

    private IReadOnlyList<string> ComputeAvailableImages()
        => ImageViewModel.Instance?.Images.Select(image => image.RelativePath).ToList() ?? [];

    private void ExecuteConstant(INodeExecutionContext context)
    {
        var value = Trimmed(ReadInput("Value")) ?? NodeValue;
        var valueType = NodeValueType;
        object? result = valueType switch
        {
            "Number" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            "Boolean" when bool.TryParse(value, out var boolean) => boolean,
            _ => value
        };
        WriteOutput("Value", result);
        context.Log.Write(LogLevel.Info, Title, $"Constant '{valueType}': {value}  已输出常量。");
    }

    private void ExecuteProjectImage(INodeExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(SelectedImage))
        {
            throw new InvalidOperationException("No project image was selected.");
        }

        var root = ProjectContextService.Instance.RootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("No project is open.");
        }

        var absolutePath = Path.Combine(root, SelectedImage);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The selected project image was not found.", absolutePath);
        }
        WriteOutput("Value", absolutePath);
        context.Log.Write(LogLevel.Info, Title, $"Image path: {absolutePath}  已输出镜像路径。");
    }
}