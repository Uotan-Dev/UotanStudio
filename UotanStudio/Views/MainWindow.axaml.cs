using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Areas.ProjectExplorer;
using UotanStudio.Areas.Workspace;
using UotanStudio.Messages;
using UotanStudio.Services;

namespace UotanStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // 在 InitializeComponent 之前设置 DataContext，使子控件绑定尽早可用；
        // 工作区 DockControl 的布局由 WorkspaceView 自行在模板实例化前提供（见其构造函数）。
        // Set the DataContext before InitializeComponent so child bindings resolve early;
        // the workspace DockControl's layout is provided by WorkspaceView itself before
        // its template instantiates (see its constructor).
        DataContext = new ViewModels.MainViewModel();
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // 忽略清理过程中的任何错误，以防止应用程序崩溃
        }
    }

    /// <summary>导航到设备管理标签页（索引 0）。</summary>
    private void OnDeviceManagerClick(object? sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new NavigateTabMessage(0));

    /// <summary>导航到设置标签页（索引 4）。</summary>
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new NavigateTabMessage(4));

    /// <summary>重新加载所有语言包。</summary>
    private void OnReloadLanguagePacksClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            LanguageService.Instance.Initialize();
        }
        catch
        {
            // 忽略重载过程中的错误
        }
    }

    /// <summary>清空节点编辑器画布（导航到节点标签页并发送清空消息）。</summary>
    private void OnClearCanvasClick(object? sender, RoutedEventArgs e)
    {
        // 导航到节点编辑器标签页（索引 2），由 MainView 中的接收器触发 ClearAllCommand
        WeakReferenceMessenger.Default.Send(new NavigateTabMessage(2));
        WeakReferenceMessenger.Default.Send(new ClearCanvasMessage());
    }

    /// <summary>
    /// 处理「导入镜像」：多选固件镜像文件并导入当前工程。
    /// <para>Handles "import images": multi-selects firmware image files and imports them
    /// into the current project.</para>
    /// </summary>
    private async void OnImportImagesClick(object? sender, RoutedEventArgs e)
    {
        var vm = ProjectExplorerViewModel.Instance;
        if (vm is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
    /// 处理「导出镜像」：选择目标目录并将当前工程部件镜像导出。
    /// <para>Handles "export images": picks a target directory and exports the current
    /// project's component images.</para>
    /// </summary>
    private async void OnExportImagesClick(object? sender, RoutedEventArgs e)
    {
        var vm = ProjectExplorerViewModel.Instance;
        if (vm is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export Firmware Images",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        var directory = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(directory))
        {
            await vm.ExportImagesAsync(directory);
        }
    }

    /// <summary>
    /// 展开「窗口」菜单时动态构建其子项：工具窗口开关、工程部件与打开的文档。
    /// <para>Builds the "Window" menu children when it expands: tool-window toggles, project
    /// components and open documents.</para>
    /// </summary>
    private void OnWindowMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || DataContext is not ViewModels.MainViewModel vm)
        {
            return;
        }

        var win = vm.WindowManagerVM;
        var items = new List<object>();

        // 工具窗口：可勾选开关。
        // Tool windows: checkable toggles.
        foreach (var id in win.ToolWindowIds)
        {
            var toggle = new MenuItem
            {
                Header = win.ToolTitle(id),
                IsChecked = win.IsToolShown(id)
            };
            var toggled = id;
            toggle.Click += (_, _) =>
            {
                toggle.IsChecked = !toggle.IsChecked;
                win.SetToolVisible(toggled, toggle.IsChecked);
            };
            items.Add(toggle);
        }

        // 工程部件窗口（打开工程后显示）。
        // Project component windows (shown once a project is open).
        if (win.ComponentPaths.Count > 0)
        {
            items.Add(new Separator());
            var components = new MenuItem
            {
                Header = LanguageService.Instance.GetString("WinMgr_Components")
            };
            var componentChildren = new List<object>();
            foreach (var path in win.ComponentPaths)
            {
                var child = new MenuItem { Header = Path.GetFileName(path) };
                var componentPath = path;
                child.Click += (_, _) => win.OpenComponent(componentPath);
                componentChildren.Add(child);
            }

            components.ItemsSource = componentChildren;
            items.Add(components);
        }

        // 打开的文档。
        // Open documents.
        if (win.Documents.Count > 0)
        {
            items.Add(new Separator());
            var documents = new MenuItem
            {
                Header = LanguageService.Instance.GetString("WinMgr_Documents")
            };
            var documentChildren = new List<object>();
            foreach (var doc in win.Documents)
            {
                var child = new MenuItem { Header = doc.Title };
                var docPath = doc.Path;
                child.Click += (_, _) => win.ActivateDocument(docPath);
                documentChildren.Add(child);
            }

            documents.ItemsSource = documentChildren;
            items.Add(documents);
        }

        menu.ItemsSource = items;
    }

    /// <summary>退出应用程序。</summary>
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>新建项目：选择一个目录，以目录名创建并保存工程，随后切换到工作区标签页。</summary>
    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var vm = ProjectExplorerViewModel.Instance;
        if (vm is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Project Folder",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        var directory = folders[0].TryGetLocalPath();
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
        WeakReferenceMessenger.Default.Send(new NavigateTabMessage(1));
    }

    /// <summary>打开项目：选择一个 .uotproj 文件并打开，随后切换到工作区标签页。</summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var vm = ProjectExplorerViewModel.Instance;
        if (vm is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await vm.OpenProjectAsync(path);
        WeakReferenceMessenger.Default.Send(new NavigateTabMessage(1));
    }

    /// <summary>保存项目：将当前打开的工程写入磁盘。</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var vm = ProjectExplorerViewModel.Instance;
        if (vm is not null)
        {
            await vm.SaveAsync();
        }
    }

    /// <summary>显示关于对话框。</summary>
    private async void OnAboutClick(object? sender, RoutedEventArgs e) => await ShowAboutDialog();

    private async System.Threading.Tasks.Task ShowAboutDialog()
    {
        var title = LanguageService.Instance.GetString("About_Title");
        var description = LanguageService.Instance.GetString("About_Description");
        await ShowInfoDialog(title, $"UotanStudio\nVersion: 1.0.0\n\n{description}");
    }

    private async System.Threading.Tasks.Task ShowInfoDialog(string title, string text)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Text = text,
                Margin = new Thickness(20),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        await dialog.ShowDialog(this);
    }
}