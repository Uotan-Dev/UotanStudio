using System.Diagnostics.CodeAnalysis;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Editable data for a host without an editor. Access it only when the owning session is idle.
/// <para>面向无编辑器宿主的可编辑数据。仅应在所属会话空闲时访问。</para>
/// </summary>
public sealed class ProjectDataWorkspace : IProjectWorkspace
{
    private string? _documentRoot;
    /// <summary>
    /// Open node project documents keyed by node project name, including unknown node type keys.
    /// <para>按节点项目名索引的打开文档集合，包含未知节点类型键。</para>
    /// </summary>
    /// <value>Read/write dictionary; empty by default. A solution with no registered node project leaves it
    /// empty. Keys enumerate in insertion order even after a removal, which the active-document fallback and
    /// <see cref="OpenWorkflowNames"/> rely on.
    /// <para>可读写字典；默认为空。未登记节点项目的解决方案保持为空。即使发生删除，键仍按插入顺序枚举，
    /// 活动文档回退与 <see cref="OpenWorkflowNames"/> 依赖该顺序。</para></value>
    public OrderedDictionary<string, NodeProjectSnapshot> Workflows { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Current image entries, relative to the solution data directory.
    /// <para>当前镜像条目，相对于解决方案数据目录。</para>
    /// </summary>
    /// <value>Read/write list; empty by default. <para>可读写列表；默认为空。</para></value>
    public List<ImageEntryDto> Images { get; set; } = [];
    /// <summary>
    /// Current packing settings.
    /// <para>当前打包设置。</para>
    /// </summary>
    /// <value>Read/write; defaults to a new <see cref="ProjectSettingsDto"/>.
    /// <para>可读写；默认为新的 <see cref="ProjectSettingsDto"/>。</para></value>
    public ProjectSettingsDto Settings { get; set; } = new();
    /// <summary>
    /// Document descriptors, treated as opaque data by this host.
    /// <para>文档描述符，本宿主将其视为不透明数据。</para>
    /// </summary>
    /// <value>Read/write list; empty by default and never interpreted by this workspace.
    /// <para>可读写列表；默认为空，本工作区不解析其内容。</para></value>
    public List<OpenDocumentDto> Documents { get; set; } = [];
    /// <summary>
    /// Whether the host is performing an operation that prevents project changes.
    /// <para>宿主是否正在执行会阻止项目变更的操作。</para>
    /// </summary>
    /// <value>Read/write flag maintained by the host; <c>false</c> by default.
    /// <para>由宿主维护的可读写标志；默认为 <c>false</c>。</para></value>
    public bool IsImageOperationRunning { get; set; }
    /// <summary>
    /// Reports whether a node editor surface is available; this data-only implementation reports one.
    /// <para>指示是否提供节点编辑器界面；本纯数据实现始终报告提供。</para>
    /// </summary>
    /// <value><see langword="true"/> at all times. <para>始终为 <see langword="true"/>。</para></value>
    public bool HasNodeEditor => true;
    /// <summary>
    /// Reports whether image entries missing from disk are preserved instead of dropped.
    /// <para>指示磁盘上缺失的镜像条目是否保留而非丢弃。</para>
    /// </summary>
    /// <value><see langword="true"/> at all times. <para>始终为 <see langword="true"/>。</para></value>
    public bool PreserveMissingImages => true;
    /// <summary>
    /// Switches the project root. With a non-null root, stored documents are rebased into it when
    /// <paramref name="preserveDocuments"/> is set and cleared otherwise; a null root clears documents,
    /// resets settings to a fresh default and forgets the document root.
    /// <para>切换项目根目录。根目录非空时，<paramref name="preserveDocuments"/> 为真则文档相对路径重定位到新根，
    /// 否则清空文档；根目录为空时清除文档、将设置重置为新默认值并遗忘文档根。</para>
    /// </summary>
    /// <param name="rootDirectory">
    /// New project root, or <see langword="null"/> to reset documents and settings.
    /// <para>新项目根目录；为 <see langword="null"/> 时清除文档并将设置重置为默认。</para>
    /// </param>
    /// <param name="preserveDocuments">
    /// When <see langword="true"/> and a root is given, existing documents are rebased into the new root.
    /// <para>为 <see langword="true"/> 且提供了根目录时，现有文档重定位到新根。</para>
    /// </param>
    public void ChangeProjectRoot(string? rootDirectory, bool preserveDocuments)
    {
        if (rootDirectory is not null) ChangeRoot(rootDirectory, preserveDocuments);
        else { Documents.Clear(); Settings = new(); _documentRoot = null; }
    }
    /// <summary>
    /// Discards every open node project document and forgets the active one.
    /// <para>丢弃全部打开的节点项目文档并遗忘活动项。</para>
    /// </summary>
    public void ClearNodes()
    {
        Workflows.Clear();
        ActiveWorkflowName = null;
    }
    /// <summary>
    /// Removes every image entry from the current list.
    /// <para>移除当前列表中的全部镜像条目。</para>
    /// </summary>
    public void ClearImages() => Images.Clear();
    /// <summary>
    /// Replaces the packing settings with the given instance, which is kept by reference.
    /// <para>用给定实例替换打包设置；实例按引用保存。</para>
    /// </summary>
    /// <param name="settings">Settings instance to adopt. <para>要采用的设置实例。</para></param>
    public void ApplySettings(ProjectSettingsDto settings) => Settings = settings;
    /// <summary>
    /// Appends the given entries to the image list without clearing existing content.
    /// <para>将给定条目追加到镜像列表，不清空既有内容。</para>
    /// </summary>
    /// <param name="images">Entries to append; they are not copied. <para>要追加的条目；不进行拷贝。</para></param>
    public void RestoreImages(IEnumerable<ImageEntryDto> images) => Images.AddRange(images);
    /// <summary>
    /// Adopts the document list by reference and records its root directory; performs no I/O.
    /// <para>按引用接收文档列表并记录其根目录；不执行任何 I/O。</para>
    /// </summary>
    /// <param name="documents">Document descriptors to adopt as the current set. <para>作为当前集合采用的文档描述符。</para></param>
    /// <param name="rootDirectory">Root the descriptors' relative paths are resolved against. <para>解析描述符相对路径所依据的根目录。</para></param>
    /// <returns>A completed task; the update is synchronous. <para>已完成的任务；更新为同步执行。</para></returns>
    public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory)
    {
        Documents = documents;
        _documentRoot = rootDirectory;
        return Task.CompletedTask;
    }
    /// <summary>
    /// The open node project names, in insertion order.
    /// <para>打开的节点项目名称，按插入顺序。</para>
    /// </summary>
    /// <value>Keys of <see cref="Workflows"/>. <para>即 <see cref="Workflows"/> 的键。</para></value>
    public IReadOnlyList<string> OpenWorkflowNames => [.. Workflows.Keys];

