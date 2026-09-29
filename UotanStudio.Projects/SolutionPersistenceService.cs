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
    /// Asynchronously saves the solution snapshot to the specified path via an atomic staged write: the
    /// JSON is first written beside the destination and only moved over it once fully serialized, and an
    /// existing file with identical text is left untouched. Concurrent saves are serialized.
    /// <para>异步保存解决方案快照到指定路径，采用原子暂存写入：先在目标文件旁完整序列化 JSON，成功后
    /// 才移动覆盖目标；磁盘上文本完全一致的文件保持不动。并发写入互相串行。</para>
    /// </summary>
    /// <param name="path">Destination solution file path. <para>目标解决方案文件路径。</para></param>
    /// <param name="snapshot">Snapshot to serialize; must not be <see langword="null"/>.
    /// <para>待序列化的快照；不得为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the write before it commits. <para>在写入提交之前取消。</para></param>
    /// <returns><see langword="true"/> when the snapshot is persisted or the file already matches it;
    /// <see langword="false"/> when serialization or I/O fails, in which case the previous file content
    /// is preserved. <para>快照已持久化、或磁盘文件内容已一致时返回 <see langword="true"/>；序列化或 I/O
    /// 失败时返回 <see langword="false"/>，此时保留原有文件内容。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.
    /// <para><paramref name="snapshot"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.
    /// <para>操作被取消。</para></exception>
    public static async Task<bool> SaveSolutionAsync(string path, SolutionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.SolutionSnapshot, "solution", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the solution snapshot from the specified path, validating its format version
    /// and sub-project references after deserialization.
    /// <para>异步从指定路径加载解决方案快照，反序列化后校验其格式版本与子项目引用。</para>
    /// </summary>
    /// <param name="path">Solution file path to read. <para>待读取的解决方案文件路径。</para></param>
    /// <param name="cancellationToken">Cancels a read in progress. <para>取消进行中的读取。</para></param>
    /// <returns>The validated snapshot, or <see langword="null"/> when the file is missing, exceeds
    /// 32 MiB, is not valid JSON for this schema, or fails validation.
    /// <para>校验通过的快照；文件缺失、超过 32 MiB、不是本模式合法 JSON 或校验失败时返回
    /// <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.
    /// <para>操作被取消。</para></exception>
    public static async Task<SolutionSnapshot?> LoadSolutionAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.SolutionSnapshot, "solution", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously saves the firmware project snapshot (images + packing settings) to the specified
    /// path via an atomic staged write; an existing file with identical text is left untouched.
    /// <para>异步保存固件项目快照（镜像清单 + 打包设置）到指定路径，采用原子暂存写入；磁盘上文本完全
    /// 一致的文件保持不动。</para>
    /// </summary>
    /// <param name="path">Destination <c>.uotanfw</c> file path. <para>目标 <c>.uotanfw</c> 文件路径。</para></param>
    /// <param name="snapshot">Snapshot to serialize; must not be <see langword="null"/>.
    /// <para>待序列化的快照；不得为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the write before it commits. <para>在写入提交之前取消。</para></param>
    /// <returns><see langword="true"/> when the snapshot is persisted or the file already matches it;
    /// <see langword="false"/> when serialization or I/O fails, preserving the previous file content.
    /// <para>快照已持久化、或磁盘文件内容已一致时返回 <see langword="true"/>；序列化或 I/O 失败时返回
    /// <see langword="false"/>，并保留原有文件内容。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.
    /// <para><paramref name="snapshot"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.
    /// <para>操作被取消。</para></exception>
    public static async Task<bool> SaveFirmwareProjectAsync(string path, FirmwareProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.FirmwareProjectSnapshot, "firmware project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the firmware project snapshot from the specified path, rejecting unsupported
    /// format versions and entries with missing or blank image paths.
    /// <para>异步从指定路径加载固件项目快照，拒绝不支持的格式版本以及缺失或空白镜像路径的条目。</para>
    /// </summary>
    /// <param name="path">Source <c>.uotanfw</c> file path. <para>源 <c>.uotanfw</c> 文件路径。</para></param>
    /// <param name="cancellationToken">Cancels a read in progress. <para>取消进行中的读取。</para></param>
    /// <returns>The validated snapshot, or <see langword="null"/> when the file is missing, exceeds
    /// 32 MiB, is not valid JSON for this schema, or fails validation.
    /// <para>校验通过的快照；文件缺失、超过 32 MiB、不是本模式合法 JSON 或校验失败时返回
    /// <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.
    /// <para>操作被取消。</para></exception>
    public static async Task<FirmwareProjectSnapshot?> LoadFirmwareProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.FirmwareProjectSnapshot, "firmware project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously saves the node project snapshot (node canvas) to the specified path after
    /// <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/> has validated the graph, via an atomic
    /// staged write; an existing file with identical text is left untouched.
    /// <para>异步保存节点项目快照（节点画布）到指定路径：写入前先经
    /// <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/> 校验图结构，采用原子暂存写入；磁盘上
    /// 文本完全一致的文件保持不动。</para>
    /// </summary>
    /// <param name="path">Destination <c>.uotannode</c> file path. <para>目标 <c>.uotannode</c> 文件路径。</para></param>
    /// <param name="snapshot">Snapshot to serialize; must not be <see langword="null"/>.
    /// <para>待序列化的快照；不得为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the write before it commits. <para>在写入提交之前取消。</para></param>
    /// <returns><see langword="true"/> when the snapshot is persisted or the file already matches it;
    /// <see langword="false"/> when graph validation, serialization or I/O fails, preserving the
    /// previous file content. <para>快照已持久化、或磁盘文件内容已一致时返回 <see langword="true"/>；
    /// 图校验、序列化或 I/O 失败时返回 <see langword="false"/>，并保留原有文件内容。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.
    /// <para><paramref name="snapshot"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.
    /// <para>操作被取消。</para></exception>
    public static async Task<bool> SaveNodeProjectAsync(string path, NodeProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.NodeProjectSnapshot, "node project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the node project snapshot from the specified path, validating the graph and
    /// rejecting unsupported format versions via <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/>.
    /// <para>异步从指定路径加载节点项目快照，并通过 <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/>
    /// 校验图结构、拒绝不支持的格式版本。</para>
    /// </summary>
    /// <param name="path">Source <c>.uotannode</c> file path. <para>源 <c>.uotannode</c> 文件路径。</para></param>
    /// <param name="cancellationToken">Cancels a read in progress. <para>取消进行中的读取。</para></param>
    /// <returns>The validated snapshot, or <see langword="null"/> when the file is missing, exceeds
    /// 32 MiB, is not valid JSON for this schema, or fails validation.
    /// <para>校验通过的快照；文件缺失、超过 32 MiB、不是本模式合法 JSON 或校验失败时返回
    /// <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.
    /// <para>操作被取消。</para></exception>
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
/// list, and the node projects it references (relative to the solution file's directory).
/// <para>解决方案快照，承载 <c>.uotan</c> 解决方案文件结构：解决方案名、子项目列表及其引用的节点项目
/// （相对解决方案文件目录）。</para>
/// </summary>
public sealed class SolutionSnapshot
{
    /// <summary>
    /// Solution file format version (currently 1).
    /// <para>解决方案文件格式版本（当前为 1）。</para>
    /// </summary>
    /// <value>The version tag written to disk; loading rejects any value other than 1.
    /// <para>写入磁盘的版本标记；加载时除 1 外一律拒绝。</para></value>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Solution name (without extension).
    /// <para>解决方案名（不含扩展名）。</para>
    /// </summary>
    /// <value>The display name persisted with the solution; empty by default.
    /// <para>随解决方案持久化的显示名称；默认为空字符串。</para></value>
    public string SolutionName { get; set; } = string.Empty;

    /// <summary>
    /// Node project files this solution references, in registration order.
    /// <para>本解决方案引用的节点项目文件清单，按登记顺序排列。</para>
    /// </summary>
    /// <value>The node project references, or <see langword="null"/> when the solution references none. A
    /// freshly created solution stores no node project until one is created explicitly.
    /// <para>节点项目引用；解决方案未引用任何节点项目时为 <see langword="null"/>。新建的解决方案在显式
    /// 创建节点项目之前不引用任何节点项目。</para></value>
    public List<NodeProjectDto>? NodeProjects { get; set; }

    /// <summary>
    /// References to independently stored sub-projects.
    /// <para>独立存放的子项目引用列表。</para>
    /// </summary>
    /// <value>The sub-project references, or <see langword="null"/> when the solution stores none.
    /// <para>子项目引用；解决方案不含子项目时为 <see langword="null"/>。</para></value>
    public List<SubProjectDto>? SubProjects { get; set; }

    /// <summary>
    /// Solution-level extension state keyed by well-known names (e.g. <c>delivery</c> delivery
    /// plan, <c>panels</c> panel visibility), stored as raw JSON elements so the host application
    /// owns their schema. Optional; solutions may omit it.
    /// <para>解决方案级扩展状态，按约定键名存放（如 <c>delivery</c> 交付计划、<c>panels</c> 面板
    /// 可见性），以原始 JSON 元素承载、模式由宿主应用定义。可选；解决方案可不包含该字段。</para>
    /// </summary>
    /// <value>Raw JSON payloads keyed by well-known names, or <see langword="null"/> when absent.
    /// <para>按约定键名存放的原始 JSON 内容；不存在时为 <see langword="null"/>。</para></value>
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
    /// <value>The name shown in the UI; unique per solution case-insensitively and constrained by
    /// <see cref="ProjectValidation.ValidateName"/>. <para>界面显示名称；在同一解决方案内按大小写不敏感
    /// 唯一，并受 <see cref="ProjectValidation.ValidateName"/> 约束。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Relative path to the <c>.uotanfw</c> file (relative to the solution file's directory).
    /// <para>指向 <c>.uotanfw</c> 文件的相对路径（相对解决方案文件目录）。</para>
    /// </summary>
    /// <value>The stored project-file location; validation rejects a blank value.
    /// <para>已保存的项目文件位置；校验会拒绝空白值。</para></value>
    public string RelativePath { get; set; } = string.Empty;
}

/// <summary>
/// Node project reference DTO, carrying the display name, the relative path of its <c>.uotannode</c> file,
/// and whether the editor keeps it open and active.
/// <para>节点项目引用 DTO，承载显示名称、其 <c>.uotannode</c> 文件的相对路径，以及编辑器是否将其保持打开
/// 与活动。</para>
/// </summary>
public sealed class NodeProjectDto
{
    /// <summary>
    /// Node project display name, also the base name of its file.
    /// <para>节点项目显示名称，同时作为其文件的基本名。</para>
    /// </summary>
    /// <value>The name shown in the UI; unique per solution case-insensitively and constrained by
    /// <see cref="ProjectValidation.ValidateName"/>. <para>界面显示名称；在同一解决方案内按大小写不敏感
    /// 唯一，并受 <see cref="ProjectValidation.ValidateName"/> 约束。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Relative path to the <c>.uotannode</c> file (relative to the solution file's directory, using
    /// <c>/</c> separators).
    /// <para>指向 <c>.uotannode</c> 文件的相对路径（相对解决方案文件目录，使用 <c>/</c> 分隔）。</para>
    /// </summary>
    /// <value>The stored node project file location; validation rejects a blank value or a path escaping
    /// the solution data root. <para>已保存的节点项目文件位置；校验会拒绝空白值以及逃出解决方案数据根目录
    /// 的路径。</para></value>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether the editor restores this node project into an open canvas when the solution is opened.
    /// <para>打开解决方案时编辑器是否把该节点项目还原为打开的画布。</para>
    /// </summary>
    /// <value><see langword="true"/> for node projects the user keeps open in the editor.
    /// <para>用户在编辑器中保持打开的节点项目为 <see langword="true"/>。</para></value>
    public bool IsOpen { get; set; }

    /// <summary>
    /// Whether this node project is the one the editor presents as active.
    /// <para>该节点项目是否为编辑器当前呈现为活动的画布。</para>
    /// </summary>
    /// <value>At most one reference per solution is active, and an active reference must also be open.
    /// <para>每个解决方案至多一个引用为活动，且活动引用必须同时处于打开状态。</para></value>
    public bool IsActive { get; set; }
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
    /// <value>The version tag written to disk; loading rejects any value other than 1.
    /// <para>写入磁盘的版本标记；加载时除 1 外一律拒绝。</para></value>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Project name (without extension).
    /// <para>项目名（不含扩展名）。</para>
    /// </summary>
    /// <value>The firmware project display name; empty by default.
    /// <para>固件项目显示名称；默认为空字符串。</para></value>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Image list (all paths relative to the project root directory).
    /// <para>镜像清单（全部为相对工程根目录的路径）。</para>
    /// </summary>
    /// <value>The persisted image entries; loading rejects <see langword="null"/> entries and entries
    /// with a blank relative path. <para>已持久化的镜像条目；加载时拒绝 <see langword="null"/> 条目以及
    /// 相对路径为空的条目。</para></value>
    public List<ImageEntryDto> Images { get; set; } = [];

    /// <summary>
    /// Packing settings snapshot.
    /// <para>打包设置快照。</para>
    /// </summary>
    /// <value>The persisted packing settings, or <see langword="null"/> when the project stores none.
    /// <para>已持久化的打包设置；项目未保存设置时为 <see langword="null"/>。</para></value>
    public ProjectSettingsDto? Settings { get; set; }

    /// <summary>
    /// Open document tabs (restored on project open). Paths are relative to the project root directory.
    /// <para>已打开的文档标签（打开工程时恢复）。路径为相对工程根目录的路径。</para>
    /// </summary>
    /// <value>The tab descriptors to restore, or <see langword="null"/> when nothing was persisted.
    /// <para>待恢复的标签描述符；未持久化任何标签时为 <see langword="null"/>。</para></value>
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
    /// <value>Integer encoding of the host editor open mode; 0 lets the editor choose automatically, and
    /// only text-kind documents carry an explicit non-zero mode.
    /// <para>宿主编辑器打开模式的整数编码；0 表示由编辑器自动选择，仅文本类文档携带非 0 的显式模式。</para></value>
    public int OpenMode { get; set; }
    /// <summary>Indicates the selected document.<para>标识选中的文档。</para></summary>
    /// <value><see langword="true"/> when this tab is the active one on restore; <see langword="false"/>
    /// for all other tabs. <para>恢复时本标签为活动标签则为 <see langword="true"/>；其余标签为
    /// <see langword="false"/>。</para></value>
    public bool IsActive { get; set; }
    /// <summary>Gets the left comparison path.<para>获取左侧比对路径。</para></summary>
    /// <value>Left side of a diff view, or <see langword="null"/> when the tab opens without comparison.
    /// <para>比对视图的左侧文件；该标签未开启比对时为 <see langword="null"/>。</para></value>
    public string? CompareLeftPath { get; set; }
    /// <summary>Gets the right comparison path.<para>获取右侧比对路径。</para></summary>
    /// <value>Right side of a diff view, or <see langword="null"/> when the tab opens without comparison.
    /// <para>比对视图的右侧文件；该标签未开启比对时为 <see langword="null"/>。</para></value>
    public string? CompareRightPath { get; set; }
    /// <summary>
    /// Document kind identifier: "Text", "Firmware", "FileManager", "Dtb", "Dex", "Manifest", "Arsc", "Hex".
    /// <para>文档类型标识符。</para>
    /// </summary>
    /// <value>The kind string selecting the editor used to restore the tab; empty by default.
    /// <para>决定恢复标签所用编辑器的类型字符串；默认为空。</para></value>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// File path relative to the project root directory.
    /// <para>相对工程根目录的文件路径。</para>
    /// </summary>
    /// <value>The document location resolved against the project root on restore.
    /// <para>恢复时相对工程根目录解析的文档位置。</para></value>
    public string RelativePath { get; set; } = string.Empty;
}

/// <summary>
/// Node project snapshot, carrying the <c>.uotannode</c> project file structure: node canvas.
/// <para>节点项目快照，承载 <c>.uotannode</c> 项目文件结构：节点画布。</para>
/// </summary>
public sealed class NodeProjectSnapshot
{
    /// <summary>
    /// Node project schema version shared with <see cref="WorkflowSnapshot"/>.
    /// <para>与 <see cref="WorkflowSnapshot"/> 共用的节点工程架构版本。</para>
    /// </summary>
    /// <value>The format version new snapshots are written with and loading accepts.
    /// <para>新快照写入、且加载时所接受的格式版本。</para></value>
    public const int CurrentFormatVersion = WorkflowSnapshot.CurrentFormatVersion;
    /// <summary>
    /// Project file format version, defaulted to <see cref="CurrentFormatVersion"/>.
    /// <para>项目文件格式版本，默认为 <see cref="CurrentFormatVersion"/>。</para>
    /// </summary>
    /// <value>The version tag persisted with the canvas; an unsupported value fails validation on both
    /// save and load. <para>随画布持久化的版本标记；不支持的取值会在保存与加载校验时失败。</para></value>
    [JsonRequired]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>
    /// Validates the complete graph before changing a project context or publishing it.
    /// <para>在切换工程上下文或发布工程之前，校验完整的图结构。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">A node or connection collection is <see langword="null"/>,
    /// contains a <see langword="null"/> entry, or carries a format version the workflow schema does not
    /// support. <para>节点或连接集合为 <see langword="null"/>、包含 <see langword="null"/> 条目，或携带
    /// 工作流架构不支持的格式版本。</para></exception>
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
    /// <value>The node project display name; empty by default.
    /// <para>节点项目显示名称；默认为空字符串。</para></value>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Node DTO list.
    /// <para>节点 DTO 列表。</para>
    /// </summary>
    /// <value>The persisted canvas nodes; empty by default, and validation rejects <see langword="null"/>
    /// collections or <see langword="null"/> entries. <para>已持久化的画布节点；默认为空，校验会拒绝
    /// <see langword="null"/> 集合与 <see langword="null"/> 条目。</para></value>
    public List<NodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// Connection DTO list.
    /// <para>连接 DTO 列表。</para>
    /// </summary>
    /// <value>The persisted edges between nodes; empty by default, and validation rejects
    /// <see langword="null"/> collections or <see langword="null"/> entries.
    /// <para>已持久化的节点间连线；默认为空，校验会拒绝 <see langword="null"/> 集合与
    /// <see langword="null"/> 条目。</para></value>
    public List<ConnectionDto> Connections { get; set; } = [];
}