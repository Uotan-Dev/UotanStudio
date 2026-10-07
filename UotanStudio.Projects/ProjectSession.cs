namespace UotanStudio.Services.Projects;

using System.Text.Json;

/// <summary>
/// Coordinates project creation, loading and persistence through a workspace data boundary.
/// <para>通过工作区数据边界协调项目的创建、加载与持久化。</para>
/// </summary>
public sealed partial class ProjectSession
{
    private readonly IProjectWorkspace _workspace;

    /// <summary>
    /// Creates a session with an optional host adapter and explicitly owned context/history.
    /// <para>使用可选的宿主适配器与显式拥有的上下文/历史创建会话。</para>
    /// </summary>
    /// <param name="workspace">
    /// Host data adapter, or <see langword="null"/> to use a default <see cref="ProjectDataWorkspace"/>.
    /// <para>宿主数据适配器；为 <see langword="null"/> 时使用默认的 <see cref="ProjectDataWorkspace"/>。</para>
    /// </param>
    /// <param name="context">
    /// Project context owned by this session; <see langword="null"/> starts an empty context.
    /// <para>本会话持有的项目上下文；<see langword="null"/> 表示从空上下文开始。</para>
    /// </param>
    /// <param name="history">
    /// Operation history owned by this session; <see langword="null"/> starts an empty history.
    /// <para>本会话持有的操作历史；<see langword="null"/> 表示从空历史开始。</para>
    /// </param>
    public ProjectSession(IProjectWorkspace? workspace = null, ProjectContext? context = null, ProjectHistory? history = null)
    {
        _workspace = workspace ?? new ProjectDataWorkspace();
        Context = context ?? new();
        History = history ?? new();
    }

    /// <summary>
    /// <para>宿主钩子：持久化解决方案前捕获扩展状态（如交付计划、面板可见性）。返回 null 表示无状态。</para>
    /// Host hook: captures the extension state (e.g. delivery plan, panel visibility) before the
    /// solution file is persisted. Returning null means there is no state to store.
    /// </summary>
    /// <value>
    /// The host hook invoked before each solution write, or <see langword="null"/> when the host supplies
    /// none; a hook returning <see langword="null"/> stores no extension state.
    /// <para>每次写入解决方案文件前调用的宿主钩子；宿主未提供时为 <see langword="null"/>。钩子返回
    /// <see langword="null"/> 表示不存储扩展状态。</para>
    /// </value>
    public Func<Dictionary<string, JsonElement>?>? CaptureState { get; set; }

    /// <summary>
    /// <para>最近一次打开/另存的解决方案文件携带的扩展状态；未打开或解决方案未包含状态时为 null。</para>
    /// Extension state carried by the most recently opened/saved solution file; null when no solution
    /// is open or the file contains no state.
    /// </summary>
    /// <value>
    /// The extension state dictionary of the open solution, or <see langword="null"/> when none applies.
    /// <para>已打开解决方案的扩展状态字典；不适用时为 <see langword="null"/>。</para>
    /// </value>
    public Dictionary<string, JsonElement>? SolutionState { get; private set; }

    /// <summary>
    /// <para>最近一次打开尝试的失败原因；成功后为 <see cref="ProjectOpenFailure.None"/>。宿主据此弹出
    /// 明确的失败提示，而不是只写日志。</para>
    /// The failure reason of the most recent open attempt, or <see cref="ProjectOpenFailure.None"/> after a
    /// successful open; hosts surface an explicit dialog from it instead of relying on log output alone.
    /// </summary>
    /// <value>Set by <see cref="OpenProjectFromPathAsync"/> and cleared on success.
    /// <para>由 <see cref="OpenProjectFromPathAsync"/> 设置，成功时清除。</para></value>
    public ProjectOpenFailure OpenFailure { get; private set; }

    /// <summary>
    /// <para>最近一次打开失败时，磁盘文件里读到的 <c>FormatVersion</c>；文件不可解析或非格式版本问题时为
    /// <see langword="null"/>。</para>
    /// The <c>FormatVersion</c> read from the offending file when the most recent open failed, or
    /// <see langword="null"/> when the file is unparseable or the failure is not a version mismatch.
    /// </summary>
    /// <value>Populated for <see cref="ProjectOpenFailure.InvalidSolution"/> and
    /// <see cref="ProjectOpenFailure.InvalidNodeProject"/> when the stored version can be read.
    /// <para>在 <see cref="ProjectOpenFailure.InvalidSolution"/> 与
    /// <see cref="ProjectOpenFailure.InvalidNodeProject"/> 且能读到存储版本时填充。</para></value>
    public int? OpenFailureStoredVersion { get; private set; }

    /// <summary>
    /// <para>最近一次打开失败时真正读不到的那个文件：解决方案文件或节点项目文件；缺失文件类失败为发起打开的
    /// 路径。</para>
    /// The file that actually could not be read during the most recent failed open — the solution file or a
    /// node project file — or the requested path for a missing-file failure.
    /// </summary>
    /// <value>Set alongside <see cref="OpenFailure"/>; <see langword="null"/> before the first attempt.
    /// <para>与 <see cref="OpenFailure"/> 一同设置；首次尝试之前为 <see langword="null"/>。</para></value>
    public string? OpenFailurePath { get; private set; }

