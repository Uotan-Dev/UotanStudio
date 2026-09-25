namespace UotanStudio.Services.Projects;

/// <summary>
/// Editable data for a host without an editor. Access it only when the owning session is idle.
/// <para>面向无编辑器宿主的可编辑数据。仅应在所属会话空闲时访问。</para>
/// </summary>
public sealed class ProjectDataWorkspace : IProjectWorkspace
{
    private string? _documentRoot;
    /// <summary>
    /// Current workflow snapshot, including unknown node type keys.
    /// <para>当前工作流快照，包含未知节点类型键。</para>
    /// </summary>
    /// <value>Read/write; defaults to an empty <see cref="NodeProjectSnapshot"/>.
    /// <para>可读写；默认为空 <see cref="NodeProjectSnapshot"/>。</para></value>
    public NodeProjectSnapshot Workflow { get; set; } = new();
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
    /// Replaces the workflow with a fresh empty snapshot.
    /// <para>将工作流替换为全新的空快照。</para>
    /// </summary>
    public void ClearNodes() => Workflow = new();
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
    /// Validates the snapshot through <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/> and adopts
    /// it by reference; performs no I/O.
    /// <para>先经 <see cref="NodeProjectSnapshot.EnsureSupportedVersion"/> 校验快照，再按引用接收；不执行任何 I/O。</para>
    /// </summary>
    /// <param name="snapshot">Snapshot to validate and store. <para>待校验并存储的快照。</para></param>
    /// <returns>A completed task when the snapshot is accepted. <para>快照通过校验时已完成的任务。</para></returns>
    /// <exception cref="InvalidDataException">
    /// The snapshot uses an unsupported format version or describes an invalid graph.
    /// <para>快照使用了不受支持的格式版本或描述了无效的图。</para>
    /// </exception>
    public Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot)
    {
        snapshot.EnsureSupportedVersion();
        Workflow = snapshot;
        return Task.CompletedTask;
    }
    /// <summary>
    /// Captures the current workflow as a new snapshot that keeps the stored format version and
    /// shallow-copies the node and connection lists.
    /// <para>将当前工作流捕获为新快照：保留已存储的格式版本，并对节点与连接列表进行浅拷贝。</para>
    /// </summary>
    /// <param name="projectName">Project name written into the returned snapshot. <para>写入返回快照的项目名。</para></param>
    /// <returns>
    /// A new snapshot whose lists are copies; the entries inside them are shared with the current workflow.
    /// <para>新快照；其列表为拷贝，列表内条目与当前工作流共享。</para>
    /// </returns>
    public NodeProjectSnapshot CaptureWorkflow(string projectName) => new()
    {
        FormatVersion = Workflow.FormatVersion,
        ProjectName = projectName,
        Nodes = Workflow.Nodes.ToList(),
        Connections = Workflow.Connections.ToList()
    };
    /// <summary>
    /// Captures the image list as an independent copy.
    /// <para>将镜像列表捕获为独立拷贝。</para>
    /// </summary>
    /// <returns>
    /// A new list referencing the same entries; later edits do not affect the workspace.
    /// <para>引用相同条目的新列表；后续修改不影响工作区。</para>
    /// </returns>
    public List<ImageEntryDto> CaptureImages() => Images.ToList();
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
    public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) => Documents.Select(document => new OpenDocumentDto
    {
        Kind = document.Kind,
        OpenMode = document.OpenMode,
        IsActive = document.IsActive,
        RelativePath = Rebase(document.RelativePath, rootDirectory) ?? string.Empty,
        CompareLeftPath = Rebase(document.CompareLeftPath, rootDirectory),
        CompareRightPath = Rebase(document.CompareRightPath, rootDirectory)
    }).ToList();

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