    /// <summary>
    /// The node project name this host presents as active; <see langword="null"/> when nothing is open.
    /// <para>本宿主呈现为活动的节点项目名；没有打开项时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>Read/write for data-only hosts; assigning a name that is not open keeps it reported as
    /// active until the next <see cref="ClearNodes"/>. <para>纯数据宿主可读写；赋值为未打开的名称会一直保持
    /// 该名称，直到下一次 <see cref="ClearNodes"/>。</para></value>
    public string? ActiveWorkflowName { get; set; }

    /// <summary>
    /// Presents an open node project as active; an unknown name is ignored.
    /// <para>将一个已打开的节点项目设为活动；未知名称被忽略。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    public void ActivateWorkflow(string name)
    {
        if (Workflows.ContainsKey(name)) ActiveWorkflowName = name;
    }

    /// <summary>
    /// Discards one node project document without deleting its file; the next open document becomes active
    /// when the discarded one was active.
    /// <para>丢弃单个节点项目文档且不删除其文件；被丢弃者为活动项时，下一个打开的文档成为活动项。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    public void CloseWorkflow(string name)
    {
        var wasActive = string.Equals(ActiveWorkflowName, name, StringComparison.OrdinalIgnoreCase);
        Workflows.Remove(name);
        if (wasActive) ActiveWorkflowName = Workflows.Keys.FirstOrDefault();
    }

