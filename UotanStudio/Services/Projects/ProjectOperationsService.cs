using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;

namespace UotanStudio.Services.Projects;

/// <summary>Coordinates project creation, loading and persistence through a workspace data boundary.</summary>
public sealed partial class ProjectOperationsService
{
    private readonly IProjectWorkspace _workspace;

    internal ProjectOperationsService(IProjectWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    internal bool IsBusy => _operationGate.CurrentCount == 0;
    internal Func<Task<bool>>? BeforeProjectChangeAsync { get; set; }

    internal async Task WaitForIdleAsync()
    {
        await _operationGate.WaitAsync();
        _operationGate.Release();
    }

    private async Task RunOperationAsync(Func<Task> operation, bool changingProject = false)
    {
        if (changingProject && _workspace.IsImageOperationRunning)
        {
            LogService.Instance.Write(LogLevel.Warn, "Projects", "Wait for the image operation before switching projects. 请等待镜像操作完成后再切换工程。");
            return;
        }
        if (changingProject && BeforeProjectChangeAsync is { } flush && !await flush()) return;
        await _operationGate.WaitAsync();
        try { await operation(); }
        finally { _operationGate.Release(); }
    }

    internal async Task AutoSaveAsync(bool persistProject = true, bool persistPageState = true)
    {
        if (!await _operationGate.WaitAsync(0)) return;
        try
        {
            await _workspace.AutoSaveDocumentsAsync();
            if (persistProject && CurrentProjectPath is { Length: > 0 } path) await PersistCoreAsync(path, persistPageState);
        }
        finally { _operationGate.Release(); }
    }
    /// <summary>
    /// <para>当前工程文件路径，由保存/打开操作填充。</para>
    /// Current project file path, populated by save/open operations.
    /// </summary>
    public string? CurrentProjectPath { get; set; }

    /// <summary>
    /// <para>在指定路径创建并持久化一个空解决方案（固件项目 + 节点项目 + 状态目录）。</para>
    /// Creates and persists an empty solution (firmware project + node project + state directory) at the
    /// specified path.
    /// </summary>
    public Task CreateNewSolutionAsync(string path) => RunOperationAsync(() => CreateNewSolutionCoreAsync(path), true);

    private async Task CreateNewSolutionCoreAsync(string path)
    {
        ProjectContextService.Instance.OpenSolution(path);
        CurrentProjectPath = path;
        _workspace.ClearNodes();

        _workspace.ClearImages();
        OperationHistoryService.Instance.Clear();

        var solutionName = ProjectContextService.Instance.SolutionName ?? Path.GetFileNameWithoutExtension(path);
        var rootDir = ProjectContextService.Instance.RootDirectory
            ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", solutionName);
        var nodePath = ProjectContextService.Instance.NodeProjectPath
            ?? Path.Combine(rootDir, solutionName + ".uotannode");
        var stateDir = ProjectContextService.Instance.StateDirectory
            ?? Path.Combine(rootDir, ".uotan");

        var subProjectDir = Path.Combine(rootDir, solutionName);
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

        await SolutionPersistenceService.SaveFirmwareProjectAsync(firmwarePath, firmware);
        await SolutionPersistenceService.SaveNodeProjectAsync(nodePath, node);
        await SolutionPersistenceService.SaveSolutionAsync(path, solution);
        await OperationHistoryService.Instance.SaveAsync(Path.Combine(stateDir, "history.json"));
        await SolutionSettingsService.SaveAsync(Path.Combine(stateDir, "settings.json"), new SolutionSettings());

        ProjectContextService.Instance.OpenSolution(path, solution);
        WeakReferenceMessenger.Default.Send(new ProjectContextChangedMessage());
    }

    /// <summary>
    /// <para>从指定路径打开解决方案：加载固件项目与节点项目，重建镜像清单、打包设置、
    /// 画布与操作历史。返回是否成功。</para>
    /// Opens the solution at the specified path: loads the firmware and node projects, rebuilding the
    /// image list, packing settings, canvas and operation history. Returns whether the open succeeded.
    /// </summary>
    public async Task<bool> OpenProjectFromPathAsync(string path)
    {
        var opened = false;
        await RunOperationAsync(async () => opened = await OpenProjectFromPathCoreAsync(path), true);
        return opened;
    }

    private async Task<bool> OpenProjectFromPathCoreAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LogService.Instance.Write(LogLevel.Warn, "Menu", $"Open aborted: missing file '{path}'");
            return false;
        }

