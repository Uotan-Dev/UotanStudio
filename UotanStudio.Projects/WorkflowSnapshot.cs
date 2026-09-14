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
    /// <para>工程文件格式版本。</para>
    /// Project file format version.
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// <para>节点 DTO 列表。</para>
    /// List of node DTOs.
    /// </summary>
    public List<NodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// <para>连接 DTO 列表。</para>
    /// List of connection DTOs.
    /// </summary>
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
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// <para>节点标题。</para>
    /// Node title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// <para>节点类别字符串。</para>
    /// Node category string.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// <para>画布 X 坐标。</para>
    /// Canvas X coordinate.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// <para>画布 Y 坐标。</para>
    /// Canvas Y coordinate.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// <para>稳定节点类型键，对应 NodeTypeRegistry 注册键</para>
    /// Stable node type key matching a NodeTypeRegistry key.
    /// </summary>
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>
    /// <para>输入端口用户编辑初始值映射（端口标题 → 字符串值）。仅持久化用户显式填写的值。</para>
    /// User-edited initial values of input ports (port title → string value). Only explicitly set values are persisted.
    /// </summary>
    public Dictionary<string, string>? InputInitialValues { get; set; }
}

/// <summary>
/// <para>连接数据传输对象。</para>
/// Connection data transfer object.
/// </summary>
public sealed class ConnectionDto
{
    /// <summary>
    /// <para>源连接器标题。</para>
    /// Source connector title.
    /// </summary>
    public string SourceConnectorTitle { get; set; } = string.Empty;

    /// <summary>
    /// <para>目标连接器标题。</para>
    /// Target connector title.
    /// </summary>
    public string TargetConnectorTitle { get; set; } = string.Empty;

    /// <summary>
    /// <para>源节点实例 ID（可选；旧版工程文件可能缺失，缺失时按连接器标题匹配重建）。</para>
    /// Source node instance ID (optional; legacy project files may omit it, in which case rebuild falls
    /// back to connector-title matching).
    /// </summary>
    public string? SourceInstanceId { get; set; }

    /// <summary>
    /// <para>目标节点实例 ID（可选；旧版工程文件可能缺失，缺失时按连接器标题匹配重建）。</para>
    /// Target node instance ID (optional; legacy project files may omit it, in which case rebuild falls
    /// back to connector-title matching).
    /// </summary>
    public string? TargetInstanceId { get; set; }
}