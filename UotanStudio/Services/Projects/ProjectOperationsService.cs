using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Projects;

/// <summary>
/// <para>项目操作服务：从 <c>MenuViewModel</c> 提取的核心项目管理逻辑，
/// 包括新建/打开/保存/另存/添加子项目/导入文件。供 <c>MenuViewModel</c>
/// 薄代理调用，也可独立使用（如命令行入口）。</para>
/// Project operations service: core project management logic extracted from
/// <c>MenuViewModel</c>, covering new/open/save/save-as/add-sub-project/import-files.
/// Called by <c>MenuViewModel</c> as a thin delegation target; also usable independently
/// (e.g. from the command-line entry point).
/// </summary>
public sealed class ProjectOperationsService
{
    private static readonly Lazy<ProjectOperationsService> LazyInstance = new(() => new ProjectOperationsService());

    /// <summary><para>单例实例。</para>Singleton instance.</summary>
    public static ProjectOperationsService Instance => LazyInstance.Value;

    private ProjectOperationsService() { }

    /// <summary>
    /// <para>当前工程文件路径，由保存/打开操作填充。</para>
    /// Current project file path, populated by save/open operations.
    /// </summary>
    public string? CurrentProjectPath { get; set; }

    /// <summary>
    /// <para>当前节点编辑器视图模型引用，由外部设置以便持久化访问画布。</para>
    /// Current node editor view model reference, set externally so persistence can access the canvas.
    /// </summary>
    public NodeEditorViewModel? NodeEditorVM { get; set; }

    /// <summary>
    /// <para>当前工作区视图模型引用，由外部设置以便 Ctrl+S 保存活动文档。</para>
    /// Current workspace view model reference, set externally so Ctrl+S can save the active document.
    /// </summary>
    public WorkspaceViewModel? WorkspaceVM { get; set; }

    /// <summary>
    /// <para>在指定路径创建并持久化一个空解决方案（固件项目 + 节点项目 + 状态目录）。</para>
    /// Creates and persists an empty solution (firmware project + node project + state directory) at the
    /// specified path.
    /// </summary>
    public async Task CreateNewSolutionAsync(string path)
    {
        ProjectContextService.Instance.OpenSolution(path);
        CurrentProjectPath = path;
        if (NodeEditorVM != null)
        {
            NodeEditorVM.Clear();
        }

        ImageViewModel.EnsureInstance().Images.Clear();
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
            Settings = ProjectSettingViewModel.EnsureInstance().ToDto()
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

        var images = ImageViewModel.EnsureInstance();
        images.Images.Clear();

        var projectSettings = ProjectSettingViewModel.EnsureInstance();
        projectSettings.ApplyFromDto(new ProjectSettingsDto());

        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(ProjectContextService.Instance.FirmwareProjectPath ?? string.Empty);
        if (firmware is not null)
        {
            foreach (var dto in firmware.Images)
            {
                var absolute = ProjectContextService.Instance.ResolvePath(dto.RelativePath);
                if (absolute is null || !File.Exists(absolute))
                {
                    continue;
                }

                images.Images.Add(new ImageEntryViewModel
                {
                    DisplayName = Path.GetFileName(dto.RelativePath),
                    RelativePath = dto.RelativePath,
                    SizeBytes = dto.SizeBytes,
                    Format = dto.Format
                });
            }

            if (firmware.Settings is not null)
            {
                projectSettings.ApplyFromDto(firmware.Settings);
            }
        }

        if (NodeEditorVM != null)
        {
            NodeEditorVM.Clear();
        }

        var node = await SolutionPersistenceService.LoadNodeProjectAsync(ProjectContextService.Instance.NodeProjectPath ?? string.Empty);
        if (NodeEditorVM != null && node is not null)
        {
            await NodeEditorVM.LoadFromAsync(new WorkflowSnapshot
            {
                FormatVersion = 1,
                Nodes = node.Nodes,
                Connections = node.Connections
            });
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
    public async Task<bool> SaveAsync()
    {
        await SaveActiveDocumentAsync();

        if (string.IsNullOrEmpty(CurrentProjectPath))
        {
            return false;
        }

        await PersistToAsync(CurrentProjectPath);
        return true;
    }

    /// <summary>
    /// <para>另存为：复制镜像目录到新根目录后持久化。</para>
    /// Saves the solution under a new path, copying the image directory to the new root, then persisting.
    /// </summary>
    public async Task SaveAsAsync(string path)
    {
        var oldImages = ProjectContextService.Instance.ActiveSubProject?.ImagesDir;
        var oldRoot = ProjectContextService.Instance.RootDirectory;
        CurrentProjectPath = path;
        ProjectContextService.Instance.OpenSolution(path);

        var newRoot = ProjectContextService.Instance.RootDirectory;
        if (oldImages is not null && oldRoot is not null && newRoot is not null &&
            !string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(oldImages))
        {
            var newImages = ProjectContextService.Instance.EnsureImagesDirectory();
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(oldImages))
                {
                    File.Copy(file, Path.Combine(newImages, Path.GetFileName(file)), overwrite: true);
                }
            });
        }

