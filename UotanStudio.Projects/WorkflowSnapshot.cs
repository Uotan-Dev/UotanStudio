using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>
/// <para>工作流快照 JSON 序列化上下文（AOT 源生成）。</para>
/// Workflow snapshot JSON serialization context (AOT source-generated).
/// </summary>
[JsonSerializable(typeof(WorkflowSnapshot))]
[JsonSerializable(typeof(List<NodeDto>))]
[JsonSerializable(typeof(List<ConnectionDto>))]
[JsonSerializable(typeof(List<NodePortDto>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class WorkflowJsonContext : JsonSerializerContext;

/// <summary>
/// <para>工作流快照，承载工程文件的完整结构。</para>
/// Workflow snapshot, carrying the complete structure of the project file.
/// </summary>
public sealed class WorkflowSnapshot
{
    /// <summary>
    /// Workflow schema using the redesigned node contract.
    /// <para>采用重新设计节点契约的工作流格式版本。</para>
    /// </summary>
    public const int CurrentFormatVersion = 1;
    /// <summary>
    /// <para>工程文件格式版本。</para>
    /// Project file format version.
    /// </summary>
    /// <value>
    /// Version stamped into the serialized file; defaults to <see cref="CurrentFormatVersion"/>.
    /// <para>写入序列化文件的版本号；默认为 <see cref="CurrentFormatVersion"/>。</para>
    /// </value>
    [JsonRequired]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>
    /// Rejects unsupported workflow contracts before reconstruction or publication.
    /// <para>在重建或发布之前拒绝不受支持的工作流契约。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The format version differs from <see cref="CurrentFormatVersion"/>, or a node's embedded composite fails its identity or structural validation.
    /// <para>格式版本与 <see cref="CurrentFormatVersion"/> 不一致，或节点内嵌复合包未通过身份与结构校验。</para>
    /// </exception>
    public void EnsureSupportedVersion()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported workflow format version {FormatVersion}; expected {CurrentFormatVersion}.");
        }
        foreach (var node in Nodes)
        {
            if (node.CompositePackage is not { } package)
            {
                continue;
            }
            var errors = VisualPluginValidation.Validate(package);
            if (package.NodeTypeKey != node.TypeKey || errors.Count != 0)
            {
                throw new InvalidDataException("Invalid embedded composite: " + string.Join(Environment.NewLine, errors));
            }
        }
    }

    /// <summary>
    /// <para>节点 DTO 列表。</para>
    /// List of node DTOs.
    /// </summary>
    /// <value>
    /// All captured canvas nodes; an empty list when the workflow has none.
    /// <para>所有已捕获的画布节点；工作流没有节点时为空列表。</para>
    /// </value>
    public List<NodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// <para>连接 DTO 列表。</para>
    /// List of connection DTOs.
    /// </summary>
    /// <value>
    /// Edges between node ports; each endpoint may be identified by stable port id or by connector title.
    /// <para>节点端口之间的连线；每个端点可通过稳定端口 ID 或连接器标题识别。</para>
    /// </value>
    public List<ConnectionDto> Connections { get; set; } = [];
}

/// <summary>
/// <para>节点数据传输对象，仅持久化必要字段。</para>
/// Node data transfer object, persisting only essential fields.
/// </summary>
public sealed class NodeDto
{
    /// <summary>
    /// <para>节点实例 ID。</para>
    /// Node instance ID.
    /// </summary>
    /// <value>Identifier unique within the snapshot that connections bind to. <para>快照内唯一、供连接绑定的标识符。</para></value>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// <para>节点标题。</para>
    /// Node title.
    /// </summary>
    /// <value>
    /// Displayed node title, also used to match connections when stable port ids are absent.
    /// <para>展示的节点标题；当连接缺少稳定端口 ID 时也用于匹配。</para>
    /// </value>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// <para>节点类别字符串。</para>
    /// Node category string.
    /// </summary>
    /// <value>Palette grouping string. <para>用于分组展示的类别字符串。</para></value>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// <para>画布 X 坐标。</para>
    /// Canvas X coordinate.
    /// </summary>
    /// <value>Horizontal canvas position in layout units. <para>以布局单位表示的画布水平位置。</para></value>
    public double X { get; set; }

    /// <summary>
    /// <para>画布 Y 坐标。</para>
    /// Canvas Y coordinate.
    /// </summary>
    /// <value>Vertical canvas position in layout units. <para>以布局单位表示的画布垂直位置。</para></value>
    public double Y { get; set; }

    /// <summary>
    /// <para>稳定节点类型键，对应 NodeTypeRegistry 注册键</para>
    /// Stable node type key matching a NodeTypeRegistry key.
    /// </summary>
    /// <value>
    /// Registry key of the node implementation; an unregistered key keeps the node and its ports as a placeholder.
    /// <para>节点实现的注册表键；未注册的键会将节点及其端口以占位形式保留。</para>
    /// </value>
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>
    /// Version-pinned definition of a locally encapsulated composite.
    /// <para>对本地封装复合节点进行版本锁定的定义。</para>
    /// </summary>
    /// <value>
    /// Embedded package when the node hosts a composite, <see langword="null"/> for ordinary nodes.
    /// <para>节点承载复合体时为内嵌插件包；普通节点为 <see langword="null"/>。</para>
    /// </value>
    public VisualPluginPackage? CompositePackage { get; set; }

    /// <summary>
    /// Configurable node value type, when the node exposes one.
    /// <para>节点暴露可配置值时的值类型。</para>
    /// </summary>
    /// <value>
    /// Type discriminator of the configurable value; <see langword="null"/> when the node exposes none.
    /// <para>可配置值的类型判别符；节点未暴露可配置值时为 <see langword="null"/>。</para>
    /// </value>
    public string? NodeValueType { get; set; }

    /// <summary>
    /// Configurable node value, when the node exposes one.
    /// <para>节点暴露的可配置值本身。</para>
    /// </summary>
    /// <value>
    /// Configured value stored as text, interpreted through <see cref="NodeValueType"/>; <see langword="null"/> when unset.
    /// <para>以文本存储的配置值，需结合 <see cref="NodeValueType"/> 解释；未设置时为 <see langword="null"/>。</para>
    /// </value>
    public string? NodeValue { get; set; }

    /// <summary>
    /// Selected project-image relative path, when the node references an image.
    /// <para>节点引用镜像时所选的项目镜像相对路径。</para>
    /// </summary>
    /// <value>
    /// Path relative to the project root; <see langword="null"/> when the node selects no image.
    /// <para>相对项目根目录的路径；节点未选择镜像时为 <see langword="null"/>。</para>
    /// </value>
    public string? SelectedImage { get; set; }

    /// <summary>
    /// User-defined display name; null keeps the localized node title.
    /// <para>用户自定义显示名；为 null 时保留本地化节点标题。</para>
    /// </summary>
    /// <value>
    /// Explicit override for the node caption; <see langword="null"/> lets the localized title win.
    /// <para>节点标题的显式覆盖值；为 <see langword="null"/> 时以本地化标题为准。</para>
    /// </value>
    public string? CustomName { get; set; }

    /// <summary>
    /// <para>输入端口用户编辑初始值映射（端口标题 → 字符串值）。仅持久化用户显式填写的值。</para>
    /// User-edited initial values of input ports (port title → string value). Only explicitly set values are persisted.
    /// </summary>
    /// <value>
    /// Title-keyed map of edited values; <see langword="null"/> when nothing was edited.
    /// <para>以端口标题为键的编辑值映射；未编辑任何值时为 <see langword="null"/>。</para>
    /// </value>
    public Dictionary<string, string>? InputInitialValues { get; set; }

    /// <summary>
    /// User-edited initial values keyed by stable input port id.
    /// <para>以稳定输入端口 ID 为键的用户编辑初始值。</para>
    /// </summary>
    /// <value>
    /// Port-id-keyed map that survives title re-localization; <see langword="null"/> when nothing was edited.
    /// <para>以端口 ID 为键的映射，可在标题重新本地化后继续生效；未编辑任何值时为 <see langword="null"/>。</para>
    /// </value>
    public Dictionary<string, string>? InputInitialValuesByPortId { get; set; }

    /// <summary>
    /// Input schema retained so unavailable node types can be represented without data loss.
    /// <para>保留输入端口架构，使不可用的节点类型也能无损呈现。</para>
    /// </summary>
    /// <value>
    /// Persisted input ports of this node; empty when the node declares none.
    /// <para>该节点已持久化的输入端口；没有声明时为空列表。</para>
    /// </value>
    public List<NodePortDto> InputPorts { get; set; } = [];

    /// <summary>
    /// Output schema retained so unavailable node types can be represented without data loss.
    /// <para>保留输出端口架构，使不可用的节点类型也能无损呈现。</para>
    /// </summary>
    /// <value>
    /// Persisted output ports of this node; empty when the node declares none.
    /// <para>该节点已持久化的输出端口；没有声明时为空列表。</para>
    /// </value>
    public List<NodePortDto> OutputPorts { get; set; } = [];
}

/// <summary>
/// Stable persisted node-port schema.
/// <para>稳定持久化的节点端口架构。</para>
/// </summary>
public sealed class NodePortDto
{
    /// <summary>
    /// Stable non-localized port identifier.
    /// <para>稳定的非本地化端口标识符。</para>
    /// </summary>
    /// <value>
    /// Identifier connections and composite bindings reference; unique among the owning node's ports of the same direction.
    /// <para>连接与复合端口绑定所引用的标识符；在同一节点的同一方向端口中唯一。</para>
    /// </value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display title retained for compatibility and missing-node presentation.
    /// <para>为兼容性与缺失节点展示而保留的显示标题。</para>
    /// </summary>
    /// <value>
    /// Human-readable port caption used when a connection carries no stable port id.
    /// <para>当连接未携带稳定端口 ID 时使用的可读端口标题。</para>
    /// </value>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Serialized node port type name.
    /// <para>序列化后的节点端口类型名称。</para>
    /// </summary>
    /// <value>
    /// One of the known port type names such as <c>Any</c>, <c>Flow</c> or <c>Path</c>; defaults to <c>Any</c>.
    /// <para>已知端口类型名之一，例如 <c>Any</c>、<c>Flow</c>、<c>Path</c>；默认值为 <c>Any</c>。</para>
    /// </value>
    public string PortType { get; set; } = "Any";

    /// <summary>
    /// Maximum allowed connections; zero means unlimited.
    /// <para>允许的最大连接数；零表示不限。</para>
    /// </summary>
    /// <value>
    /// Per-port connection ceiling enforced by the editor; negative values are rejected by plugin validation.
    /// <para>由编辑器执行的单端口连接上限；负值会被插件校验拒绝。</para>
    /// </value>
    public int MaxConnections { get; set; }
}

/// <summary>
/// <para>连接数据传输对象。</para>
/// Connection data transfer object.
/// </summary>
public sealed class ConnectionDto
{
    /// <summary>
    /// Stable source port id.
    /// <para>稳定的源端口 ID。</para>
    /// </summary>
    /// <value>
    /// Output port identifier on the source node; <see langword="null"/> when only the connector title was persisted.
    /// <para>源节点上的输出端口标识符；仅持久化了连接器标题时为 <see langword="null"/>。</para>
    /// </value>
    public string? SourcePortId { get; set; }

    /// <summary>
    /// Stable target port id.
    /// <para>稳定的目标端口 ID。</para>
    /// </summary>
    /// <value>
    /// Input port identifier on the target node; <see langword="null"/> when only the connector title was persisted.
    /// <para>目标节点上的输入端口标识符；仅持久化了连接器标题时为 <see langword="null"/>。</para>
    /// </value>
    public string? TargetPortId { get; set; }

    /// <summary>
    /// <para>源连接器标题。</para>
    /// Source connector title.
    /// </summary>
    /// <value>
    /// Display caption of the source connector, used to match the endpoint when the stable port id is absent.
    /// <para>源连接器的显示标题；在缺少稳定端口 ID 时用于匹配该端点。</para>
    /// </value>
    public string SourceConnectorTitle { get; set; } = string.Empty;

    /// <summary>
    /// <para>目标连接器标题。</para>
    /// Target connector title.
    /// </summary>
    /// <value>
    /// Display caption of the target connector, used to match the endpoint when the stable port id is absent.
    /// <para>目标连接器的显示标题；在缺少稳定端口 ID 时用于匹配该端点。</para>
    /// </value>
    public string TargetConnectorTitle { get; set; } = string.Empty;

    /// <summary>
    /// <para>源节点实例 ID（可选；缺失时按连接器标题匹配重建）。</para>
    /// Source node instance ID (optional; when omitted, rebuild falls back to connector-title matching).
    /// </summary>
    /// <value>
    /// <see cref="NodeDto.InstanceId"/> of the node owning the source port; <see langword="null"/> when the file carries titles only.
    /// <para>拥有源端口的节点的 <see cref="NodeDto.InstanceId"/>；文件仅携带标题时为 <see langword="null"/>。</para>
    /// </value>
    public string? SourceInstanceId { get; set; }

    /// <summary>
    /// <para>目标节点实例 ID（可选；缺失时按连接器标题匹配重建）。</para>
    /// Target node instance ID (optional; when omitted, rebuild falls back to connector-title matching).
    /// </summary>
    /// <value>
    /// <see cref="NodeDto.InstanceId"/> of the node owning the target port; <see langword="null"/> when the file carries titles only.
    /// <para>拥有目标端口的节点的 <see cref="NodeDto.InstanceId"/>；文件仅携带标题时为 <see langword="null"/>。</para>
    /// </value>
    public string? TargetInstanceId { get; set; }
}