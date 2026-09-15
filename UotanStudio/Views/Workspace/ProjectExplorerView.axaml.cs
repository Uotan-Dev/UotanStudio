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
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using Ursa.Controls;

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

    private void OnOpenAsLog(object? sender, RoutedEventArgs e) => OpenWith(EditorOpenMode.Log);

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

    private void OnOpenAsFileSystem(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.FileSystem);
    }

    private void OnConvertImgToSimg(object? sender, RoutedEventArgs e) => RequestFormatConversion("IMG→SIMG");

    private void OnConvertSimgToImg(object? sender, RoutedEventArgs e) => RequestFormatConversion("SIMG→IMG");

    private void OnConvertImgToDat(object? sender, RoutedEventArgs e) => RequestFormatConversion("IMG→DAT");

    private void OnConvertImgToBr(object? sender, RoutedEventArgs e) => RequestFormatConversion("IMG→BR");

    /// <summary>
    /// <para>记录格式转换请求：按需求本页暂不接入转换后端，只在日志中留下所选文件与转换方向。</para>
    /// Records a format-conversion request: the conversion backend is intentionally not wired up yet (per
    /// request), so the selected file and direction are only written to the log.
    /// </summary>
    /// <param name="direction"><para>转换方向文本，如 <c>IMG→SIMG</c>。</para>The direction text, e.g. <c>IMG→SIMG</c>.</param>
    private void RequestFormatConversion(string direction)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode { IsDirectory: false, IsPlaceholder: false } node)
        {
            return;
        }

        LogService.Instance.Write(LogLevel.Info, "Explorer",
            $"Format conversion requested ({direction}) for '{node.Path}'; the conversion backend is not wired up yet.");
    }

    /// <summary>
    /// <para>打开校验窗口：复用已打开的实例，否则新建并作为主窗口的附属窗口显示。</para>
    /// Opens the checksum window: reuses the existing instance, otherwise creates one owned by the main window.
    /// </summary>
    private void OnChecksum(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode { IsDirectory: false, IsPlaceholder: false } node)
        {
            return;
        }

        // 与「工具 → 文件校验器」共用同一个窗口实例，右键只切换目标文件。
        // Shares one window instance with Tools -> File Checksum; the context menu just switches the file.
        MenuViewModel.ShowFileChecksumWindow(node.Path);
    }

    private void OnNewEmptySubProject(object? sender, RoutedEventArgs e)
    {
        // 右键「新建子项目」与顶部菜单共用同一表单流程（子项目模式的新建窗口）。
        // The explorer "new sub-project" entry shares the form flow with the File menu.
        WeakReferenceMessenger.Default.Send(new AddSubProjectRequestMessage());
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

    private Window? GetOwnerWindow() =>
        TopLevel.GetTopLevel(this) as Window ??
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
        var newName = await DialogService.InputAsync(owner, LanguageService.Instance.GetString("Dialog_Rename"), oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;
        var newPath = Path.Combine(Path.GetDirectoryName(node.Path) ?? "", newName);
        try { if (node.IsDirectory) Directory.Move(node.Path, newPath); else File.Move(node.Path, newPath); }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", $"Rename failed: {ex.Message}");
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dialog_RenameFailed"), ex.Message, MessageBoxIcon.Error);
        }
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
        if (!await DialogService.ConfirmAsync(owner, LanguageService.Instance.GetString("Dialog_ConfirmDelete"),
                string.Format(LanguageService.Instance.GetString("Dialog_ConfirmDeleteMessage"), name),
                LanguageService.Instance.GetString("Menu_Delete"))) return;
        try { if (node.IsDirectory) Directory.Delete(node.Path, true); else File.Delete(node.Path); }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", $"Delete failed: {ex.Message}");
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dialog_DeleteFailed"), ex.Message, MessageBoxIcon.Error);
        }
    }

    private async void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        var owner = GetOwnerWindow(); if (owner is null) return;
        if (await ExplorerItemCreationService.CreateAsync(owner, node.Path, isDirectory: false) is not null)
            node.RefreshChildren();
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        var owner = GetOwnerWindow(); if (owner is null) return;
        if (await ExplorerItemCreationService.CreateAsync(owner, node.Path, isDirectory: true) is not null)
            node.RefreshChildren();
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        var owner = GetOwnerWindow();
        if (owner is null) return;
        var destination = node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path);
        if (destination is null) return;
        var sources = ExplorerClipboard.Paths.ToArray();
        var move = ExplorerClipboard.IsCutMode;
        try
        {
            foreach (var source in sources)
            {
                await Task.Run(() => ExplorerFileOperations.Transfer(source, destination, move));
                if (move && ExplorerClipboard.IsCutMode) ExplorerClipboard.Paths.Remove(source);
            }
            if (node.IsDirectory) node.RefreshChildren();
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", ex.Message);
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Menu_Paste"), ex.Message, MessageBoxIcon.Error);
        }
    }

    private async void OnUnpack(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsDirectory || node.IsPlaceholder) return;
        await ImageViewModel.Instance.RunExplorerOperationAsync(node.Path, false);
    }

    private async void OnPackRom(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || !node.IsDirectory || node.IsPlaceholder) return;
        await ImageViewModel.Instance.RunExplorerOperationAsync(node.Path, true);
    }
}

internal static class ExplorerClipboard
{
    public static List<string> Paths { get; } = new();
    public static bool IsCutMode { get; set; }
}