    /// <summary>
    /// <para>仅重写解决方案文件以保存扩展状态（不动 <c>.uotanfw</c>/<c>.uotannode</c>）；经既有会话操作门串行化。</para>
    /// Rewrites only the solution file to persist extension state (leaving <c>.uotanfw</c>/<c>.uotannode</c>
    /// untouched); serialized through the existing session operation gate.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels waiting for the operation gate and the solution rewrite.
    /// <para>取消等待操作门与解决方案重写。</para>
    /// </param>
    /// <returns>
    /// Completes once the rewrite ran or was skipped; read or write failures are logged, not thrown.
    /// <para>重写执行或被跳过后完成；读取或写入失败仅记录日志，不抛出异常。</para>
    /// </returns>
    public async Task PersistSolutionStateAsync(CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(async () =>
        {
            var path = CurrentProjectPath;
            if (string.IsNullOrEmpty(path)) return;
            var solution = await SolutionPersistenceService.LoadSolutionAsync(path, cancellationToken);
            if (solution is null)
            {
                ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Projects", $"State save skipped: cannot read solution '{path}'");
                return;
            }
            solution.State = CaptureState?.Invoke();
            SolutionState = solution.State;
            if (!await SolutionPersistenceService.SaveSolutionAsync(path, solution, cancellationToken))
                ProjectDiagnostics.Write(ProjectLogLevel.Error, "Projects", $"State save failed: {path}");
        }, cancellationToken: cancellationToken);
    }

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    /// <summary>
    /// Whether a lifecycle or persistence operation owns the session gate.
    /// <para>是否有生命周期或持久化操作正持有会话门。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> while an operation holds the gate; <see langword="false"/> when the session
    /// is idle. <para>操作持有门时为 <see langword="true"/>；会话空闲时为 <see langword="false"/>。</para>
    /// </value>
    public bool IsBusy => _operationGate.CurrentCount == 0;
    /// <summary>
    /// Host confirmation invoked outside the gate; it may flush existing project operations.
    /// <para>在门之外调用的宿主确认；它可以先执行既有工程操作。</para>
    /// </summary>
    /// <value>
    /// The confirmation delegate, or <see langword="null"/> to proceed without asking; returning
    /// <see langword="false"/> aborts the pending project-changing operation.
    /// <para>确认委托；为 <see langword="null"/> 时不询问直接进行；返回 <see langword="false"/> 会中止
    /// 待执行的项目切换操作。</para>
    /// </value>
    public Func<Task<bool>>? BeforeProjectChangeAsync { get; set; }