        var solution = await SolutionPersistenceService.LoadSolutionAsync(path);
        if (solution is null)
        {
            LogService.Instance.Write(LogLevel.Error, "Menu", $"Open aborted: failed to load solution '{path}'");
            return false;
        }

        ProjectContextService.Instance.OpenSolution(path, solution);
        CurrentProjectPath = path;

        _workspace.ClearImages();
        _workspace.ApplySettings(new ProjectSettingsDto());

        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(ProjectContextService.Instance.FirmwareProjectPath ?? string.Empty);
        if (firmware is not null)
        {
            _workspace.RestoreImages(firmware.Images.Where(dto =>
            {
                var absolute = ProjectContextService.Instance.ResolvePath(dto.RelativePath);
                return absolute is not null && File.Exists(absolute);
            }));

            if (firmware.Settings is not null)
            {
                _workspace.ApplySettings(firmware.Settings);
            }

            // 恢复上次打开的文档标签（文本、固件信息、文件管理器等）。
            // Restore previously open document tabs (text, firmware info, file manager, etc.).
            if (firmware.OpenDocuments is { Count: > 0 } openDocs)
            {
                var rootDir = ProjectContextService.Instance.RootDirectory ?? Path.GetDirectoryName(path) ?? ".";
                await _workspace.RestoreDocumentsAsync(openDocs, rootDir);
            }
        }

        _workspace.ClearNodes();

        var node = await SolutionPersistenceService.LoadNodeProjectAsync(ProjectContextService.Instance.NodeProjectPath ?? string.Empty);
        if (_workspace.HasNodeEditor && node is not null)
        {
            await _workspace.RestoreWorkflowAsync(node);
        }

        if (ProjectContextService.Instance.StateDirectory is { } stateDir)
        {
            await OperationHistoryService.Instance.LoadAsync(Path.Combine(stateDir, "history.json"));
            await SolutionSettingsService.LoadAsync(Path.Combine(stateDir, "settings.json"));
        }

