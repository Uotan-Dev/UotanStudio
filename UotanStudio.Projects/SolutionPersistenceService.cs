using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Solution/project JSON serialization context (AOT source-generated).
/// <para>解决方案/项目 JSON 序列化上下文（AOT 源生成）。</para>
/// </summary>
[JsonSerializable(typeof(SolutionSnapshot))]
[JsonSerializable(typeof(FirmwareProjectSnapshot))]
[JsonSerializable(typeof(NodeProjectSnapshot))]
[JsonSerializable(typeof(List<ImageEntryDto>))]
[JsonSerializable(typeof(ImageEntryDto))]
[JsonSerializable(typeof(ProjectSettingsDto))]
[JsonSerializable(typeof(List<NodeDto>))]
[JsonSerializable(typeof(List<ConnectionDto>))]
[JsonSerializable(typeof(SubProjectDto))]
[JsonSerializable(typeof(List<SubProjectDto>))]
[JsonSerializable(typeof(OpenDocumentDto))]
[JsonSerializable(typeof(List<OpenDocumentDto>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SolutionJsonContext : JsonSerializerContext;

/// <summary>
/// Solution-level persistence service, serializing/deserializing the VS-like project layout: a
/// <c>.uotan</c> solution file referencing a firmware project (<c>.uotanfw</c>) and a node project
/// (<c>.uotannode</c>). AOT-friendly: uses the <see cref="SolutionJsonContext"/> source-generated JSON,
/// no reflection.
/// <para>解决方案级持久化服务，序列化/反序列化 VS 式工程布局：<c>.uotan</c> 解决方案文件引用固件项目
/// （<c>.uotanfw</c>）与节点项目（<c>.uotannode</c>）。AOT 友好：使用 <see cref="SolutionJsonContext"/>
/// 源生成 JSON，零反射。</para>
/// </summary>
public static class SolutionPersistenceService
{
    /// <summary>
    /// Asynchronously saves the solution snapshot to the specified path.
    /// <para>异步保存解决方案快照到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveSolutionAsync(string path, SolutionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.SolutionSnapshot, "solution", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the solution snapshot from the specified path; returns <c>null</c> on failure.
    /// <para>异步从指定路径加载解决方案快照；失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<SolutionSnapshot?> LoadSolutionAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.SolutionSnapshot, "solution", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously saves the firmware project snapshot (images + packing settings) to the specified path.
    /// <para>异步保存固件项目快照（镜像清单 + 打包设置）到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveFirmwareProjectAsync(string path, FirmwareProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.FirmwareProjectSnapshot, "firmware project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the firmware project snapshot from the specified path; returns <c>null</c> on failure.
    /// <para>异步从指定路径加载固件项目快照；失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<FirmwareProjectSnapshot?> LoadFirmwareProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.FirmwareProjectSnapshot, "firmware project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously saves the node project snapshot (node canvas) to the specified path.
    /// <para>异步保存节点项目快照（节点画布）到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveNodeProjectAsync(string path, NodeProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.NodeProjectSnapshot, "node project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the node project snapshot from the specified path; returns <c>null</c> on failure.
    /// <para>异步从指定路径加载节点项目快照；失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<NodeProjectSnapshot?> LoadNodeProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.NodeProjectSnapshot, "node project", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SaveAsync<T>(string path, T snapshot, JsonTypeInfo<T> typeInfo, string label, CancellationToken cancellationToken)
    {

        try
        {
            if (snapshot is NodeProjectSnapshot node)
            {
                node.EnsureSupportedVersion();
            }
            if (await AtomicJsonFile.WriteIfChangedAsync(path, snapshot, typeInfo, cancellationToken).ConfigureAwait(false))
                ProjectDiagnostics.Write(ProjectLogLevel.Info, "Persistence", $"Saved {label}: {path}");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", $"Save {label} failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<T?> LoadAsync<T>(string path, JsonTypeInfo<T> typeInfo, string label, CancellationToken cancellationToken)
    {

        if (!File.Exists(path))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Persistence", $"Load {label} aborted: missing file '{path}'");
            return default;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Project state exceeds 32 MiB.");
            var result = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case SolutionSnapshot solution: ProjectValidation.ValidateSolution(solution); break;
                case FirmwareProjectSnapshot firmware when firmware.FormatVersion != 1 || firmware.Images is null
                    || firmware.Images.Any(image => image is null || string.IsNullOrWhiteSpace(image.RelativePath)):
                    throw new InvalidDataException("Invalid or unsupported firmware project snapshot.");
                case NodeProjectSnapshot node: node.EnsureSupportedVersion(); break;
            }
            ProjectDiagnostics.Write(ProjectLogLevel.Info, "Persistence", $"Loaded {label}: {path}");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", $"Load {label} failed: {ex.Message}");
            return default;
        }
    }
}

/// <summary>
/// Solution snapshot, carrying the <c>.uotan</c> solution file structure: solution name, sub-project
/// list, and the node project file it references (relative to the solution file's directory).
/// <para>解决方案快照，承载 <c>.uotan</c> 解决方案文件结构：解决方案名、子项目列表及引用的节点项目文件
/// （相对解决方案文件目录）。</para>
/// </summary>
public sealed class SolutionSnapshot
{
    /// <summary>
    /// Solution file format version (currently 2).
    /// <para>解决方案文件格式版本（当前为 2）。</para>
    /// </summary>
    public int FormatVersion { get; set; } = 2;

    /// <summary>
    /// Solution name (without extension).
    /// <para>解决方案名（不含扩展名）。</para>
    /// </summary>
    public string SolutionName { get; set; } = string.Empty;

    /// <summary>
    /// Node project file path relative to the solution file's directory.
    /// <para>节点项目文件路径（相对解决方案文件目录）。</para>
    /// </summary>
    public string NodeProjectPath { get; set; } = string.Empty;

    /// <summary>References to independently stored sub-projects.</summary>
    public List<SubProjectDto>? SubProjects { get; set; }

    /// <summary>
    /// Solution-level extension state keyed by well-known names (e.g. <c>delivery</c> delivery
    /// plan, <c>panels</c> panel visibility), stored as raw JSON elements so the host application
    /// owns their schema. Optional; solutions may omit it.
    /// <para>解决方案级扩展状态，按约定键名存放（如 <c>delivery</c> 交付计划、<c>panels</c> 面板
    /// 可见性），以原始 JSON 元素承载、模式由宿主应用定义。可选；解决方案可不包含该字段。</para>
    /// </summary>
    public Dictionary<string, JsonElement>? State { get; set; }
}

/// <summary>
/// Sub-project reference DTO, carrying the sub-project's display name and the relative path to its
/// <c>.uotanfw</c> file.
/// <para>子项目引用 DTO，承载子项目显示名称及其 <c>.uotanfw</c> 文件的相对路径。</para>
/// </summary>
public sealed class SubProjectDto
{
    /// <summary>
    /// Sub-project display name (e.g. device codename).
    /// <para>子项目显示名称（如设备代号）。</para>
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Relative path to the <c>.uotanfw</c> file (relative to the solution file's directory).
    /// <para>指向 <c>.uotanfw</c> 文件的相对路径（相对解决方案文件目录）。</para>
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
}

/// <summary>
/// Firmware project snapshot, carrying the <c>.uotanfw</c> project file structure: image list and packing settings.
/// <para>固件项目快照，承载 <c>.uotanfw</c> 项目文件结构：镜像清单与打包设置。</para>
/// </summary>
public sealed class FirmwareProjectSnapshot
{
    /// <summary>
    /// Project file format version (currently 1).
    /// <para>项目文件格式版本（当前为 1）。</para>
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Project name (without extension).
    /// <para>项目名（不含扩展名）。</para>
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Image list (all paths relative to the project root directory).
    /// <para>镜像清单（全部为相对工程根目录的路径）。</para>
    /// </summary>
    public List<ImageEntryDto> Images { get; set; } = [];

    /// <summary>
    /// Packing settings snapshot.
    /// <para>打包设置快照。</para>
    /// </summary>
    public ProjectSettingsDto? Settings { get; set; }

    /// <summary>
    /// Open document tabs (restored on project open). Paths are relative to the project root directory.
    /// <para>已打开的文档标签（打开工程时恢复）。路径为相对工程根目录的路径。</para>
    /// </summary>
    public List<OpenDocumentDto>? OpenDocuments { get; set; }
}

/// <summary>
/// Open document descriptor, capturing the document type and its file path so the tab can be
/// restored on project reopen.
/// <para>已打开文档描述符，记录文档类型与文件路径以便重新打开工程时恢复标签。</para>
/// </summary>
public sealed class OpenDocumentDto
{
    /// <summary>Gets the explicit editor mode.<para>获取明确指定的编辑器模式。</para></summary>
    public int OpenMode { get; set; }
    /// <summary>Indicates the selected document.<para>标识选中的文档。</para></summary>
    public bool IsActive { get; set; }
    /// <summary>Gets the left comparison path.<para>获取左侧比对路径。</para></summary>
    public string? CompareLeftPath { get; set; }
    /// <summary>Gets the right comparison path.<para>获取右侧比对路径。</para></summary>
    public string? CompareRightPath { get; set; }
    /// <summary>
    /// Document kind identifier: "Text", "Firmware", "FileManager", "Dtb", "Dex", "Manifest", "Arsc", "Hex".
    /// <para>文档类型标识符。</para>
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// File path relative to the project root directory.
    /// <para>相对工程根目录的文件路径。</para>
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
}

/// <summary>
/// Node project snapshot, carrying the <c>.uotannode</c> project file structure: node canvas.
/// <para>节点项目快照，承载 <c>.uotannode</c> 项目文件结构：节点画布。</para>
/// </summary>
public sealed class NodeProjectSnapshot
{
    /// <summary>Node project schema using the redesigned workflow contract.</summary>
    public const int CurrentFormatVersion = WorkflowSnapshot.CurrentFormatVersion;
    /// <summary>
    /// Project file format version (currently 1).
    /// <para>项目文件格式版本（当前为 1）。</para>
    /// </summary>
    [JsonRequired]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Validates the complete graph before changing a project context or publishing it.</summary>
    public void EnsureSupportedVersion()
    {
        if (Nodes is null || Connections is null || Nodes.Any(node => node is null)
            || Connections.Any(edge => edge is null))
        {
            throw new InvalidDataException("Invalid node project graph.");
        }
        new WorkflowSnapshot { FormatVersion = FormatVersion, Nodes = Nodes, Connections = Connections }.EnsureSupportedVersion();
    }

    /// <summary>
    /// Project name (without extension).
    /// <para>项目名（不含扩展名）。</para>
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Node DTO list.
    /// <para>节点 DTO 列表。</para>
    /// </summary>
    public List<NodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// Connection DTO list.
    /// <para>连接 DTO 列表。</para>
    /// </summary>
    public List<ConnectionDto> Connections { get; set; } = [];
}