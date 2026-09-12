namespace UotanStudio.Services.Projects;

/// <summary>Coordinates project creation, loading and persistence through a workspace data boundary.</summary>
public sealed partial class ProjectSession
{
    private readonly IProjectWorkspace _workspace;

    /// <summary>Creates a session with an optional host adapter and explicitly owned context/history.</summary>
    public ProjectSession(IProjectWorkspace? workspace = null, ProjectContext? context = null, ProjectHistory? history = null)
    {
        _workspace = workspace ?? new ProjectDataWorkspace();
        Context = context ?? new();
        History = history ?? new();
    }

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    /// <summary>Whether a lifecycle or persistence operation owns the session gate.</summary>
    public bool IsBusy => _operationGate.CurrentCount == 0;
    /// <summary>Host confirmation invoked outside the gate; it may flush existing project operations.</summary>
    public Func<Task<bool>>? BeforeProjectChangeAsync { get; set; }

    /// <summary>Waits for the current operation without initiating a save.</summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        _operationGate.Release();
    }

    private async Task RunOperationAsync(Func<Task> operation, bool changingProject = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Serialize change confirmations without holding the persistence gate: confirmation may flush it.
        if (changingProject) await _changeGate.WaitAsync(cancellationToken);
        try
        {
            if (changingProject && _workspace.IsImageOperationRunning)
            {
                ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Projects", "Wait for the image operation before switching projects. 请等待镜像操作完成后再切换工程。");
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

    /// <summary>Saves host documents and optionally project/page data; overlapping ticks are skipped.</summary>
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
    public string? CurrentProjectPath { get => Context.CurrentSolutionPath; internal set => Context.SetCompatibilitySolutionPath(value); }

    /// <summary>
    /// <para>在指定路径创建并持久化一个空解决方案（固件项目 + 节点项目 + 状态目录）。</para>
    /// Creates and persists an empty solution (firmware project + node project + state directory) at the
    /// specified path.
    /// </summary>
    public Task CreateNewSolutionAsync(string path, CancellationToken cancellationToken = default) => RunOperationAsync(() => CreateNewSolutionCoreAsync(path, cancellationToken), true, cancellationToken);

    private async Task CreateNewSolutionCoreAsync(string path, CancellationToken cancellationToken)
    {
        using var transaction = new ProjectFileTransaction();
        var target = new ProjectContext();
        target.OpenSolution(path);

        var solutionName = target.SolutionName ?? Path.GetFileNameWithoutExtension(path);
        var rootDir = target.RootDirectory
            ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", solutionName);
        var nodePath = target.NodeProjectPath
            ?? Path.Combine(rootDir, solutionName + ".uotannode");
        var subProjectDir = Path.Combine(rootDir, solutionName);
        var stateDir = Path.Combine(subProjectDir, ".uotan");
        var subImagesDir = Path.Combine(subProjectDir, "images");
        var subOutputDir = Path.Combine(subProjectDir, "output");
        Directory.CreateDirectory(subImagesDir);
        Directory.CreateDirectory(subOutputDir);

        var firmwarePath = Path.Combine(subProjectDir, solutionName + ".uotanfw");
        var firmware = new FirmwareProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = solutionName,
            Images = [],
            Settings = _workspace.CaptureSettings()
        };
        var node = new NodeProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = solutionName,
            Nodes = [],
            Connections = []
        };
        var solution = new SolutionSnapshot
        {
            FormatVersion = 2,
            SolutionName = solutionName,
            NodeProjectPath = $"{solutionName}/{solutionName}.uotannode",
            SubProjects =
            [
                new SubProjectDto { Name = solutionName, RelativePath = $"{solutionName}/{solutionName}.uotanfw" }
            ]
        };

        await transaction.StageAsync(firmwarePath, firmware, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);
        await transaction.StageAsync(nodePath, node, SolutionJsonContext.Default.NodeProjectSnapshot, cancellationToken);
        await transaction.StageAsync(path, solution, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
        await transaction.StageAsync(Path.Combine(stateDir, "history.json"), new List<HistoryOperationDto>(), HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken);
        await transaction.StageAsync(Path.Combine(stateDir, "settings.json"), new SolutionSettings(), SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken);
        transaction.Commit(cancellationToken);
        Context.OpenSolution(path, solution);
        CurrentProjectPath = Path.GetFullPath(path);
        _workspace.ClearNodes();
        _workspace.ClearImages();
        History.Clear();
        _workspace.ChangeProjectRoot(Context.RootDirectory!, preserveDocuments: false);
        Changed?.Invoke();
    }

    /// <summary>
    /// <para>从指定路径打开解决方案：加载固件项目与节点项目，重建镜像清单、打包设置、
    /// 画布与操作历史。返回是否成功。</para>
    /// Opens the solution at the specified path: loads the firmware and node projects, rebuilding the
    /// image list, packing settings, canvas and operation history. Returns whether the open succeeded.
    /// </summary>
    public async Task<bool> OpenProjectFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var opened = false;
        await RunOperationAsync(async () => opened = await OpenProjectFromPathCoreAsync(path, cancellationToken), true, cancellationToken);
        return opened;
    }

    private async Task<bool> OpenProjectFromPathCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Menu", $"Open aborted: missing file '{path}'");
            return false;
        }

        var solution = await SolutionPersistenceService.LoadSolutionAsync(path, cancellationToken);
        if (solution is null)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Open aborted: failed to load solution '{path}'");
            return false;
        }

        var target = new ProjectContext();
        target.OpenSolution(path, solution);
        var firmware = target.FirmwareProjectPath is { } firmwarePath
            ? await SolutionPersistenceService.LoadFirmwareProjectAsync(firmwarePath, cancellationToken) : null;
        var node = await SolutionPersistenceService.LoadNodeProjectAsync(target.NodeProjectPath!, cancellationToken);
        // Read descriptors before publication; bind host actions only after the new context is active.
        var history = new ProjectHistory();
        if (target.StateDirectory is { } stateDirectory)
        {
            await history.LoadAsync(Path.Combine(stateDirectory, "history.json"), cancellationToken);
            await SolutionSettingsService.LoadAsync(Path.Combine(stateDirectory, "settings.json"), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Context.OpenSolution(path, solution);
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

            // 恢复上次打开的文档标签（文本、固件信息、文件管理器等）。
            // Restore previously open document tabs (text, firmware info, file manager, etc.).
            if (firmware.OpenDocuments is { Count: > 0 } openDocs)
            {
                var rootDir = Context.RootDirectory ?? Path.GetDirectoryName(path) ?? ".";
                await _workspace.RestoreDocumentsAsync(openDocs, rootDir);
            }
        }

        _workspace.ClearNodes();

        if (node is not null)
        {
            await _workspace.RestoreWorkflowAsync(node);
        }

        History.CopyFrom(history);

        Changed?.Invoke();
        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu", $"Opened solution: {path}");
        return true;
    }

    /// <summary>
    /// <para>保存当前项目：先尝试保存当前活动的文档编辑器，再持久化工程快照。
    /// 若无路径则返回 false 以便调用方触发另存流程。</para>
    /// Saves the current project: first tries to save the active document editor, then persists the
    /// project snapshot. Returns false when there is no path so the caller can trigger Save As.
    /// </summary>
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
    public Task SaveAsAsync(string path, CancellationToken cancellationToken = default) => RunOperationAsync(() => SaveAsCoreAsync(path, cancellationToken), true, cancellationToken);

    /// <summary>
    /// <para>将若干文件导入当前解决方案的镜像清单。</para>
    /// Imports the given files into the current solution's image list.
    /// </summary>
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
        if (!ProjectValidation.IsSubProjectDirectory(context.RootDirectory!, subProjectDir)) return;
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

        var firmware = new FirmwareProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = subName,
            Images = [],
            Settings = _workspace.CaptureSettings()
        };
        await transaction.StageAsync(fwPath, firmware, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);

        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu", $"Added sub-project: {subName}");

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
    public Task ImportSubProjectAsync(string dirPath, CancellationToken cancellationToken = default)
        => RunOperationAsync(() => ImportSubProjectCoreAsync(dirPath, cancellationToken), changingProject: true, cancellationToken: cancellationToken);

    private async Task ImportSubProjectCoreAsync(string dirPath, CancellationToken cancellationToken)
    {
        using var transaction = new ProjectFileTransaction();
        var context = Context;
        if (context.RootDirectory is { } solutionRoot && !ProjectValidation.IsSubProjectDirectory(solutionRoot, dirPath)) return;
        if (!context.IsProjectOpen || context.RootDirectory is null)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Menu", "Import sub-project aborted: no solution is open  导入子项目中止：尚未打开解决方案");
            return;
        }

        if (!Directory.Exists(dirPath))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Import sub-project aborted: directory not found: {dirPath}  导入子项目中止：目录不存在：{dirPath}");
            return;
        }

        var subName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath));
        if (string.IsNullOrEmpty(subName))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", "Import sub-project aborted: invalid directory name  导入子项目中止：无效的目录名");
            return;
        }

        if (context.SubProjects.Any(sp => string.Equals(sp.Name, subName, StringComparison.OrdinalIgnoreCase)))
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Menu", $"Import sub-project aborted: \"{subName}\" already exists  导入子项目中止：\"{subName}\" 已存在");
            return;
        }

        // 确保 images/ 和 output/ 子目录存在。
        // Ensure images/ and output/ sub-directories exist.
        Directory.CreateDirectory(Path.Combine(dirPath, "images"));
        Directory.CreateDirectory(Path.Combine(dirPath, "output"));

        // 若目录下无 .uotanfw 文件则创建；已有时跳过以保留用户修改。
        // Create a .uotanfw file if missing; skip if it already exists to preserve user changes.
        var fwPath = Path.Combine(dirPath, subName + ".uotanfw");
        if (!File.Exists(fwPath))
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

        ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu", $"Imported sub-project: {subName} from {dirPath}  已导入子项目：{subName}（{dirPath}）");

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
        snapshot.FormatVersion = 2;
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
    public Task SaveActiveDocumentAsync() => _workspace.SaveActiveDocumentAsync();

    /// <summary>
    /// <para>将当前工程完整快照写入指定路径。</para>
    /// Writes the complete project snapshot to the specified path.
    /// </summary>
    public Task PersistToAsync(string path, CancellationToken cancellationToken = default) => PersistToAsync(path, persistPageState: true, cancellationToken);

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

        var node = _workspace.CaptureWorkflow(solutionName);

        var openDocs = persistPageState ? _workspace.CaptureDocuments(rootDir) : [];

        var subProjectDtos = new List<SubProjectDto>();
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

        var nodePath = context.NodeProjectPath
            ?? Path.Combine(rootDir, solutionName + ".uotannode");
        var stateDir = context.StateDirectory;

        var solution = new SolutionSnapshot
        {
            FormatVersion = 2,
            SolutionName = solutionName,
            NodeProjectPath = $"{solutionName}/{solutionName}.uotannode",
            SubProjects = subProjectDtos.Count > 0 ? subProjectDtos : null
        };

        await transaction.StageAsync(nodePath, node, SolutionJsonContext.Default.NodeProjectSnapshot, cancellationToken);
        await transaction.StageAsync(path, solution, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
        if (stateDir is not null)
        {
            await transaction.StageAsync(Path.Combine(stateDir, "history.json"), History.CaptureSnapshot(), HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken);
            var settingsPath = Path.Combine(stateDir, "settings.json");
            var solutionSettings = await SolutionSettingsService.LoadAsync(settingsPath, cancellationToken) ?? new SolutionSettings();
            if (persistPageState)
                solutionSettings.RecentDocuments = openDocs.Select(d => d.RelativePath).Where(p => p.Length > 0).ToList();
            await transaction.StageAsync(settingsPath, solutionSettings, SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken);
        }
        transaction.Commit(cancellationToken);
    }

}