        WeakReferenceMessenger.Default.Send(new ProjectContextChangedMessage());
        LogService.Instance.Write(LogLevel.Info, "Menu", $"Opened solution: {path}");
        return true;
    }

    /// <summary>
    /// <para>保存当前项目：先尝试保存当前活动的文档编辑器，再持久化工程快照。
    /// 若无路径则返回 false 以便调用方触发另存流程。</para>
    /// Saves the current project: first tries to save the active document editor, then persists the
    /// project snapshot. Returns false when there is no path so the caller can trigger Save As.
    /// </summary>
    public async Task<bool> SaveAsync(bool saveActiveDocument = true)
    {
        if (saveActiveDocument) await SaveActiveDocumentAsync();

        if (string.IsNullOrEmpty(CurrentProjectPath))
        {
            return false;
        }

        await PersistToAsync(CurrentProjectPath);
        return true;
    }

    /// <summary>
    /// <para>另存为：准备完整项目副本与快照，发布成功后切换上下文。</para>
    /// Saves a prepared project copy under a new path, switching context only after publication succeeds.
    /// </summary>
    public Task SaveAsAsync(string path) => RunOperationAsync(() => SaveAsCoreAsync(path), true);

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
    public Task AddSubProjectAsync(string fwPath)
        => RunOperationAsync(() => AddSubProjectCoreAsync(fwPath), changingProject: true);

    private async Task AddSubProjectCoreAsync(string fwPath)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || CurrentProjectPath is null)
            throw new InvalidOperationException("No solution is open.");
        var subProjectDir = Path.GetDirectoryName(fwPath)!;
        var subName = Path.GetFileNameWithoutExtension(fwPath);

        if (context.SubProjects.Any(sp => string.Equals(sp.Name, subName, StringComparison.OrdinalIgnoreCase)))
        {
            LogService.Instance.Write(LogLevel.Error, "Menu", $"Add sub-project aborted: \"{subName}\" already exists");
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
        RequireSaved(await SolutionPersistenceService.SaveFirmwareProjectAsync(fwPath, firmware), fwPath);

        LogService.Instance.Write(LogLevel.Info, "Menu", $"Added sub-project: {subName}");

        await RegisterSubProjectAsync(fwPath, subName);
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
    public Task ImportSubProjectAsync(string dirPath)
        => RunOperationAsync(() => ImportSubProjectCoreAsync(dirPath), changingProject: true);

    private async Task ImportSubProjectCoreAsync(string dirPath)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.RootDirectory is null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Menu", "Import sub-project aborted: no solution is open  导入子项目中止：尚未打开解决方案");
            return;
        }

        if (!Directory.Exists(dirPath))
        {
            LogService.Instance.Write(LogLevel.Error, "Menu", $"Import sub-project aborted: directory not found: {dirPath}  导入子项目中止：目录不存在：{dirPath}");
            return;
        }

        var subName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath));
        if (string.IsNullOrEmpty(subName))
        {
            LogService.Instance.Write(LogLevel.Error, "Menu", "Import sub-project aborted: invalid directory name  导入子项目中止：无效的目录名");
            return;
        }

        if (context.SubProjects.Any(sp => string.Equals(sp.Name, subName, StringComparison.OrdinalIgnoreCase)))
        {
            LogService.Instance.Write(LogLevel.Error, "Menu", $"Import sub-project aborted: \"{subName}\" already exists  导入子项目中止：\"{subName}\" 已存在");
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
            RequireSaved(await SolutionPersistenceService.SaveFirmwareProjectAsync(fwPath, firmware), fwPath);
        }

        LogService.Instance.Write(LogLevel.Info, "Menu", $"Imported sub-project: {subName} from {dirPath}  已导入子项目：{subName}（{dirPath}）");

        await RegisterSubProjectAsync(fwPath, subName);
    }

    /// <summary>Persists new references before reloading, while already holding the operation gate.</summary>
    private async Task RegisterSubProjectAsync(string firmwarePath, string name)
    {
        var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
        var context = ProjectContextService.Instance;
        var root = context.RootDirectory ?? throw new InvalidOperationException("No solution directory is available.");
        var snapshot = await SolutionPersistenceService.LoadSolutionAsync(path)
            ?? throw new InvalidDataException("Cannot read the current solution.");
        snapshot.FormatVersion = 2;
        snapshot.SubProjects ??= [];
        foreach (var project in context.SubProjects)
            if (!snapshot.SubProjects.Any(sp => string.Equals(sp.Name, project.Name, StringComparison.OrdinalIgnoreCase)))
                snapshot.SubProjects.Add(new() { Name = project.Name, RelativePath = Path.GetRelativePath(root, project.FwProjectPath).Replace(Path.DirectorySeparatorChar, '/') });
        snapshot.SubProjects.Add(new() { Name = name, RelativePath = Path.GetRelativePath(root, firmwarePath).Replace(Path.DirectorySeparatorChar, '/') });
        RequireSaved(await SolutionPersistenceService.SaveSolutionAsync(path, snapshot), path);
        await OpenProjectFromPathCoreAsync(path);
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
    public Task PersistToAsync(string path) => PersistToAsync(path, persistPageState: true);

    internal Task PersistToAsync(string path, bool persistPageState)
        => RunOperationAsync(() => PersistCoreAsync(path, persistPageState));

    private async Task PersistCoreAsync(string path, bool persistPageState = true)
    {
        if (!_workspace.HasNodeEditor)
        {
            LogService.Instance.Write(LogLevel.Warn, "Menu", "Save aborted: no node editor bound");
            return;
        }

        var context = ProjectContextService.Instance;
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
                    : (await SolutionPersistenceService.LoadFirmwareProjectAsync(sp.FwProjectPath))?.OpenDocuments
            };
            RequireSaved(await SolutionPersistenceService.SaveFirmwareProjectAsync(sp.FwProjectPath, firmware), sp.FwProjectPath);
        }

        var nodePath = context.NodeProjectPath
            ?? Path.Combine(rootDir, solutionName + ".uotannode");
        var stateDir = context.StateDirectory
            ?? Path.Combine(rootDir, ".uotan");

        var solution = new SolutionSnapshot
        {
            FormatVersion = 2,
            SolutionName = solutionName,
            NodeProjectPath = $"{solutionName}/{solutionName}.uotannode",
            SubProjects = subProjectDtos.Count > 0 ? subProjectDtos : null
        };

        RequireSaved(await SolutionPersistenceService.SaveNodeProjectAsync(nodePath, node), nodePath);
        RequireSaved(await SolutionPersistenceService.SaveSolutionAsync(path, solution), path);
        await OperationHistoryService.Instance.SaveAsync(Path.Combine(stateDir, "history.json"));
        var settingsPath = Path.Combine(stateDir, "settings.json");
        var solutionSettings = await SolutionSettingsService.LoadAsync(settingsPath) ?? new SolutionSettings();
        if (persistPageState)
            solutionSettings.RecentDocuments = openDocs.Select(d => d.RelativePath).Where(p => p.Length > 0).ToList();
        RequireSaved(await SolutionSettingsService.SaveAsync(settingsPath, solutionSettings), settingsPath);
    }

}