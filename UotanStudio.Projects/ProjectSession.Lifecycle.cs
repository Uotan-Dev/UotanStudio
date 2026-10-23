namespace UotanStudio.Projects;

public sealed partial class ProjectSession
{
    /// <summary>
    /// Context belonging to this session. Hosts should use session operations for mutations.
    /// <para>属于本会话的上下文；宿主应通过会话操作进行变更。</para>
    /// </summary>
    /// <value>
    /// The context instance supplied to the constructor or created by it; its identity is stable for the
    /// session lifetime. <para>构造时传入或由构造创建的上下文实例；其身份在会话期内保持稳定。</para>
    /// </value>
    public ProjectContext Context { get; }
    /// <summary>
    /// History belonging to this session; executable actions are supplied by the host.
    /// <para>属于本会话的操作历史；可执行动作由宿主提供。</para>
    /// </summary>
    /// <value>
    /// The history instance owned by the session; opening a solution or switching sub-projects replaces
    /// its contents while the instance identity stays stable.
    /// <para>会话持有的历史实例；打开解决方案或切换子项目时其内容被替换，但实例身份保持不变。</para>
    /// </value>
    public ProjectHistory History { get; }
    /// <summary>
    /// Data adapter supplied at construction, or the default ProjectDataWorkspace.
    /// <para>构造时提供的数据适配器，缺省为默认的 ProjectDataWorkspace。</para>
    /// </summary>
    /// <value>
    /// Never <see langword="null"/>: the adapter given to the constructor, otherwise a default
    /// <see cref="ProjectDataWorkspace"/>. <para>永不为 <see langword="null"/>：构造传入的适配器，
    /// 未提供时为默认 <see cref="ProjectDataWorkspace"/>。</para>
    /// </value>
    public IProjectWorkspace Workspace => _workspace;
    /// <summary>
    /// Captures directory state without exposing context mutation methods.
    /// <para>捕获目录状态，且不暴露上下文的变更方法。</para>
    /// </summary>
    /// <value>
    /// A fresh <see cref="ProjectSessionState"/> snapshot taken at read time: solution path, root
    /// directory, active sub-project name and the sub-project list.
    /// <para>读取时刻生成的新 <see cref="ProjectSessionState"/> 快照：解决方案路径、根目录、活动子项目名
    /// 与子项目列表。</para>
    /// </value>
    public ProjectSessionState State => new(Context.CurrentSolutionPath, Context.RootDirectory,
        Context.ActiveSubProject?.Name, Array.AsReadOnly(Context.SubProjects.ToArray()));
    /// <summary>
    /// Raised synchronously after successful lifecycle operations, on the calling context.
    /// <para>在生命周期操作成功后，于调用方上下文中同步触发。</para>
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Raised after a solution is reloaded from disk in full (open, add/remove sub-project). Hosts use it
    /// to rebuild state keyed by the solution rather than by its file path, such as the delivery panel.
    /// <para>解决方案从磁盘完整重新加载后触发（打开、添加/删除子项目）。宿主据此重建与解决方案
    /// 而非文件路径绑定的状态，例如交付面板。</para>
    /// </summary>
    public event Action? SolutionReloaded;

