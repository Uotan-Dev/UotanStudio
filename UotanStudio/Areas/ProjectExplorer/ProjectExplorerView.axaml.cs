using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace UotanStudio.Areas.ProjectExplorer;

/// <summary>
/// Project explorer view: create/open a firmware project and browse its tree.
/// <para>项目资源管理器视图：新建/打开固件工程并浏览其工程树。</para>
/// </summary>
public partial class ProjectExplorerView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectExplorerView"/> class.
    /// <para>初始化 <see cref="ProjectExplorerView"/> 类的新实例。</para>
    /// </summary>
    public ProjectExplorerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Handles the create-project button: picks a directory, then creates and saves the project
    /// named after the folder.
    /// <para>处理新建工程按钮：选择目录，随后以目录名创建并保存工程。</para>
    /// </summary>
    private async void OnCreateProjectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var dir = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Project Folder",
            AllowMultiple = false
        });
        if (dir.Count == 0)
        {
            return;
        }

        var directory = dir[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "UntitledFirmware";
        }

        await vm.CreateProjectAsync(directory, name);
    }

    /// <summary>
    /// Handles the open-project button: picks a .uotproj file and opens it.
    /// <para>处理打开工程按钮：选择 .uotproj 文件并打开。</para>
    /// </summary>
    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Firmware Project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("UotanStudio Project")
                {
                    Patterns = ["*.uotproj"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await vm.OpenProjectAsync(path);
        }
    }
}