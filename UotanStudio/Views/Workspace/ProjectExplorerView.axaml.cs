using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>项目资源管理器视图，处理目录展开加载、文件双击打开与右键打开方式。</para>
/// Project explorer view, handling directory expansion loading, double-click opening and the
/// right-click open-with menu for file nodes.
/// </summary>
public partial class ProjectExplorerView : UserControl
{
    /// <summary>
    /// <para>创建项目资源管理器视图，订阅树节点的展开事件以触发目录惰性加载。</para>
    /// Creates the project explorer view, subscribing to tree-item expansion events to trigger lazy directory loading.
    /// </summary>
    public ProjectExplorerView()
    {
        InitializeComponent();
        ProjectTree.AddHandler(TreeViewItem.ExpandedEvent, OnItemExpanded);
        ProjectTree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, handledEventsToo: true);
        ProjectTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Control { DataContext: ExplorerNode node })
        {
            node.EnsureChildrenLoaded();
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control { DataContext: ExplorerNode node } || node.IsPlaceholder)
        {
            return;
        }

        if (node.RepackConfigurationPath is { } configurationPath)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(
                configurationPath, EditorOpenMode.Text));
            e.Handled = true;
            return;
        }

        // 项目文件夹（子项目 / 节点项目）：双击打开关联的项目配置文件。
        // Project folders (sub-project / node project): double-click opens the associated config file.
        if (node.IsDirectory && node.IsProjectFolder)
        {
            OpenProjectFileForFolder(node);
            return;
        }

        if (!node.IsDirectory)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.Path));
        }
    }

    /// <summary>
    /// <para>双击项目文件夹时查找并打开关联的项目配置文件：顶层目录打开 .uotan，子项目目录打开 .uotanfw，节点项目打开 .uotannode。</para>
    /// Finds and opens the associated project config file when a project folder is double-clicked:
    /// root directory opens .uotan, sub-project directory opens .uotanfw, node project opens .uotannode.
    /// </summary>
    private static void OpenProjectFileForFolder(ExplorerNode node)
    {
        // 有关联项目路径的节点（如节点项目文件夹）：直接打开。
        // Nodes with an associated project path (e.g. node project folder): open directly.
        if (node.AssociatedProjectPath is not null && System.IO.File.Exists(node.AssociatedProjectPath))
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.AssociatedProjectPath));
            return;
        }

        // 子项目文件夹：在目录内查找 .uotanfw 文件。
        // Sub-project folder: find the .uotanfw file inside the directory.
        if (System.IO.Directory.Exists(node.Path))
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(node.Path, "*.uotanfw"))
            {
                WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(f));
                return;
            }
        }

        // 顶层解决方案文件夹：在父目录中查找 .uotan 文件。
        // Top-level solution folder: find the .uotan file in the parent directory.
        var parentDir = System.IO.Path.GetDirectoryName(node.Path);
        if (parentDir is not null)
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(parentDir, "*.uotan"))
            {
                if (System.IO.File.Exists(f))
                {
                    WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(f));
                    return;
                }
            }
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ProjectTree).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed &&
            e.Source is Control { DataContext: ExplorerNode node })
        {
            ProjectTree.SelectedItem = node;
        }
    }

    private void OnOpenAsText(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Text);
    }

    private void OnStartCompare(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ExplorerNode { IsPlaceholder: false } node })
        {
            ProjectTree.SelectedItem = node;
            WeakReferenceMessenger.Default.Send(new OpenCompareRequestMessage { Path = node.Path });
        }
    }

    private void OnOpenAsHex(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Hex);
    }

    private void OnOpenAsDtb(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Dtb);
    }

    private void OnOpenAsDex(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Dex);
    }

    private void OnOpenAsAxml(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Axml);
    }

    private void OnOpenAsArsc(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Arsc);
    }

    private void OnOpenAsApk(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Apk);
    }

    private void OnOpenAsFirmware(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Firmware);
    }

    private void OnNewEmptySubProject(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && node.IsDirectory)
        {
            var dirName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(node.Path));
            if (!string.IsNullOrEmpty(dirName))
            {
                var fwPath = System.IO.Path.Combine(node.Path, dirName + ".uotanfw");
                _ = ProjectOperationsService.Instance.AddSubProjectAsync(fwPath);
            }
        }
    }

    private void OnImportExistingSubProject(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && node.IsDirectory)
        {
            _ = ProjectOperationsService.Instance.ImportSubProjectAsync(node.Path);
        }
    }

    private void OpenWith(EditorOpenMode mode)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && !node.IsDirectory && !node.IsPlaceholder)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.Path, mode));
        }
    }

    private static async Task<string?> ShowInputDialogAsync(Window owner, string title, string defaultText = "")
    {
        var win = new Window { Title = title, Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var panel = new StackPanel { Margin = new Thickness(8) };
        var tb = new TextBox { Text = defaultText };
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(tb);
        panel.Children.Add(ok);
        win.Content = panel;
        tb.SelectAll();
        ok.Click += (_, _) => win.Close(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) win.Close(tb.Text); };
        return await win.ShowDialog<string?>(owner);
    }

    private static async Task<bool> ShowConfirmDialogAsync(Window owner, string title, string message)
    {
        var win = new Window { Title = title, Width = 350, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var panel = new StackPanel { Margin = new Thickness(8) };
        var msg = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var yesBtn = new Button { Content = "Yes", Margin = new Thickness(0, 0, 8, 0) };
        var noBtn = new Button { Content = "No" };
        btnPanel.Children.Add(yesBtn);
        btnPanel.Children.Add(noBtn);
        panel.Children.Add(msg);
        panel.Children.Add(btnPanel);
        win.Content = panel;
        yesBtn.Click += (_, _) => win.Close(true);
        noBtn.Click += (_, _) => win.Close(false);
        return await win.ShowDialog<bool>(owner);
    }

    private Window? GetOwnerWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private async void OnCopyFullPath(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(node.Path);
        }
    }

    private void OnOpenInFileExplorer(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        try
        {
            var target = node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path);
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Explorer", $"Open in explorer failed: {ex.Message}"); }
    }

    private void OnOpenInTerminal(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        try { Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{node.Path}\"", UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/k cd /d \"{node.Path}\"", UseShellExecute = true }); }
            catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Explorer", $"Open terminal failed: {ex.Message}"); }
        }
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        var owner = GetOwnerWindow(); if (owner is null) return;
        var oldName = node.IsDirectory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(node.Path)) : Path.GetFileName(node.Path);
        var newName = await ShowInputDialogAsync(owner, LanguageService.Instance.GetString("Dialog_Rename"), oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;
        var newPath = Path.Combine(Path.GetDirectoryName(node.Path) ?? "", newName);
        try { if (node.IsDirectory) Directory.Move(node.Path, newPath); else File.Move(node.Path, newPath); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Explorer", $"Rename failed: {ex.Message}"); }
    }

    private void OnCut(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        ExplorerClipboard.Paths.Clear(); ExplorerClipboard.Paths.Add(node.Path); ExplorerClipboard.IsCutMode = true;
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        ExplorerClipboard.Paths.Clear(); ExplorerClipboard.Paths.Add(node.Path); ExplorerClipboard.IsCutMode = false;
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        var owner = GetOwnerWindow(); if (owner is null) return;
        _ = DeleteAsync(node, owner);
    }

    private async Task DeleteAsync(ExplorerNode node, Window owner)
    {
        var name = node.IsDirectory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(node.Path)) : Path.GetFileName(node.Path);
        if (!await ShowConfirmDialogAsync(owner, LanguageService.Instance.GetString("Dialog_ConfirmDelete"), string.Format(LanguageService.Instance.GetString("Dialog_ConfirmDeleteMessage"), name))) return;
        try { if (node.IsDirectory) Directory.Delete(node.Path, true); else File.Delete(node.Path); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Explorer", $"Delete failed: {ex.Message}"); }
    }

    private async void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        var owner = GetOwnerWindow(); if (owner is null) return;
        var name = await ShowInputDialogAsync(owner, LanguageService.Instance.GetString("Dialog_NewFile"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try { File.Create(Path.Combine(node.Path, name)).Dispose(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Explorer", $"Create file failed: {ex.Message}"); }
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        var owner = GetOwnerWindow(); if (owner is null) return;
        var name = await ShowInputDialogAsync(owner, LanguageService.Instance.GetString("Dialog_NewFolder"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try { Directory.CreateDirectory(Path.Combine(node.Path, name)); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Explorer", $"Create folder failed: {ex.Message}"); }
    }

    private void OnUnpack(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsDirectory || node.IsPlaceholder) return;
        LogService.Instance.Write(LogLevel.Info, "Explorer", $"Unpack requested for: {node.Path}");
    }

    private void OnPackRom(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        LogService.Instance.Write(LogLevel.Info, "Explorer", $"Pack ROM requested for: {node.Path}");
    }
}

internal static class ExplorerClipboard
{
    public static List<string> Paths { get; } = new();
    public static bool IsCutMode { get; set; }
}