    /// <summary>Switches active sub-project under the operation gate. Optional persistence saves the old project's data first.
    /// <para>在操作门下切换活动子项目；可选的持久化会先保存旧工程的数据。</para></summary>
    /// <param name="name">Target sub-project name, matched case-insensitively.
    /// <para>目标子项目名，按大小写不敏感匹配。</para></param>
    /// <param name="persistCurrent">When <see langword="false"/>, the previous project's files are left untouched; when <see langword="true"/>, its data is persisted without capturing editor page state.
    /// <para>为 <see langword="false"/> 时不改写旧工程文件；为 <see langword="true"/> 时持久化其数据，但不采集编辑器页面状态。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the reads performed before the switch.
    /// <para>取消门等待与切换前的读取。</para></param>
    /// <returns><see langword="true"/> when the target became active (including when it already was); <see langword="false"/> when no sub-project matches <paramref name="name"/> or the operation was gated.
    /// <para>目标已处于活动状态（含原本就是活动项）时返回 <see langword="true"/>；没有匹配 <paramref name="name"/> 的子项目或操作被门拦截时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="InvalidDataException">The target sub-project's firmware file cannot be read.
    /// <para>目标子项目的固件文件无法读取。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the state was swapped in.
    /// <para>令牌在状态替换前被取消。</para></exception>
    public async Task<bool> SwitchSubProjectAsync(string name, bool persistCurrent = true, CancellationToken cancellationToken = default)
    {
        var switched = false;
        await RunOperationAsync(async () =>
        {
            var target = Context.SubProjects.FirstOrDefault(sp => string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            if (target == Context.ActiveSubProject) { switched = true; return; }
            var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(target.FwProjectPath, cancellationToken)
                ?? throw new InvalidDataException("Cannot read the target sub-project.");
            if (persistCurrent && CurrentProjectPath is { } path) await PersistCoreAsync(path, false, cancellationToken);
            var history = new ProjectHistory();
            await history.LoadAsync(Path.Combine(target.StateDirectory, "history.json"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Context.SetActiveSubProject(name);
            History.CopyFrom(history);
            _workspace.ClearImages();
            _workspace.ApplySettings(firmware.Settings ?? new());
            _workspace.RestoreImages(firmware.Images.Where(dto => _workspace.PreserveMissingImages || Context.ResolvePath(dto.RelativePath) is { } absolute && File.Exists(absolute)));
            switched = true;
            Changed?.Invoke();
        }, changingProject: true, cancellationToken: cancellationToken);
        return switched;
    }

    /// <summary>Closes the project after the host's change confirmation. Optional persistence does not save editor contents.
    /// <para>在宿主确认变更后关闭工程；可选的持久化不会保存编辑器内容。</para></summary>
    /// <param name="persistProject">When <see langword="true"/> and a path exists, project and page data are persisted first; the active editor itself is never flushed.
    /// <para>为 <see langword="true"/> 且路径存在时，先持久化工程与页面数据；但不会保存当前编辑器内容。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the persistence writes.
    /// <para>取消门等待与持久化写入。</para></param>
    /// <param name="confirmChange">When <see langword="false"/>, closing proceeds without the project-change gate or the host confirmation.
    /// <para>为 <see langword="false"/> 时，关闭不经过项目变更门， 也不询问宿主确认。</para></param>
    /// <returns><see langword="true"/> when the session was reset to a closed state; <see langword="false"/> when an image operation is running or the host declined the change.
    /// <para>会话成功重置为已关闭状态时返回 <see langword="true"/>；镜像操作运行中或宿主拒绝变更时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during the gate wait, or during persistence.
    /// <para>令牌在门等待前后或持久化期间被取消。</para></exception>
    public async Task<bool> CloseAsync(bool persistProject = false, CancellationToken cancellationToken = default, bool confirmChange = true)
    {
        var closed = false;
        await RunOperationAsync(async () =>
        {
            if (_workspace.IsImageOperationRunning) return;
            if (persistProject && CurrentProjectPath is { } path) await PersistCoreAsync(path, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _workspace.ClearImages();
            _workspace.ClearNodes();
            _workspace.ChangeProjectRoot(null, preserveDocuments: false);
            History.Clear();
            Context.CloseProject();
            CurrentProjectPath = null;
            SolutionState = null;
            closed = true;
            Changed?.Invoke();
        }, changingProject: confirmChange, cancellationToken: cancellationToken);
        return closed;
    }

    /// <summary>
    /// Exports the current on-disk project under the same operation gate as persistence.
    /// <para>在与持久化相同的操作门下导出磁盘上的当前工程。</para>
    /// </summary>
    /// <param name="outputPath">Destination archive path. <para>目标归档文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the archive writes.
    /// <para>取消门等待与归档写入。</para></param>
    /// <returns>Completes after the solution directory has been archived; the data exported is exactly
    /// what is on disk from the last successful save.
    /// <para>解决方案目录归档完成后返回；导出的内容即上次成功保存后磁盘上的数据。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open.
    /// <para>尚未打开解决方案。</para></exception>
    public Task ExportAsync(string outputPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(() => ProjectArchiveService.ExportAsync(
            Context.RootDirectory ?? throw new InvalidOperationException("No solution is open."), outputPath, cancellationToken),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Persists current project data, then exports a reopenable package under the operation gate.
    /// <para>先持久化当前工程数据，再在操作门下导出一个可重新打开的包。</para>
    /// </summary>
    /// <param name="outputPath">Destination package path. <para>目标包文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait, the persistence writes and the package
    /// export. <para>取消门等待、持久化写入与包导出。</para></param>
    /// <returns>Completes after the project has been saved and the package written.
    /// <para>工程保存且包写入完成后返回。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open.
    /// <para>尚未打开解决方案。</para></exception>
    public Task ExportPackageAsync(string outputPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(async () =>
        {
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            await PersistCoreAsync(path, true, cancellationToken);
            await ProjectArchiveService.ExportPackageAsync(path, outputPath, cancellationToken);
        }, cancellationToken: cancellationToken);

    /// <summary>Imports a package into a new directory and opens it after host confirmation.
    /// <para>将包导入到新目录，并在宿主确认后打开它。</para></summary>
    /// <param name="archivePath">Package file produced by <see cref="ExportPackageAsync"/>.
    /// <para>由 <see cref="ExportPackageAsync"/> 生成的包文件。</para></param>
    /// <param name="destinationDirectory">Directory the package is extracted into.
    /// <para>解包目标目录。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the extraction.
    /// <para>取消门等待与解包。</para></param>
    /// <returns><see langword="true"/> when the extracted solution was opened; <see langword="false"/> when the extracted solution file cannot be read or the operation was gated.
    /// <para>解出的解决方案成功打开时返回 <see langword="true"/>；解出的解决方案文件无法读取或操作被门 拦截时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="IOException"><paramref name="destinationDirectory"/> already exists as a file or directory.
    /// <para><paramref name="destinationDirectory"/> 已作为文件或目录存在。</para></exception>
    public async Task<bool> ImportPackageAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var opened = false;
        await RunOperationAsync(async () =>
        {
            var path = await ProjectPackage.ImportAsync(archivePath, destinationDirectory, cancellationToken);
            opened = await OpenProjectFromPathCoreAsync(path, CancellationToken.None);
        }, changingProject: true, cancellationToken: cancellationToken);
        return opened;
    }
}