    /// <summary>
    /// Waits for the current operation without initiating a save.
    /// <para>等待当前操作完成，但不发起保存。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait. <para>取消等待。</para></param>
    /// <returns>Completes as soon as the operation gate is free.
    /// <para>操作门空闲后立即完成。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled while waiting.
    /// <para>等待期间令牌被取消。</para></exception>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        _operationGate.Release();
    }

    /// <summary>
    /// Runs a host file operation without allowing Save As, close or autosave to interleave.
    /// <para>运行宿主文件操作，期间不允许另存为、关闭或自动保存插入。</para>
    /// </summary>
    /// <param name="operation">The operation to run under the gate. <para>需在门下运行的操作。</para></param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the start of the operation.
    /// <para>取消等待门与操作启动。</para></param>
    /// <returns>Completes when the operation has finished and the gate is released.
    /// <para>操作结束并释放门后完成。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during the gate wait.
    /// <para>令牌在门等待之前或期间被取消。</para></exception>
    public Task ExecuteFileOperationAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunOperationAsync(operation, cancellationToken: cancellationToken);
    }

    private async Task RunOperationAsync(Func<Task> operation, bool changingProject = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (changingProject) await _changeGate.WaitAsync(cancellationToken);
        try
        {
            if (changingProject && _workspace.IsImageOperationRunning)
            {
                ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Projects", "Wait for the image operation before switching projects.");
                return;
            }
            if (changingProject && BeforeProjectChangeAsync is { } flush && !await flush()) return;
            await _operationGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (changingProject && _workspace.IsImageOperationRunning) return;
                await operation();
            }
            finally { _operationGate.Release(); }
        }
        finally { if (changingProject) _changeGate.Release(); }
    }

    /// <summary>
    /// Saves host documents and optionally project/page data; overlapping ticks are skipped.
    /// <para>保存宿主文档，并可选保存工程/页面数据；重叠的定时触发会被跳过。</para>
    /// </summary>
    /// <param name="persistProject">When <see langword="false"/>, only host documents are saved and the
    /// project files are left untouched. <para>为 <see langword="false"/> 时仅保存宿主文档，不改写工程文件。</para></param>
    /// <param name="persistPageState">Passed to persistence; <see langword="false"/> keeps the stored
    /// open-document lists instead of capturing the current editors.
    /// <para>传递给持久化；为 <see langword="false"/> 时保留已存储的打开文档列表，不采集当前编辑器状态。</para></param>
    /// <param name="cancellationToken">Cancels the persistence writes. <para>取消持久化写入。</para></param>
    /// <returns>Completes after the save attempt; does nothing when another operation already owns the
    /// gate. <para>保存尝试后完成；若门已被其他操作占用则什么都不做。</para></returns>
    public async Task AutoSaveAsync(bool persistProject = true, bool persistPageState = true, CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            await _workspace.AutoSaveDocumentsAsync();
            if (persistProject && CurrentProjectPath is { Length: > 0 } path) await PersistCoreAsync(path, persistPageState, cancellationToken);
        }
        finally { _operationGate.Release(); }
    }
    /// <summary>
    /// <para>当前工程文件路径，由保存/打开操作填充。</para>
    /// Current project file path, populated by save/open operations.
    /// </summary>
    /// <value>
    /// The full path of the open solution file, or <see langword="null"/> when no solution is open.
    /// <para>已打开解决方案文件的完整路径；未打开解决方案时为 <see langword="null"/>。</para>
    /// </value>
    public string? CurrentProjectPath { get => Context.CurrentSolutionPath; internal set => Context.SetCompatibilitySolutionPath(value); }

    /// <summary>
    /// <para>在指定路径创建并持久化一个空解决方案（固件项目 + 状态目录）。节点项目不会被自动创建，
    /// 需要时经 <see cref="CreateNodeProjectAsync"/> 显式登记。</para>
    /// Creates and persists an empty solution (firmware project + state directory) at the specified path.
    /// No node project is created; register one with <see cref="CreateNodeProjectAsync"/> when needed.
    /// </summary>
    /// <param name="path">Solution file path to create; missing parent directories are created.
    /// <para>要创建的解决方案文件路径；缺失的父目录会被创建。</para></param>
    /// <param name="cancellationToken">Cancels staging before the transaction commits.
    /// <para>在事务提交前取消暂存。</para></param>
    /// <returns>Completes after the whole file set has been staged through one transaction, committed,
    /// and the session context switched to the new solution.
    /// <para>整套文件经同一事务暂存、提交并切换会话上下文后完成。</para></returns>
    public Task CreateNewSolutionAsync(string path, CancellationToken cancellationToken = default) => RunOperationAsync(() => CreateNewSolutionCoreAsync(path, cancellationToken), true, cancellationToken);

    private async Task CreateNewSolutionCoreAsync(string path, CancellationToken cancellationToken)
    {
        using var transaction = new ProjectFileTransaction();
        var target = new ProjectContext();
        target.OpenSolution(path);

        var solutionName = target.SolutionName ?? Path.GetFileNameWithoutExtension(path);
        var rootDir = target.RootDirectory
            ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", solutionName);
        var subProjectDir = Path.Combine(rootDir, solutionName);
        var stateDir = Path.Combine(subProjectDir, ".uotan");
        var subImagesDir = Path.Combine(subProjectDir, "images");
        var subOutputDir = Path.Combine(subProjectDir, "output");
        Directory.CreateDirectory(subImagesDir);
        Directory.CreateDirectory(subOutputDir);
        Directory.CreateDirectory(Path.Combine(subProjectDir, "delivery"));

        var firmwarePath = Path.Combine(subProjectDir, solutionName + ".uotanfw");
        var firmware = new FirmwareProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = solutionName,
            Images = [],
            Settings = _workspace.CaptureSettings()
        };
        var solution = new SolutionSnapshot
        {
            FormatVersion = 1,
            SolutionName = solutionName,
            SubProjects =
            [
                new SubProjectDto { Name = solutionName, RelativePath = $"{solutionName}/{solutionName}.uotanfw" }
            ]
        };

        await transaction.StageAsync(firmwarePath, firmware, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);
        await transaction.StageAsync(path, solution, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
        await transaction.StageAsync(Path.Combine(stateDir, "history.json"), [], HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken);
        await transaction.StageAsync(Path.Combine(stateDir, "settings.json"), new SolutionSettings(), SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken);
        transaction.Commit(cancellationToken);
        Context.OpenSolution(path, solution);
        CurrentProjectPath = Path.GetFullPath(path);
        _workspace.ClearNodes();
        _workspace.ClearImages();
        History.Clear();
        _workspace.ChangeProjectRoot(Context.RootDirectory!, preserveDocuments: false);
        Changed?.Invoke();
        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu",
            $"Created solution: {solutionName} | solution={Path.GetFullPath(path)} | root={rootDir} | sub-project={subProjectDir} | firmware={firmwarePath} | state={stateDir} | images={subImagesDir} | output={subOutputDir} | delivery={Path.Combine(subProjectDir, "delivery")}");
    }

    /// <summary>
    /// <para>从指定路径打开解决方案：加载固件项目与节点项目，重建镜像清单、打包设置、
    /// 画布与操作历史。返回是否成功。</para>
    /// Opens the solution at the specified path: loads the firmware and node projects, rebuilding the
    /// image list, packing settings, canvas and operation history. Returns whether the open succeeded.
    /// </summary>
    /// <param name="path">Solution file to open; a missing file aborts without touching session state.
    /// <para>要打开的解决方案文件；文件缺失时中止且不影响现有会话状态。</para></param>
    /// <param name="cancellationToken">Cancels the reads performed before any state is swapped in.
    /// <para>取消状态替换前的各项读取。</para></param>
    /// <returns><see langword="true"/> when the solution and its projects loaded and the session was
    /// rebound; <see langword="false"/> when the file is missing or the solution or node project cannot
    /// be read. <para>解决方案及其项目成功加载并完成会话重绑定时返回 <see langword="true"/>；文件缺失
    /// 或解决方案/节点项目无法读取时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled during the reads, before the
    /// session state was replaced. <para>令牌在读取阶段被取消，此时会话状态尚未替换。</para></exception>
    public async Task<bool> OpenProjectFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var opened = false;
        await RunOperationAsync(async () => opened = await OpenProjectFromPathCoreAsync(path, cancellationToken), true, cancellationToken);
        return opened;
    }

    private async Task<bool> OpenProjectFromPathCoreAsync(string path, CancellationToken cancellationToken)
    {
        OpenFailure = ProjectOpenFailure.None;
        OpenFailureStoredVersion = null;
        OpenFailurePath = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            OpenFailure = ProjectOpenFailure.MissingFile;
            OpenFailurePath = path;
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Menu", $"Open aborted: missing file '{path}'");
            return false;
        }

        var solution = await SolutionPersistenceService.LoadSolutionAsync(path, cancellationToken);
        if (solution is null)
        {
            OpenFailure = ProjectOpenFailure.InvalidSolution;
            OpenFailureStoredVersion = ReadStoredFormatVersion(path);
            OpenFailurePath = path;
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Open aborted: failed to load solution '{path}'");
            return false;
        }

        var target = new ProjectContext();
        try
        {
            target.OpenSolution(path, solution);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            OpenFailure = ProjectOpenFailure.InvalidSolution;
            OpenFailureStoredVersion = ReadStoredFormatVersion(path);
            OpenFailurePath = path;
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Open aborted: invalid solution '{path}': {ex.Message}");
            return false;
        }
        var firmware = target.FirmwareProjectPath is { } firmwarePath
            ? await SolutionPersistenceService.LoadFirmwareProjectAsync(firmwarePath, cancellationToken) : null;
        List<(string Name, NodeProjectSnapshot Snapshot)> openedNodeProjects = new(target.NodeProjects.Count);
        foreach (var descriptor in target.NodeProjects.Where(project => project.IsOpen))
        {
            if (target.ResolveNodeProjectPath(descriptor) is not { } nodePath) continue;
            var node = await SolutionPersistenceService.LoadNodeProjectAsync(nodePath, cancellationToken);
            if (node is null)
            {
                if (File.Exists(nodePath))
                {
                    OpenFailure = ProjectOpenFailure.InvalidNodeProject;
                    OpenFailureStoredVersion = ReadStoredFormatVersion(nodePath);
                    OpenFailurePath = nodePath;
                    ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Open aborted: unreadable node project '{nodePath}'.");
                    return false;
                }
                ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Menu", $"Node project file is missing: {nodePath}");
                continue;
            }
            openedNodeProjects.Add((descriptor.Name, node));
        }
        var history = new ProjectHistory();
        if (target.StateDirectory is { } stateDirectory)
        {
            await history.LoadAsync(Path.Combine(stateDirectory, "history.json"), cancellationToken);
            await SolutionSettingsService.LoadAsync(Path.Combine(stateDirectory, "settings.json"), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Context.OpenSolution(path, solution);
        SolutionState = solution.State;
        CurrentProjectPath = Path.GetFullPath(path);
        _workspace.ChangeProjectRoot(Context.RootDirectory!, preserveDocuments: false);

        _workspace.ClearImages();
        _workspace.ApplySettings(new ProjectSettingsDto());

        if (firmware is not null)
        {
            _workspace.RestoreImages(firmware.Images.Where(dto =>
            {
                var absolute = Context.ResolvePath(dto.RelativePath);
                return _workspace.PreserveMissingImages || absolute is not null && File.Exists(absolute);
            }));

            if (firmware.Settings is not null)
            {
                _workspace.ApplySettings(firmware.Settings);
            }

            if (firmware.OpenDocuments is { Count: > 0 } openDocs)
            {
                var rootDir = Context.RootDirectory ?? Path.GetDirectoryName(path) ?? ".";
                await _workspace.RestoreDocumentsAsync(openDocs, rootDir);
            }
        }

        _workspace.ClearNodes();

        foreach (var (name, snapshot) in openedNodeProjects)
            await _workspace.RestoreWorkflowAsync(name, snapshot);
        if (Context.ActiveNodeProject is { } activeNodeProject)
            _workspace.ActivateWorkflow(activeNodeProject.Name);

        History.CopyFrom(history);

        SolutionReloaded?.Invoke();
        Changed?.Invoke();
        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu",
            $"Opened solution: {path} | root={Context.RootDirectory} | sub-projects={Context.SubProjects.Count} | node-projects={Context.NodeProjects.Count} | canvases={openedNodeProjects.Count} | images={firmware?.Images.Count ?? 0}");
        return true;
    }

    /// <summary>
    /// <para>保存当前项目：先尝试保存当前活动的文档编辑器，再持久化工程快照。
    /// 若无路径则返回 false 以便调用方触发另存流程。</para>
    /// Saves the current project: first tries to save the active document editor, then persists the
    /// project snapshot. Returns false when there is no path so the caller can trigger Save As.
    /// </summary>
    /// <param name="saveActiveDocument">When <see langword="false"/>, the active editor is not flushed
    /// before the snapshot is persisted. <para>为 <see langword="false"/> 时，持久化快照前不保存当前活动编辑器。</para></param>
    /// <param name="cancellationToken">Cancels waiting for the gate and the persistence writes.
    /// <para>取消等待门与持久化写入。</para></param>
    /// <returns><see langword="true"/> when the snapshot was written; <see langword="false"/> when no
    /// project path is set yet. <para>快照写入成功时返回 <see langword="true"/>；尚未设置工程路径时
    /// 返回 <see langword="false"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during the gate
    /// wait. <para>令牌在门等待之前或期间被取消。</para></exception>
    public async Task<bool> SaveAsync(bool saveActiveDocument = true, CancellationToken cancellationToken = default)
    {
        var saved = false;
        await RunOperationAsync(async () =>
        {
            if (saveActiveDocument) await SaveActiveDocumentAsync();
            if (string.IsNullOrEmpty(CurrentProjectPath)) return;
            await PersistCoreAsync(CurrentProjectPath, true, cancellationToken);
            saved = true;
        }, cancellationToken: cancellationToken);
        return saved;
    }

    /// <summary>
    /// <para>另存为：准备完整项目副本与快照，发布成功后切换上下文。</para>
    /// Saves a prepared project copy under a new path, switching context only after publication succeeds.
    /// </summary>
    /// <param name="path">Destination solution file path; a destination identical to the open solution
    /// performs an in-place save instead of a copy.
    /// <para>目标解决方案文件路径；与当前打开解决方案相同时执行原地保存而非复制。</para></param>
    /// <param name="cancellationToken">Cancels the copy and the publication.
    /// <para>取消复制与发布。</para></param>
    /// <returns>Completes after the copy has been published and the session root switched to it; the
    /// source context and files stay intact when publication fails.
    /// <para>副本发布并切换会话根目录后完成；发布失败时源上下文与文件保持不变。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.
    /// <para><paramref name="path"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> resolves to an empty file name,
    /// <c>.</c>/<c>..</c>, or carries no extension. <para><paramref name="path"/> 解析出的文件名为空、为
    /// <c>.</c>/<c>..</c>，或缺少扩展名。</para></exception>
    /// <exception cref="IOException">A source sub-project lies inside the destination directory, or the
    /// copied tree contains a symbolic link or junction.
    /// <para>源子项目位于目标目录之内，或被复制的目录树包含符号链接/联接点。</para></exception>
    public Task SaveAsAsync(string path, CancellationToken cancellationToken = default) => RunOperationAsync(() => SaveAsCoreAsync(path, cancellationToken), true, cancellationToken);

    /// <summary>
    /// <para>将若干文件导入当前解决方案的镜像清单。</para>
    /// Imports the given files into the current solution's image list.
    /// </summary>
    /// <param name="paths">Files to import; <see langword="null"/> or an empty array is a no-op.
    /// <para>要导入的文件；为 <see langword="null"/> 或空数组时不执行任何操作。</para></param>
    /// <returns>Completes when the workspace has finished importing.
    /// <para>工作区完成导入后返回。</para></returns>
    public async Task ImportFilesAsync(string[] paths)
    {
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        await _workspace.ImportFilesAsync(paths);
    }

    /// <summary>
    /// <para>向当前解决方案添加新子项目。</para>
    /// Adds a new sub-project to the current solution.
    /// </summary>
    /// <param name="fwPath">Path of the new <c>.uotanfw</c> file inside a direct child directory of the
    /// solution root. <para>解决方案根的直接子目录下新 <c>.uotanfw</c> 文件的路径。</para></param>
    /// <param name="cancellationToken">Cancels staging and the solution rewrite.
    /// <para>取消暂存与解决方案重写。</para></param>
    /// <returns>Completes after the reference is registered and the solution is reloaded; does nothing
    /// when the directory is not a direct child of the root or the name is already taken.
    /// <para>引用注册并重新加载解决方案后完成；目录不是根的直接子级或名称已存在时不执行任何操作。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open.
    /// <para>尚未打开解决方案。</para></exception>
    /// <exception cref="IOException">The <c>.uotanfw</c> file already exists; import its directory
    /// instead. <para>该 <c>.uotanfw</c> 文件已存在，应导入其所在目录。</para></exception>
    /// <exception cref="InvalidDataException">The current solution file cannot be read or fails
    /// validation. <para>当前解决方案文件无法读取或未通过校验。</para></exception>
    public Task AddSubProjectAsync(string fwPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(() => AddSubProjectCoreAsync(fwPath, cancellationToken), changingProject: true, cancellationToken: cancellationToken);

    private async Task AddSubProjectCoreAsync(string fwPath, CancellationToken cancellationToken)
    {
        using var transaction = new ProjectFileTransaction();
        var context = Context;
        if (!context.IsProjectOpen || CurrentProjectPath is null)
            throw new InvalidOperationException("No solution is open.");
        fwPath = Path.GetFullPath(fwPath);
        if (File.Exists(fwPath)) throw new IOException("The sub-project file already exists; import its directory instead.");
        var subProjectDir = Path.GetDirectoryName(fwPath)!;
        if (!ProjectValidation.IsDirectSubProjectDirectory(context.RootDirectory!, subProjectDir)) return;
        var subName = Path.GetFileNameWithoutExtension(fwPath);

        if (context.SubProjects.Any(sp => string.Equals(sp.Name, subName, StringComparison.OrdinalIgnoreCase)))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Add sub-project aborted: \"{subName}\" already exists");
            return;
        }

        var imagesDir = Path.Combine(subProjectDir, "images");
        var outputDir = Path.Combine(subProjectDir, "output");
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(subProjectDir, "delivery"));

        var firmware = new FirmwareProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = subName,
            Images = [],
            Settings = _workspace.CaptureSettings()
        };
        await transaction.StageAsync(fwPath, firmware, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);

        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu",
            $"Added sub-project: {subName} | firmware={fwPath} | directory={subProjectDir} | state={Path.Combine(subProjectDir, ".uotan")} | images={imagesDir} | output={outputDir} | delivery={Path.Combine(subProjectDir, "delivery")}");

        await RegisterSubProjectAsync(fwPath, subName, transaction, cancellationToken);
    }

    /// <summary>
    /// <para>将现有目录作为子项目导入当前解决方案：扫描目录中已有的镜像文件并注册，
    /// 若目录下无 <c>.uotanfw</c> 文件则自动创建，<c>images/</c> 和 <c>output/</c>
    /// 目录已存在时保留原内容。</para>
    /// Imports an existing directory as a sub-project into the current solution: scans for existing
    /// image files and registers them; creates a <c>.uotanfw</c> file if missing, preserving
    /// existing <c>images/</c> and <c>output/</c> directories.
    /// </summary>
    /// <param name="dirPath"><para>要导入的目录绝对路径。</para>Absolute path of the directory to import.</param>
    /// <param name="cancellationToken">Cancels staging and the solution rewrite.
    /// <para>取消暂存与解决方案重写。</para></param>
    /// <returns>Completes after the sub-project is registered and the solution is reloaded; does nothing
    /// when no solution is open, the directory is missing or not a direct child of the root, or the name
    /// collides. <para>子项目注册并重新加载解决方案后完成；未打开解决方案、目录缺失、目录不是根的直接
    /// 子级或名称冲突时不执行任何操作。</para></returns>
    /// <exception cref="InvalidDataException">The current solution file cannot be read or fails
    /// validation. <para>当前解决方案文件无法读取或未通过校验。</para></exception>
    public Task ImportSubProjectAsync(string dirPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(() => ImportSubProjectCoreAsync(dirPath, cancellationToken), changingProject: true, cancellationToken: cancellationToken);

    private async Task ImportSubProjectCoreAsync(string dirPath, CancellationToken cancellationToken)
    {
        using var transaction = new ProjectFileTransaction();
        var context = Context;
        if (context.RootDirectory is { } solutionRoot && !ProjectValidation.IsDirectSubProjectDirectory(solutionRoot, dirPath)) return;
        if (!context.IsProjectOpen || context.RootDirectory is null)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Menu", "Import sub-project aborted: no solution is open");
            return;
        }

        if (!Directory.Exists(dirPath))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Import sub-project aborted: directory not found: {dirPath}");
            return;
        }

        var subName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath));
        if (string.IsNullOrEmpty(subName))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", "Import sub-project aborted: invalid directory name");
            return;
        }

        if (context.SubProjects.Any(sp => string.Equals(sp.Name, subName, StringComparison.OrdinalIgnoreCase)))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Import sub-project aborted: \"{subName}\" already exists");
            return;
        }

        var imagesDir = Path.Combine(dirPath, "images");
        var outputDir = Path.Combine(dirPath, "output");
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(dirPath, "delivery"));

        var fwPath = Path.Combine(dirPath, subName + ".uotanfw");
        var createFirmware = !File.Exists(fwPath);
        if (createFirmware)
        {
            var firmware = new FirmwareProjectSnapshot
            {
                FormatVersion = 1,
                ProjectName = subName,
                Images = [],
                Settings = _workspace.CaptureSettings()
            };
            await transaction.StageAsync(fwPath, firmware, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);
        }

        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu",
            $"Imported sub-project: {subName} from {dirPath} | firmware={fwPath} ({(createFirmware ? "created" : "existing")}) | images={imagesDir} | output={outputDir} | delivery={Path.Combine(dirPath, "delivery")}");

        await RegisterSubProjectAsync(fwPath, subName, transaction, cancellationToken);
    }

    /// <summary>Persists new references before reloading, while already holding the operation gate.</summary>
    private async Task RegisterSubProjectAsync(string firmwarePath, string name, ProjectFileTransaction transaction, CancellationToken cancellationToken)
    {
        var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
        var context = Context;
        var root = context.RootDirectory ?? throw new InvalidOperationException("No solution directory is available.");
        var snapshot = await SolutionPersistenceService.LoadSolutionAsync(path, cancellationToken)
            ?? throw new InvalidDataException("Cannot read the current solution.");
        snapshot.FormatVersion = 1;
        snapshot.SubProjects ??= [];
        foreach (var project in context.SubProjects)
            if (!snapshot.SubProjects.Any(sp => string.Equals(sp.Name, project.Name, StringComparison.OrdinalIgnoreCase)))
                snapshot.SubProjects.Add(new() { Name = project.Name, RelativePath = Path.GetRelativePath(root, project.FwProjectPath).Replace(Path.DirectorySeparatorChar, '/') });
        snapshot.SubProjects.Add(new() { Name = name, RelativePath = Path.GetRelativePath(root, firmwarePath).Replace(Path.DirectorySeparatorChar, '/') });
        ProjectValidation.ValidateSolution(snapshot);
        await transaction.StageAsync(path, snapshot, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
        transaction.Commit(cancellationToken);
        await OpenProjectFromPathCoreAsync(path, CancellationToken.None);
    }

    private static void RequireSaved(bool saved, string path)
    {
        if (!saved) throw new IOException($"Could not save project state: {path}");
    }

    /// <summary>
    /// <para>若当前活动文档具有 SaveCommand 且 IsDirty，则执行保存。</para>
    /// If the active document has a SaveCommand and IsDirty, executes it.
    /// </summary>
    /// <returns>Completes after the workspace has saved the active document, if applicable.
    /// <para>工作区完成活动文档保存（如适用）后返回。</para></returns>
    public Task SaveActiveDocumentAsync() => _workspace.SaveActiveDocumentAsync();

    /// <summary>
    /// <para>将当前工程完整快照写入指定路径。</para>
    /// Writes the complete project snapshot to the specified path.
    /// </summary>
    /// <param name="path">Solution file to overwrite; must identify the active solution.
    /// <para>要覆写的解决方案文件；必须指向当前活动解决方案。</para></param>
    /// <param name="cancellationToken">Cancels waiting for the gate and the writes.
    /// <para>取消等待门与写入。</para></param>
    /// <returns>Completes when the staged file set has been committed.
    /// <para>暂存的文件集提交完成后返回。</para></returns>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> does not identify the active
    /// solution; use <see cref="SaveAsAsync"/> for another path.
    /// <para><paramref name="path"/> 与当前活动解决方案不一致；请改用 <see cref="SaveAsAsync"/>。</para></exception>
    public Task PersistToAsync(string path, CancellationToken cancellationToken = default) => PersistToAsync(path, persistPageState: true, cancellationToken);

    /// <summary>
    /// Persists the complete project snapshot to a solution path under the operation gate, optionally
    /// keeping the stored open-document lists.
    /// <para>在操作门下将完整工程快照写入解决方案路径，可选保留已存储的打开文档列表。</para>
    /// </summary>
    /// <param name="path">Solution file to overwrite; compared against the active solution as full paths,
    /// case-insensitively on Windows. <para>要覆写的解决方案文件；按完整路径与当前活动解决方案比较，
    /// Windows 上大小写不敏感。</para></param>
    /// <param name="persistPageState">
    /// <see langword="false"/> keeps the stored <c>OpenDocuments</c> and recent-document list instead of
    /// capturing the current editors. <para>为 <see langword="false"/> 时保留已存储的 <c>OpenDocuments</c>
    /// 与最近文档列表，不采集当前编辑器状态。</para></param>
    /// <param name="cancellationToken">Cancels waiting for the gate and the writes.
    /// <para>取消等待门与写入。</para></param>
    /// <returns>Completes when the staged file set has been committed.
    /// <para>暂存的文件集提交完成后返回。</para></returns>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> does not identify the active
    /// solution; use <see cref="SaveAsAsync"/> for another path.
    /// <para><paramref name="path"/> 与当前活动解决方案不一致；请改用 <see cref="SaveAsAsync"/>。</para></exception>
    public Task PersistToAsync(string path, bool persistPageState, CancellationToken cancellationToken = default)
        => RunOperationAsync(() => PersistCoreAsync(path, persistPageState, cancellationToken), cancellationToken: cancellationToken);

    private async Task PersistCoreAsync(string path, bool persistPageState = true, CancellationToken cancellationToken = default)
    {
        using var transaction = new ProjectFileTransaction();
        var context = Context;
        if (context.CurrentSolutionPath is { } current && !string.Equals(Path.GetFullPath(path), current,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The active solution changed. Use SaveAsAsync to save to another path.");
        var solutionName = context.SolutionName ?? Path.GetFileNameWithoutExtension(path);
        var rootDir = context.RootDirectory
            ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", solutionName);

        var openDocs = persistPageState ? _workspace.CaptureDocuments(rootDir) : [];

        List<SubProjectDto> subProjectDtos = new(context.SubProjects.Count);
        foreach (var sp in context.SubProjects)
        {
            subProjectDtos.Add(new SubProjectDto
            {
                Name = sp.Name,
                RelativePath = Path.GetRelativePath(rootDir, sp.FwProjectPath).Replace(Path.DirectorySeparatorChar, '/')
            });

            if (sp != context.ActiveSubProject) continue;

            var firmware = new FirmwareProjectSnapshot
            {
                FormatVersion = 1,
                ProjectName = sp.Name,
                Images = _workspace.CaptureImages(),
                Settings = _workspace.CaptureSettings(),
                OpenDocuments = persistPageState ? (openDocs.Count > 0 ? openDocs : null)
                    : (await SolutionPersistenceService.LoadFirmwareProjectAsync(sp.FwProjectPath, cancellationToken))?.OpenDocuments
            };
            await transaction.StageAsync(sp.FwProjectPath, firmware, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);
        }

        var nodeProjectDtos = NodeProjectReferences();
        foreach (var descriptor in context.NodeProjects)
        {
            if (!IsWorkflowOpen(_workspace.OpenWorkflowNames, descriptor.Name)) continue;
            if (context.ResolveNodeProjectPath(descriptor) is not { } nodePath) continue;
            if (!_workspace.TryCaptureWorkflow(descriptor.Name, out var snapshot)) continue;
            await transaction.StageAsync(nodePath, snapshot, SolutionJsonContext.Default.NodeProjectSnapshot, cancellationToken);
        }

        var stateDir = context.StateDirectory;

        var solution = new SolutionSnapshot
        {
            FormatVersion = 1,
            SolutionName = solutionName,
            NodeProjects = nodeProjectDtos.Count > 0 ? nodeProjectDtos : null,
            SubProjects = subProjectDtos.Count > 0 ? subProjectDtos : null,
            State = CaptureState?.Invoke()
        };

        await transaction.StageAsync(path, solution, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
        if (stateDir is not null)
        {
            await transaction.StageAsync(Path.Combine(stateDir, "history.json"), History.CaptureSnapshot(), HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken);
            var settingsPath = Path.Combine(stateDir, "settings.json");
            var solutionSettings = await SolutionSettingsService.LoadAsync(settingsPath, cancellationToken) ?? new SolutionSettings();
            if (persistPageState)
                solutionSettings.RecentDocuments = [.. openDocs.Select(d => d.RelativePath).Where(p => p.Length > 0)];
            await transaction.StageAsync(settingsPath, solutionSettings, SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken);
        }
        transaction.Commit(cancellationToken);
    }

    /// <summary>
    /// Whether the host keeps a node project document open under the given name.
    /// <para>宿主是否以给定名称保持节点项目文档打开。</para>
    /// </summary>
    /// <param name="openNames">Names reported by the host. <para>宿主报告的名称列表。</para></param>
    /// <param name="name">Node project name to look for. <para>待查找的节点项目名称。</para></param>
    /// <returns><see langword="true"/> when a name matches case-insensitively.
    /// <para>按大小写不敏感匹配到名称时返回 <see langword="true"/>。</para></returns>
    private static bool IsWorkflowOpen(IReadOnlyList<string> openNames, string name)
        => openNames.Any(open => string.Equals(open, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <para>只读取磁盘文件里的 <c>FormatVersion</c> 数值，用于在打开失败时给出可判读的迁移提示；解析失败
    /// 返回 null，不抛出异常。</para>
    /// Reads only the <c>FormatVersion</c> number from a file on disk so a failed open can report an
    /// actionable migration hint; returns null when the file cannot be parsed and never throws.
    /// </summary>
    /// <param name="path">Candidate project file. <para>待判读的工程文件。</para></param>
    /// <returns>The stored version, or <see langword="null"/> when the file is absent, larger than 32 MiB,
    /// or not a JSON object carrying the field. <para>存储的版本号；文件缺失、超过 32 MiB 或不是携带该字段的
    /// JSON 对象时为 <see langword="null"/>。</para></returns>
    private static int? ReadStoredFormatVersion(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("FormatVersion", out var version)
                && version.TryGetInt32(out var value) ? value : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

}