    /// <summary>
    /// Validates the snapshot through <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/> and stores it
    /// under the given node project name; performs no I/O.
    /// <para>先经 <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/> 校验快照，再按给定节点项目名存储；
    /// 不执行任何 I/O。</para>
    /// </summary>
    /// <param name="name">Node project name identifying the document. <para>标识文档的节点项目名称。</para></param>
    /// <param name="snapshot">Snapshot to validate and store. <para>待校验并存储的快照。</para></param>
    /// <returns>A completed task when the snapshot is accepted. <para>快照通过校验时已完成的任务。</para></returns>
    /// <exception cref="InvalidDataException">
    /// The snapshot uses an unsupported format version or describes an invalid graph.
    /// <para>快照使用了不受支持的格式版本或描述了无效的图。</para>
    /// </exception>
    public Task RestoreWorkflowAsync(string name, NodeProjectSnapshot snapshot)
    {
        snapshot.EnsureSupportedVersion();
        Workflows[name] = snapshot;
        ActiveWorkflowName ??= name;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Captures the named node project document as a new snapshot that keeps the stored format version and
    /// shallow-copies the node and connection lists.
    /// <para>按名称捕获节点项目文档为新快照：保留已存储的格式版本，并对节点与连接列表进行浅拷贝。</para>
    /// </summary>
    /// <param name="name">Node project name identifying the document. <para>标识文档的节点项目名称。</para></param>
    /// <param name="snapshot">The captured snapshot when the name is open; a fresh empty snapshot otherwise.
    /// <para>名称处于打开状态时为捕获到的快照；否则为全新的空快照。</para></param>
    /// <returns><see langword="true"/> when a document of that name exists.
    /// <para>存在该名称的文档时返回 <see langword="true"/>。</para></returns>
    public bool TryCaptureWorkflow(string name, [NotNullWhen(true)] out NodeProjectSnapshot? snapshot)
    {
        if (!Workflows.TryGetValue(name, out var workflow))
        {
            snapshot = null;
            return false;
        }
        snapshot = new NodeProjectSnapshot
        {
            FormatVersion = workflow.FormatVersion,
            ProjectName = name,
            Nodes = [.. workflow.Nodes],
            Connections = [.. workflow.Connections]
        };
        return true;
    }
    /// <summary>
    /// Captures the image list as an independent copy.
    /// <para>将镜像列表捕获为独立拷贝。</para>
    /// </summary>
    /// <returns>
    /// A new list referencing the same entries; later edits do not affect the workspace.
    /// <para>引用相同条目的新列表；后续修改不影响工作区。</para>
    /// </returns>
    public List<ImageEntryDto> CaptureImages() => [.. Images];
    /// <summary>
    /// Captures the current packing settings.
    /// <para>捕获当前打包设置。</para>
    /// </summary>
    /// <returns>
    /// The live <see cref="ProjectSettingsDto"/> instance held by the workspace, not a copy.
    /// <para>工作区持有的 <see cref="ProjectSettingsDto"/> 活动实例，而非拷贝。</para>
    /// </returns>
    public ProjectSettingsDto CaptureSettings() => Settings;
    /// <summary>
    /// Projects the document descriptors into a destination root, rebasing every stored path against
    /// the current document root; paths stay <c>/</c>-separated.
    /// <para>将文档描述符投射到目标根目录，所有存储路径以当前文档根为基准重定位；路径保持以 <c>/</c> 分隔。</para>
    /// </summary>
    /// <param name="rootDirectory">Destination root that relative paths are recomputed against. <para>用于重算相对路径的目标根目录。</para></param>
    /// <returns>
    /// A new list of copied descriptors; unset compare paths stay <see langword="null"/>, a missing
    /// document root leaves paths unchanged, and a null relative path becomes an empty string.
    /// <para>由拷贝描述符组成的新列表；未设置的比较路径保持 <see langword="null"/>，文档根缺失时路径原样保留，
    /// 空的相对路径转为空字符串。</para>
    /// </returns>
    public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) => [.. Documents.Select(document => new OpenDocumentDto
    {
        Kind = document.Kind,
        OpenMode = document.OpenMode,
        IsActive = document.IsActive,
        RelativePath = Rebase(document.RelativePath, rootDirectory) ?? string.Empty,
        CompareLeftPath = Rebase(document.CompareLeftPath, rootDirectory),
        CompareRightPath = Rebase(document.CompareRightPath, rootDirectory)
    })];

    private string? Rebase(string? path, string destinationRoot)
        => string.IsNullOrEmpty(path) || _documentRoot is null ? path
            : Path.GetRelativePath(destinationRoot, Path.GetFullPath(Path.Combine(_documentRoot, path))).Replace(Path.DirectorySeparatorChar, '/');

    internal void ChangeRoot(string root, bool preserveDocuments)
    {
        Documents = preserveDocuments ? CaptureDocuments(root) : [];
        _documentRoot = root;
    }
    /// <summary>
    /// No-op for the data-only workspace; no document is written.
    /// <para>纯数据工作区的空操作；不写入任何文档。</para>
    /// </summary>
    /// <returns>A completed task. <para>已完成的任务。</para></returns>
    public Task SaveActiveDocumentAsync() => Task.CompletedTask;
    /// <summary>
    /// No-op for the data-only workspace; no document is written.
    /// <para>纯数据工作区的空操作；不写入任何文档。</para>
    /// </summary>
    /// <returns>A completed task. <para>已完成的任务。</para></returns>
    public Task AutoSaveDocumentsAsync() => Task.CompletedTask;
    /// <summary>
    /// File format detection and import are host responsibilities; edit Images directly in data-only hosts.
    /// <para>文件格式检测与导入属于宿主职责；在纯数据宿主中请直接编辑 Images。</para>
    /// </summary>
    /// <param name="paths">Ignored; the call always throws before reading them. <para>被忽略；调用总是在读取之前抛出异常。</para></param>
    /// <returns>Never completes; the method always throws. <para>永不完成；方法始终抛出异常。</para></returns>
    /// <exception cref="NotSupportedException">
    /// This workspace performs no import.
    /// <para>本工作区不执行任何导入。</para>
    /// </exception>
    public Task ImportFilesAsync(string[] paths) => throw new NotSupportedException("Supply image descriptors or a host import adapter.");
}