        await PersistToAsync(path);
        WeakReferenceMessenger.Default.Send(new ProjectContextChangedMessage());
    }

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

        await ImageViewModel.EnsureInstance().ImportAsync(paths);
    }

    /// <summary>
    /// <para>向当前解决方案添加新子项目。</para>
    /// Adds a new sub-project to the current solution.
    /// </summary>
    public async Task AddSubProjectAsync(string fwPath)
    {
        var context = ProjectContextService.Instance;
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
            Settings = ProjectSettingViewModel.EnsureInstance().ToDto()
        };
        await SolutionPersistenceService.SaveFirmwareProjectAsync(fwPath, firmware);

        LogService.Instance.Write(LogLevel.Info, "Menu", $"Added sub-project: {subName}");

        if (CurrentProjectPath is not null)
        {
            await OpenProjectFromPathAsync(CurrentProjectPath);
        }
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
    public async Task ImportSubProjectAsync(string dirPath)
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
                Settings = ProjectSettingViewModel.EnsureInstance().ToDto()
            };
            await SolutionPersistenceService.SaveFirmwareProjectAsync(fwPath, firmware);
        }

        LogService.Instance.Write(LogLevel.Info, "Menu", $"Imported sub-project: {subName} from {dirPath}  已导入子项目：{subName}（{dirPath}）");

        if (CurrentProjectPath is not null)
        {
            await OpenProjectFromPathAsync(CurrentProjectPath);
        }
    }

    /// <summary>
    /// <para>若当前活动文档具有 SaveCommand 且 IsDirty，则执行保存。</para>
    /// If the active document has a SaveCommand and IsDirty, executes it.
    /// </summary>
    public async Task SaveActiveDocumentAsync()
    {
        if (WorkspaceVM?.Layout is null)
        {
            return;
        }

        var documentDock = WorkspaceVM.FindDocumentsPanePublic();
        if (documentDock?.ActiveDockable is not { } active)
        {
            return;
        }

        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? saveCmd = active switch
        {
            ManifestEditorViewModel m => m.SaveCommand,
            DtbEditorViewModel d => d.SaveCommand,
            PartitionEditorViewModel p => p.SaveCommand,
            _ => null
        };

        if (saveCmd is not null && saveCmd.CanExecute(null))
        {
            await saveCmd.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// <para>将当前工程完整快照写入指定路径。</para>
    /// Writes the complete project snapshot to the specified path.
    /// </summary>
    public async Task PersistToAsync(string path)
    {
        if (NodeEditorVM == null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Menu", "Save aborted: no node editor bound");
            return;
        }

        var context = ProjectContextService.Instance;
        var solutionName = context.SolutionName ?? Path.GetFileNameWithoutExtension(path);
        var rootDir = context.RootDirectory
            ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", solutionName);

        var node = new NodeProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = solutionName,
            Nodes = NodeEditorVM.Nodes
                .Select(n => new NodeDto
                {
                    InstanceId = n.InstanceId,
                    Title = n.Title,
                    Category = n.Category.ToString(),
                    X = n.Location.X,
                    Y = n.Location.Y,
                    TypeKey = n.TypeKey
                })
                .ToList(),
            Connections = WorkflowPersistenceService.BuildConnectionDtos(NodeEditorVM.Nodes, NodeEditorVM.Connections)
        };

        var subProjectDtos = new List<SubProjectDto>();
        foreach (var sp in context.SubProjects)
        {
            subProjectDtos.Add(new SubProjectDto
            {
                Name = sp.Name,
                RelativePath = Path.GetRelativePath(rootDir, sp.FwProjectPath).Replace(Path.DirectorySeparatorChar, '/')
            });

            var firmware = new FirmwareProjectSnapshot
            {
                FormatVersion = 1,
                ProjectName = sp.Name,
                Images = ImageViewModel.EnsureInstance().Images
                    .Select(i => new ImageEntryDto
                    {
                        RelativePath = i.RelativePath,
                        SizeBytes = i.SizeBytes,
                        Format = i.Format
                    })
                    .ToList(),
                Settings = ProjectSettingViewModel.EnsureInstance().ToDto()
            };
            await SolutionPersistenceService.SaveFirmwareProjectAsync(sp.FwProjectPath, firmware);
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

        await SolutionPersistenceService.SaveNodeProjectAsync(nodePath, node);
        await SolutionPersistenceService.SaveSolutionAsync(path, solution);
        await OperationHistoryService.Instance.SaveAsync(Path.Combine(stateDir, "history.json"));
        await SolutionSettingsService.SaveAsync(Path.Combine(stateDir, "settings.json"), new SolutionSettings());
    }
}