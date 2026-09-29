using System.Diagnostics.CodeAnalysis;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Host boundary for project operations. Captured data never exposes controls or view models.
/// <para>项目操作的宿主边界。捕获出的数据不会暴露控件或视图模型。</para>
/// </summary>
/// <remarks>Methods run on the caller's synchronization context. Hosts own editor I/O, cancellation of
/// their own work, and the thread used to call the session. Confirmation callbacks must not recursively
/// start another project change; they may await persistence and document saves.
/// <para>方法在调用方的同步上下文上运行。宿主自行负责编辑器 I/O、自身工作的取消，以及调用会话所使用的线程。
/// 确认回调不得递归地发起另一次项目变更；它们可以等待持久化和文档保存。</para>
/// </remarks>
public interface IProjectWorkspace : IProjectDataHost, IProjectDocumentHost, IProjectImportHost;

/// <summary>
/// Project model capture and restoration, independent of editor persistence.
/// <para>与编辑器持久化解耦的项目模型捕获与还原。</para>
/// </summary>
public interface IProjectDataHost
{
    /// <summary>
    /// Whether missing image files should remain as descriptors in this host.
    /// <para>磁盘上已缺失的图片文件是否应在本宿主中保留为描述符。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> keeps descriptors whose backing files are absent;
    /// the default is <see langword="false"/>.
    /// <para>为 <see langword="true"/> 时保留后备文件已不存在的描述符；默认为 <see langword="false"/>。</para>
    /// </value>
    bool PreserveMissingImages => false;
    /// <summary>
    /// Notifies the host of a committed root change; existing document identities may be preserved.
    /// <para>通知宿主根目录变更已经提交；现有文档标识可以被保留。</para>
    /// </summary>
    /// <param name="rootDirectory">
    /// New absolute solution data root, or <see langword="null"/> when no project remains open.
    /// <para>新的绝对解决方案数据根目录；没有项目打开时为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="preserveDocuments">
    /// Whether to rebase the current documents onto the new root instead of discarding them.
    /// <para>是否把当前文档重定位到新根目录，而不是丢弃它们。</para>
    /// </param>
    void ChangeProjectRoot(string? rootDirectory, bool preserveDocuments) { }
    /// <summary>
    /// Indicates host work that must prevent changing or closing the project.
    /// <para>表示宿主正在进行的工作会阻止更改或关闭项目。</para>
    /// </summary>
    /// <value><see langword="true"/> while host work blocks project changes; otherwise <see langword="false"/>.
    /// <para>宿主工作阻塞项目变更时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    bool IsImageOperationRunning { get; }
    /// <summary>
    /// Host capability indicator; the library never requires an editor to persist data.
    /// <para>宿主能力指示；库从不要求必须有编辑器才能持久化数据。</para>
    /// </summary>
    /// <value><see langword="true"/> when the host exposes a node editor.
    /// <para>宿主提供节点编辑器时为 <see langword="true"/>。</para></value>
    bool HasNodeEditor { get; }
    /// <summary>
    /// Clears every node project document the host holds open.
    /// <para>清空宿主打开的全部节点项目文档。</para>
    /// </summary>
    void ClearNodes();
    /// <summary>
    /// Clears the host image representation.
    /// <para>清空宿主的镜像表示。</para>
    /// </summary>
    void ClearImages();
    /// <summary>
    /// Applies packing settings to the host.
    /// <para>将打包设置应用到宿主。</para>
    /// </summary>
    /// <param name="settings">Packing settings read from the project file, replacing whatever the host holds.
    /// <para>从项目文件读取的打包设置，直接替换宿主当前持有的设置。</para></param>
    void ApplySettings(ProjectSettingsDto settings);
    /// <summary>
    /// Restores image descriptors supplied by the project file.
    /// <para>还原项目文件提供的镜像描述符。</para>
    /// </summary>
    /// <param name="images">
    /// Descriptors to add to the host, ordered as stored and relative to the solution data directory.
    /// <para>要添加到宿主的描述符，按存储顺序给出，路径相对于解决方案数据目录。</para>
    /// </param>
    void RestoreImages(IEnumerable<ImageEntryDto> images);
    /// <summary>
    /// Restores document descriptors relative to the specified solution data root.
    /// <para>按指定的解决方案数据根目录还原文档描述符。</para>
    /// </summary>
    /// <param name="documents">Document descriptors captured for the project being opened.
    /// <para>为正在打开的项目所捕获的文档描述符。</para></param>
    /// <param name="rootDirectory">Absolute data root the descriptor paths are resolved against.
    /// <para>描述符路径所依据的绝对数据根目录。</para></param>
    /// <returns>A task that completes once the host has reopened the documents or decided to ignore them.
    /// <para>宿主重新打开文档或决定忽略后完成的任务。</para></returns>
    Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory);
    /// <summary>
    /// Restores one node project snapshot into a host document keyed by <paramref name="name"/>; the host
    /// decides unknown-node handling.
    /// <para>按 <paramref name="name"/> 键把单个节点项目快照还原到宿主文档；未知节点的处理方式由宿主决定。</para>
    /// </summary>
    /// <param name="name">Node project name identifying the host document.
    /// <para>标识宿主节点项目文档的名称。</para></param>
    /// <param name="snapshot">Stored workflow snapshot, which may still carry unrecognized node type keys.
    /// <para>存储的工作流快照，其中可能仍包含无法识别的节点类型键。</para></param>
    /// <returns>A task that completes when the host has rebuilt its document state.
    /// <para>宿主完成文档状态重建后返回的任务。</para></returns>
    Task RestoreWorkflowAsync(string name, NodeProjectSnapshot snapshot);
    /// <summary>
    /// Captures the host document for one node project without exposing editor objects.
    /// <para>捕获宿主中某个节点项目的文档，且不暴露编辑器对象。</para>
    /// </summary>
    /// <param name="name">Node project name identifying the host document.
    /// <para>标识宿主节点项目文档的名称。</para></param>
    /// <param name="snapshot">When the method returns <see langword="true"/>, the detached snapshot holding
    /// node, connection and version data safe to serialize.
    /// <para>方法返回 <see langword="true"/> 时，脱离编辑器的快照，包含可安全序列化的节点、连接与版本数据。</para></param>
    /// <returns><see langword="false"/> when the host holds no document of that name.
    /// <para>宿主没有该名称的文档时返回 <see langword="false"/>。</para></returns>
    bool TryCaptureWorkflow(string name, [NotNullWhen(true)] out NodeProjectSnapshot? snapshot);
    /// <summary>
    /// The node project names the host currently holds open, in editor order.
    /// <para>宿主当前打开的节点项目名称，按编辑器顺序。</para>
    /// </summary>
    /// <value>Empty while no document is open; entries not listed here are never rewritten by a save.
    /// <para>没有打开文档时为空；未列出的节点项目在保存时不会被重写。</para></value>
    IReadOnlyList<string> OpenWorkflowNames { get; }
    /// <summary>
    /// The node project name the host presents as active; <see langword="null"/> when no document is open.
    /// <para>宿主呈现为活动的节点项目名；没有打开文档时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>Follows <see cref="ActivateWorkflow"/> and the host's own tab changes.
    /// <para>随 <see cref="ActivateWorkflow"/> 以及宿主自身的标签切换变更。</para></value>
    string? ActiveWorkflowName { get; }
    /// <summary>
    /// Presents the named host document as active; an unknown name is ignored.
    /// <para>将指定名称的宿主文档设为活动；未知名称被忽略。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    void ActivateWorkflow(string name);
    /// <summary>
    /// Discards the named host document without deleting its file.
    /// <para>丢弃指定名称的宿主文档，但不删除其文件。</para>
    /// </summary>
    /// <param name="name">Node project name. <para>节点项目名称。</para></param>
    void CloseWorkflow(string name);
    /// <summary>
    /// Captures the current image list.
    /// <para>捕获当前的镜像列表。</para>
    /// </summary>
    /// <returns>Image descriptors in host order, relative to the solution data directory.
    /// <para>按宿主顺序给出的镜像描述符，路径相对于解决方案数据目录。</para></returns>
    List<ImageEntryDto> CaptureImages();
    /// <summary>
    /// Captures current packing settings.
    /// <para>捕获当前的打包设置。</para>
    /// </summary>
    /// <returns>The settings as currently held by the host. <para>宿主当前持有的设置。</para></returns>
    ProjectSettingsDto CaptureSettings();
    /// <summary>
    /// Captures document identities relative to the requested root, including for Save As.
    /// <para>按请求的根目录捕获文档标识，另存为时同样适用。</para>
    /// </summary>
    /// <param name="rootDirectory">Absolute directory the returned paths must be relative to.
    /// <para>返回路径必须相对于其计算的绝对目录。</para></param>
    /// <returns>Rebased document descriptors ready to serialize, with empty paths preserved as empty.
    /// <para>已完成重定位、可直接序列化的文档描述符；原本为空的路径仍保持为空。</para></returns>
    List<OpenDocumentDto> CaptureDocuments(string rootDirectory);
}

