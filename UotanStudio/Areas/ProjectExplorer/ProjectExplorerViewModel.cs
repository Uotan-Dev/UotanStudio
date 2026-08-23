using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Core.Projects;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.ProjectExplorer;

/// <summary>
/// <para>项目资源管理器视图模型：展示当前工程的目录/部件树与最近工程列表，
/// 提供新建与打开工程动作。</para>
/// Project explorer view model: shows the current project's directory/component
/// tree and the recent projects list, with create/open actions.
/// </summary>
public partial class ProjectExplorerViewModel : ViewModelBase
{
    /// <summary>
    /// <para>获取工程树的根节点集合。</para>
    /// Gets the root nodes of the project tree.
    /// </summary>
    public ObservableCollection<ProjectTreeItem> Tree { get; } = [];

    /// <summary>
    /// <para>获取最近工程路径集合。</para>
    /// Gets the recent project paths.
    /// </summary>
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>
    /// <para>获取或设置是否处于忙碌（异步操作期间）。</para>
    /// Gets or sets whether the explorer is busy (during async operations).
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// <para>获取或设置状态文本。</para>
    /// Gets or sets the status text.
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// <para>获取当前工程名称；未打开时为空。</para>
    /// Gets the current project name; empty when no project is open.
    /// </summary>
    public string? ProjectName => _openedName;

    private string? _openedName;

    /// <summary>
    /// <para>初始化项目资源管理器并刷新最近列表。</para>
    /// Initializes the project explorer and refreshes the recent list.
    /// </summary>
    public ProjectExplorerViewModel()
    {
        RefreshRecent();
    }

    /// <summary>
    /// <para>刷新最近工程项目列表。</para>
    /// Refreshes the recent projects list.
    /// </summary>
    [RelayCommand]
    private void RefreshRecent()
    {
        RecentProjects.Clear();
        foreach (var path in FirmwareProjectService.RecentProjects())
        {
            RecentProjects.Add(path);
        }

        OnPropertyChanged(nameof(ProjectName));
    }

    /// <summary>
    /// <para>打开一个工程文件并重建工程树。</para>
    /// Opens a project file and rebuilds the project tree.
    /// </summary>
    /// <param name="path"><para>工程文件路径。</para>The project file path.</param>
    public async Task OpenProjectAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var project = await Task.Run(() => FirmwareProjectService.LoadProject(path));
            if (project is null)
            {
                StatusText = LanguageService.Instance.GetString("Project_OpenFailed");
                return;
            }

            BuildTree(project);
            RefreshRecent();
            StatusText = LanguageService.Instance.GetString("Project_Opened");
            LogService.Instance.Write(LogLevel.Info, "ProjectExplorer", $"Opened project: {project.Name}");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>新建并保存一个工程，随后重建工程树。</para>
    /// Creates and saves a new project, then rebuilds the tree.
    /// </summary>
    /// <param name="directory"><para>工程目录。</para>Project directory.</param>
    /// <param name="name"><para>工程名称。</para>Project name.</param>
    public async Task CreateProjectAsync(string? directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var project = await Task.Run(() =>
            {
                var p = FirmwareProjectService.CreateProject(directory, name);
                return FirmwareProjectService.SaveProject(p) ? p : null;
            });
            if (project is null)
            {
                StatusText = LanguageService.Instance.GetString("Project_SaveFailed");
                return;
            }

            BuildTree(project);
            RefreshRecent();
            StatusText = string.Format(LanguageService.Instance.GetString("Project_Created"), project.Name);
            LogService.Instance.Write(LogLevel.Info, "ProjectExplorer", $"Created project: {project.Name}");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>清理并重建工程树。</para>
    /// Clears and rebuilds the project tree.
    /// </summary>
    /// <param name="project"><para>当前工程。</para>The current project.</param>
    private void BuildTree(FirmwareProject project)
    {
        _openedName = project.Name;
        Tree.Clear();
        var root = new ProjectTreeItem(_openedName ?? project.RootDirectory, project.RootDirectory, true);
        Tree.Add(root);
        PopulateDirectory(root, project.RootDirectory);
        OnPropertyChanged(nameof(ProjectName));
    }

    /// <summary>
    /// <para>递归填充目录节点。</para>
    /// Recursively populates a directory node.
    /// </summary>
    /// <param name="parent"><para>父节点。</para>The parent node.</param>
    /// <param name="fullPath"><para>目录完整路径。</para>The directory full path.</param>
    private void PopulateDirectory(ProjectTreeItem parent, string fullPath)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullPath))
            {
                var node = new ProjectTreeItem(Path.GetFileName(dir), dir, true);
                PopulateDirectory(node, dir);
                parent.Children.Add(node);
            }

            foreach (var file in Directory.EnumerateFiles(fullPath))
            {
                parent.Children.Add(new ProjectTreeItem(Path.GetFileName(file), file, false));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "ProjectExplorer",
                $"Failed to read directory {fullPath}: {ex.Message}");
        }
    }
}

/// <summary>
/// <para>工程树条目模型。</para>
/// Project tree item model.
/// </summary>
public class ProjectTreeItem(string name, string fullPath, bool isDirectory) : ViewModelBase
{
    /// <summary>显示名称。 Display name.</summary>
    public string Name { get; } = name;

    /// <summary>完整路径。 Full path.</summary>
    public string FullPath { get; } = fullPath;

    /// <summary>是否为目录。 Whether this is a directory.</summary>
    public bool IsDirectory { get; } = isDirectory;

    /// <summary>子节点集合。 Child nodes.</summary>
    public ObservableCollection<ProjectTreeItem> Children { get; } = [];
}