using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    /// <summary>
    /// Handles the import-images button: lets the user multi-select firmware image files and
    /// imports them into the current project.
    /// <para>处理导入镜像按钮：允许用户多选固件镜像文件并导入当前工程。</para>
    /// </summary>
    private async void OnImportImagesClick(object? sender, RoutedEventArgs e)
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
            Title = "Import Firmware Images",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Firmware Images")
                {
                    Patterns = ["*.img", "*.bin", "*.payload", "*.cpio", "*.tar", "*.ofp", "*.ops", "*.ozip"]
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

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();
        if (paths.Count > 0)
        {
            await vm.ImportImagesAsync(paths);
        }
    }

    /// <summary>
    /// Handles a node expansion: lazy-loads structure children for firmware component files.
    /// <para>处理节点展开：为固件部件文件懒加载结构子节点。</para>
    /// </summary>
    private async void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: ProjectTreeItem item })
        {
            return;
        }

        if (!item.IsDirectory && item.IsExpandable)
        {
            await item.LoadStructureAsync();
        }
    }

    /// <summary>
    /// Handles double-tap on a node: opens file items in the editor.
    /// <para>处理节点双击：在编辑器中打开文件条目。</para>
    /// </summary>
    private void OnTreeItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        if (sender is TreeViewItem { DataContext: ProjectTreeItem item })
        {
            vm.OpenItem(item);
        }
    }

    /// <summary>
    /// Handles the context-menu "open" action.
    /// <para>处理右键菜单「打开」。</para>
    /// </summary>
    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        if (sender is MenuItem { DataContext: ProjectTreeItem item })
        {
            vm.OpenItem(item);
        }
    }

    /// <summary>
    /// Handles the context-menu "unpack" action.
    /// <para>处理右键菜单「解包到工程目录」。</para>
    /// </summary>
    private void OnUnpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        if (sender is MenuItem { DataContext: ProjectTreeItem item })
        {
            vm.UnpackItem(item);
        }
    }

    /// <summary>
    /// Handles the context-menu "add to project components" action.
    /// <para>处理右键菜单「添加到工程部件」。</para>
    /// </summary>
    private async void OnAddComponentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        if (sender is MenuItem { DataContext: ProjectTreeItem item })
        {
            await vm.AddToComponentsAsync(item);
        }
    }

    /// <summary>
    /// Handles the context-menu "rename" action: prompts for a new name and renames the file.
    /// <para>处理右键菜单「重命名」：提示输入新名称并重命名文件。</para>
    /// </summary>
    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectExplorerViewModel vm)
        {
            return;
        }

        if (sender is not MenuItem { DataContext: ProjectTreeItem item })
        {
            return;
        }

        var newName = await PromptForRenameAsync(item);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            await vm.RenameItemAsync(item, newName);
        }
    }

    /// <summary>
    /// Shows a small rename dialog and returns the entered name, or null when cancelled.
    /// <para>显示一个重命名对话框并返回输入的名称；取消时返回 null。</para>
    /// </summary>
    /// <param name="item"><para>要重命名的条目。</para>The item to rename.</param>
    private async Task<string?> PromptForRenameAsync(ProjectTreeItem item)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner)
        {
            return null;
        }

        var input = new TextBox
        {
            Text = item.Name,
            Margin = new Avalonia.Thickness(20, 16, 20, 8),
            PlaceholderText = "New name"
        };

        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true,
            Margin = new Avalonia.Thickness(8, 0, 0, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 0, 20, 12),
            Children = { okButton, cancelButton }
        };

        var dialog = new Window
        {
            Title = "Rename",
            Width = 360,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Children = { input, buttons }
            }
        };

        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        okButton.Click += (_, _) =>
        {
            var name = input.Text?.Trim();
            result.TrySetResult(string.IsNullOrWhiteSpace(name) ? null : name);
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            result.TrySetResult(null);
            dialog.Close();
        };
        dialog.Closed += (_, _) => result.TrySetResult(null);

        await dialog.ShowDialog(owner);
        return await result.Task;
    }
}