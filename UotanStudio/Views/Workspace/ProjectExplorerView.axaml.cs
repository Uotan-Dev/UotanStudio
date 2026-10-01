using System.Diagnostics;
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

        if (node.IsDirectory && node.IsProjectFolder)
        {
            OpenProjectFileForFolder(node);
            return;
        }

        if (!node.IsDirectory)
        {

            if (string.Equals(System.IO.Path.GetExtension(node.Path), ".uotannode", StringComparison.OrdinalIgnoreCase))
                WeakReferenceMessenger.Default.Send(new OpenNodeProjectRequestMessage(
                    System.IO.Path.GetFileNameWithoutExtension(node.Path)));
            else
                WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.Path));
        }
    }

    /// <summary>
    /// <para>双击项目文件夹时查找并打开其项目配置文件：子项目目录打开 .uotanfw，顶层目录打开 .uotan。</para>
    /// Finds and opens the project config file when a project folder is double-clicked: a sub-project
    /// directory opens its .uotanfw, the root directory opens its .uotan.
    /// </summary>
    private static void OpenProjectFileForFolder(ExplorerNode node)
    {

        if (System.IO.Directory.Exists(node.Path))
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(node.Path, "*.uotanfw"))
            {
                WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(f));
                return;
            }
        }

        foreach (var f in System.IO.Directory.EnumerateFiles(node.Path, "*.uotan"))
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(f));
            return;
        }

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

    private void OnOpenWith(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: EditorOpenMode mode } item)
        {
            var target = item.DataContext as ExplorerNode ?? ProjectTree.SelectedItem as ExplorerNode;
            if (target is { IsDirectory: false, IsPlaceholder: false })
            {
                WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(target.Path, mode));
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// <para>稀疏转换：原始镜像与稀疏镜像之间的双向转换（需求上两项合一，方向由源文件类型决定）。</para>
    /// Sparse conversion: converts between raw and sparse images in either direction; the source file type
    /// decides the direction, so the two former entries are merged into one.
    /// </summary>
    private void OnConvertSparse(object? sender, RoutedEventArgs e)
        => _ = ConvertSelectedImageAsync(ImageViewModel.ConvertSparse);

    private void OnConvertImgToDat(object? sender, RoutedEventArgs e)
        => _ = ConvertSelectedImageAsync(ImageViewModel.ConvertImgToDat);

    private void OnConvertImgToBr(object? sender, RoutedEventArgs e)
        => _ = ConvertSelectedImageAsync(ImageViewModel.ConvertImgToBr);

    /// <summary>
    /// <para>弹出确认对话框展示操作内容与预期输出文件，确认后执行格式转换并把进度上报到镜像管理面板。</para>
    /// Shows a confirmation dialog with the operation and expected output files, then runs the conversion
    /// whose progress is reported to the image manager panel.
    /// </summary>
    /// <param name="direction"><para>转换方向键（见 <see cref="ImageViewModel"/>）。</para>Conversion direction key (see <see cref="ImageViewModel"/>).</param>
    private async Task ConvertSelectedImageAsync(string direction)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode { IsDirectory: false, IsPlaceholder: false } node
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var language = LanguageService.Instance;
        try
        {
            var preview = ImageViewModel.Instance.GetFormatConversionPreview(node.Path, direction);
            var message = string.Format(language.GetString("Image_ConvertConfirm"),
                preview.Operation, string.Join("\n", preview.OutputPaths));
            if (!await DialogService.ConfirmAsync(owner, language.GetString("Image_ConvertTitle"), message))
            {
                return;
            }

            await ImageViewModel.Instance.RunExplorerFormatConversionAsync(node.Path, direction);
            await DialogService.ShowAsync(owner, language.GetString("Image_ConvertTitle"),
                ImageViewModel.Instance.StatusText, Ursa.Controls.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", ex.Message);
            await DialogService.ShowAsync(owner, language.GetString("Image_ConvertTitle"), global::UotanStudio.Services.UserError.Describe(ex),
                Ursa.Controls.MessageBoxIcon.Error);
        }
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

        MenuViewModel.ShowFileChecksumWindow(node.Path);
    }

    private void OnNewEmptySubProject(object? sender, RoutedEventArgs e)
    {

        WeakReferenceMessenger.Default.Send(new AddSubProjectRequestMessage());
    }

    private void OnImportExistingSubProject(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && node.IsDirectory)
        {
            _ = ProjectOperationsService.Instance.ImportSubProjectAsync(node.Path);
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
            await clipboard.FlushAsync();
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

    /// <summary>
    /// <para>在多列文件浏览器（原对比文档）中打开选中的文件或目录：作为文件列载入，不自动开始对比。</para>
    /// Opens the selected file or folder in the multi-column file browser (the former compare document): it loads as a file column
    /// without starting a comparison.
    /// </summary>
    private void OnOpenInCompareView(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsPlaceholder) return;
        WeakReferenceMessenger.Default.Send(new OpenCompareRequestMessage { Path = node.Path });
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
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dialog_RenameFailed"), global::UotanStudio.Services.UserError.Describe(ex), MessageBoxIcon.Error);
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
        try
        {

            var subProject = FindSubProject(node);
            if (subProject is not null)
            {
                await ProjectOperationsService.Instance.RemoveSubProjectAsync(subProject.Name);
            }
            if (node.IsDirectory) Directory.Delete(node.Path, true); else File.Delete(node.Path);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", $"Delete failed: {ex.Message}");
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dialog_DeleteFailed"), global::UotanStudio.Services.UserError.Describe(ex), MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// <para>若节点是当前解决方案注册的子项目根目录，返回其上下文；否则返回 <c>null</c>。</para>
    /// Returns the sub-project context when the node is a registered sub-project root of the open
    /// solution; otherwise <c>null</c>.
    /// </summary>
    private static SubProjectContext? FindSubProject(ExplorerNode node)
    {
        if (!node.IsDirectory) return null;
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return context.SubProjects.FirstOrDefault(sp => string.Equals(
            Path.TrimEndingDirectorySeparator(sp.RootPath),
            Path.TrimEndingDirectorySeparator(node.Path), comparison));
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
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Menu_Paste"), global::UotanStudio.Services.UserError.Describe(ex), MessageBoxIcon.Error);
        }
    }

    private async void OnUnpack(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ExplorerNode node || node.IsDirectory || node.IsPlaceholder) return;
        await ImageViewModel.Instance.RunExplorerOperationAsync(node.Path, false);
    }

}

internal static class ExplorerClipboard
{
    public static List<string> Paths { get; } = new();
    public static bool IsCutMode { get; set; }
}