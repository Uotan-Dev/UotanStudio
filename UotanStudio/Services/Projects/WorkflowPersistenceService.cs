using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.ViewModels.NodeViewModels;

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
/// <para>节点工作流持久化服务，将节点画布序列化/反序列化到 <c>.uotan</c> 工程文件。</para>
/// Node workflow persistence service, serializing/deserializing the node canvas to a <c>.uotan</c> project file.
/// </summary>
/// <remarks>
/// <para>AOT 友好：使用 <see cref="WorkflowJsonContext"/> 源生成 JSON，零反射。</para>
/// AOT-friendly: uses <see cref="WorkflowJsonContext"/> source-generated JSON, no reflection.
/// </remarks>
public static class WorkflowPersistenceService
{
    /// <summary>
    /// <para>异步保存节点工作流到指定路径。</para>
    /// Asynchronously saves the node workflow to the specified path.
    /// </summary>
    public static async Task<bool> SaveAsync(
        string path,
        IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnectionViewModel> connections,
        CancellationToken cancellationToken = default)
    {
        var log = LogService.Instance;
        if (string.IsNullOrEmpty(path))
        {
            log.Write(LogLevel.Warn, "Persistence", "Save aborted: empty path");
            return false;
        }

        try
        {
            var snapshot = new WorkflowSnapshot
            {
                FormatVersion = 1,
                Nodes = BuildNodeDtos(nodes),
                Connections = BuildConnectionDtos(nodes, connections)
            };

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, snapshot, WorkflowJsonContext.Default.WorkflowSnapshot, cancellationToken).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Persistence", $"Saved workflow: {path} ({nodes.Count} nodes, {connections.Count} connections)");
            return true;
        }
        catch (System.Exception ex)
        {
            log.Write(LogLevel.Error, "Persistence", $"Save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <para>异步从指定路径加载节点工作流，返回节点与连接 DTO 列表（由调用方重建画布）。</para>
    /// Asynchronously loads the node workflow from the specified path, returning node and connection DTO lists (the caller rebuilds the canvas).
    /// </summary>
    public static async Task<WorkflowSnapshot?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var log = LogService.Instance;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log.Write(LogLevel.Warn, "Persistence", $"Load aborted: missing file '{path}'");
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync(stream, WorkflowJsonContext.Default.WorkflowSnapshot, cancellationToken).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Persistence", $"Loaded workflow: {path} ({snapshot?.Nodes.Count ?? 0} nodes, {snapshot?.Connections.Count ?? 0} connections)");
            return snapshot;
        }
        catch (System.Exception ex)
        {
            log.Write(LogLevel.Error, "Persistence", $"Load failed: {ex.Message}");
            return null;
        }
    }

    private static List<NodeDto> BuildNodeDtos(IReadOnlyList<FlowNodeViewModel> nodes)
    {
        var result = new List<NodeDto>(nodes.Count);
        foreach (var n in nodes)
        {
            result.Add(new NodeDto
            {
                InstanceId = n.InstanceId,
                Title = n.Title,
                Category = n.Category.ToString(),
                X = n.Location.X,
                Y = n.Location.Y,
                TypeKey = n.TypeKey,
                InputInitialValues = BuildInitialValueMap(n)
            });
        }
        return result;
    }

    private static Dictionary<string, string>? BuildInitialValueMap(FlowNodeViewModel node)
    {
        Dictionary<string, string>? map = null;
        foreach (var connector in node.Input)
        {
            if (connector is not { HasPersistableInitialValue: true } || connector.InitialValue is null)
            {
                continue;
            }

            var text = System.Convert.ToString(connector.InitialValue, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            (map ??= []).Add(connector.Title, text);
        }

        return map;
    }

    /// <summary>
    /// <para>由节点与连接集合构建连接 DTO 列表，解析每个连接器所属节点以持久化源/目标实例 ID（供无头执行等场景可靠重建拓扑）。</para>
    /// Builds connection DTOs from node and connection collections, resolving each connector's owning node so that
    /// source/target instance IDs can be persisted (reliable topology rebuild for headless execution and similar).
    /// </summary>
    public static List<ConnectionDto> BuildConnectionDtos(IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections)
    {
        var result = new List<ConnectionDto>(connections.Count);
        foreach (var c in connections)
        {
            if (c.Source == null || c.Target == null) continue;
            result.Add(new ConnectionDto
            {
                SourceConnectorTitle = c.Source.Title,
                TargetConnectorTitle = c.Target.Title,
                SourceInstanceId = FindOwnerNodeId(nodes, c.Source),
                TargetInstanceId = FindOwnerNodeId(nodes, c.Target)
            });
        }
        return result;
    }

    /// <summary>
    /// <para>按引用扫描查找连接器所属节点实例 ID；未找到返回 <c>null</c>。</para>
    /// Finds the owning node instance ID of a connector by reference scan; returns <c>null</c> if not found.
    /// </summary>
    private static string? FindOwnerNodeId(IReadOnlyList<FlowNodeViewModel> nodes, NodeConnectorViewModel connector)
    {
        foreach (var n in nodes)
        {
            if (n.Input.Contains(connector) || n.Output.Contains(connector))
            {
                return n.InstanceId;
            }
        }
        return null;
    }
}

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