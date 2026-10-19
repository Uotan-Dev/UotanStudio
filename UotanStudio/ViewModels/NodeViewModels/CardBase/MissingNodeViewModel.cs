using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Lossless placeholder for a workflow node whose implementation is unavailable.<para>为缺少实现的流程节点保留数据的占位节点。</para></summary>
public sealed class MissingNodeViewModel : FlowNodeViewModel
{
    private readonly string _reason;
    private readonly bool _hasSelectedImage;

    /// <summary>Creates a placeholder while preserving the serialized port surface.<para>创建占位节点并保留序列化的端口结构。</para></summary>
    /// <param name="dto">Serialized node data to carry over.<para>要沿用的序列化节点数据。</para></param>
    /// <param name="reason">Reason reported when execution is attempted.<para>尝试执行时报告的原因。</para></param>
    public MissingNodeViewModel(NodeDto dto, string reason) : base(
        string.IsNullOrWhiteSpace(dto.Title) ? dto.TypeKey : dto.Title,
        Enum.TryParse<NodeCategory>(dto.Category, true, out var category) ? category : NodeCategory.Plugin)
    {
        TypeKey = dto.TypeKey;
        SelectedImage = dto.SelectedImage ?? string.Empty;
        _hasSelectedImage = dto.SelectedImage is not null;
        _reason = reason;
        AddPorts(Input, dto.InputPorts, true);
        AddPorts(Output, dto.OutputPorts, false);
    }

    /// <summary>Marks the node as unavailable because its implementation is missing.<para>因缺少实现而将节点标记为不可用。</para></summary>
    /// <value>Always true.<para>恒为真。</para></value>
    public override bool IsUnavailable => true;

    /// <summary>Reports project-image-node status from the serialized selection.<para>依据序列化的选择项报告项目镜像节点状态。</para></summary>
    /// <value>True when the serialized data carries a selected image.<para>序列化数据带有已选镜像时为真。</para></value>
    public override bool IsProjectImageNode => _hasSelectedImage;

    /// <summary>Fails execution with the recorded unavailability reason.<para>以记录的不适用原因使执行失败。</para></summary>
    /// <param name="context">The ignored execution context.<para>被忽略的执行上下文。</para></param>
    public override Task ExecuteAsync(INodeExecutionContext context)
        => Task.FromException(new InvalidOperationException(_reason));

    private static void AddPorts(ICollection<NodeConnectorViewModel> destination,
        IEnumerable<NodePortDto> ports, bool input)
    {
        foreach (var port in ports)
        {
            destination.Add(new NodeConnectorViewModel
            {
                PortId = port.Id,
                Title = port.Title,
                IsInput = input,
                PortType = Enum.TryParse<NodePortType>(port.PortType, true, out var type) ? type : NodePortType.Any,
                MaxConnections = port.MaxConnections
            });
        }
    }
}