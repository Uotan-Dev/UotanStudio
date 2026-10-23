namespace UotanStudio.Workflow.Nodes;

public partial class NodeConnectorViewModel
{
    /// <summary>Whether this parameter is hidden until advanced options are requested.<para>该参数是否在请求高级选项前保持隐藏。</para></summary>
    /// <value>The advanced-option flag.<para>高级选项标记。</para></value>
    public bool IsAdvanced { get; set; }

    /// <summary>Whether this input configures an operation rather than carrying a resource.<para>该输入是配置操作而非承载资源。</para></summary>
    /// <value>The configuration-input flag.<para>配置型输入标记。</para></value>
    public bool IsConfiguration { get; set; }

    /// <summary>Schema-owned relevance rule evaluated against the owning node.<para>依据模式定义、针对所属节点求值的相关性规则。</para></summary>
    /// <value>The visibility predicate, or null when always relevant.<para>可见性谓词，始终相关时为空。</para></value>
    public Func<FlowNodeViewModel, bool>? IsVisibleWhen { get; set; }

    /// <summary>Description of the connected upstream source.<para>已连接上游来源的描述。</para></summary>
    /// <value>The upstream source text, empty when unconnected.<para>上游来源文本，未连接时为空。</para></value>
    public string SourceDescription
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasConnectedSource));
                OnPropertyChanged(nameof(UsesTextEditor));
                OnPropertyChanged(nameof(CanEditChoice));
                OnPropertyChanged(nameof(CanEditNumber));
                OnPropertyChanged(nameof(CanEditBoolean));
                OnPropertyChanged(nameof(CanBrowsePath));
            }
        }
    } = string.Empty;

    public bool HasConnectedSource => SourceDescription.Length > 0;
    public bool CanEditChoice => HasChoices && !HasConnectedSource;
    public bool CanEditNumber => IsNumberPort && !HasConnectedSource;
    public bool CanEditBoolean => IsBooleanPort && !HasConnectedSource;
    public bool CanBrowsePath => IsPathPort && !HasConnectedSource;
}