/// <summary>
/// Editor save policy provided by the host.
/// <para>由宿主提供的编辑器保存策略。</para>
/// </summary>
public interface IProjectDocumentHost
{
    /// <summary>
    /// Saves the active document using the host's existing editor policy.
    /// <para>按宿主现有的编辑器策略保存当前活动文档。</para>
    /// </summary>
    /// <returns>A task that completes once the host has run its save for the active document.
    /// <para>宿主为活动文档执行其保存流程后完成的任务。</para></returns>
    Task SaveActiveDocumentAsync();
    /// <summary>
    /// Saves the supported modified documents using the host's existing auto-save policy.
    /// <para>按宿主现有的自动保存策略保存支持的已修改文档。</para>
    /// </summary>
    /// <returns>A task that completes after the host has handled every modified document it supports.
    /// <para>宿主处理完其支持的所有已修改文档后完成的任务。</para></returns>
    Task AutoSaveDocumentsAsync();
}

/// <summary>
/// Host-specific file recognition and import interaction.
/// <para>宿主特定的文件识别与导入交互。</para>
/// </summary>
public interface IProjectImportHost
{
    /// <summary>
    /// Imports files using host-specific detection and interaction.
    /// <para>使用宿主特定的检测与交互导入文件。</para>
    /// </summary>
    /// <param name="paths">Absolute paths the user selected; the host decides which formats it accepts.
    /// <para>用户选择的绝对路径；接受哪些格式由宿主决定。</para></param>
    /// <returns>A task that completes when the host has finished importing, prompting the user, or rejecting the paths.
    /// <para>宿主完成导入、向用户提问或拒绝路径后返回的任务。</para></returns>
    Task ImportFilesAsync(string[] paths);
}