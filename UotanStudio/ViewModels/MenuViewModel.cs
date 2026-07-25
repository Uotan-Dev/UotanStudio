using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Linq;
using System.Threading.Tasks;
using UotanStudio.Services;
using UotanStudio.Views;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>顶部菜单栏视图模型，提供文件、编辑、视图、工具与帮助菜单的所有命令实现。</para>
/// Top menu bar view model, providing command implementations for File, Edit, View, Tools, and Help menus.
/// </summary>
public partial class MenuViewModel : ViewModelBase
{
    private static TopLevel? TopLevel => TopLevelExtensions.GetTopLevel();

    /// <summary>
    /// <para>创建菜单视图模型。</para>
    /// Creates the menu view model.
    /// </summary>
    public MenuViewModel()
    {
    }

    // ===== 文件 / File =====

    /// <summary>
    /// <para>创建一个新项目，弹出文件保存对话框选择项目路径。</para>
    /// Creates a new project by showing a file-save dialog to pick the project path.
    /// </summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        var path = await PickProjectFileAsync(isSave: true);
        if (path != null)
        {
            _currentProjectPath = path;
            // 新工程：清空画布后立即保存空骨架
            if (NodeEditorVM != null)
            {
                NodeEditorVM.Clear();
                await PersistToAsync(path);
            }
        }
    }

    /// <summary>
    /// <para>打开现有项目，弹出文件打开对话框选择 .uotan 工程文件并重建画布。</para>
    /// Opens an existing project by showing a file-open dialog to pick a .uotan project file and rebuild the canvas.
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var path = await PickProjectFileAsync(isSave: false);
        if (path == null) return;
        _currentProjectPath = path;

        var snapshot = await Services.WorkflowPersistenceService.LoadAsync(path).ConfigureAwait(false);
        if (snapshot == null) return;

        if (NodeEditorVM != null)
        {
            await NodeEditorVM.LoadFromAsync(snapshot).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>当前工程文件路径，由保存/打开操作填充。</para>
    /// Current project file path, populated by save/open operations.
    /// </summary>
    private string? _currentProjectPath;

    /// <summary>
    /// <para>当前节点编辑器视图模型引用，由外部设置以便持久化访问画布。</para>
    /// Current node editor view model reference, set externally so persistence can access the canvas.
    /// </summary>
    public NodeEditorViewModel? NodeEditorVM { get; set; }

    /// <summary>
    /// <para>保存当前项目，若无路径则触发另存流程。</para>
    /// Saves the current project, triggering Save As if there is no project path yet.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(_currentProjectPath))
        {
            await SaveAsAsync();
            return;
        }
        await PersistToAsync(_currentProjectPath);
    }

    /// <summary>
    /// <para>另存为，弹出文件保存对话框选择新项目路径。</para>
    /// Saves the project under a new path, showing a file-save dialog.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var path = await PickProjectFileAsync(isSave: true);
        if (path == null) return;
        _currentProjectPath = path;
        await PersistToAsync(path);
    }

    /// <summary>
    /// <para>将当前画布快照写入指定路径。</para>
    /// Writes the current canvas snapshot to the specified path.
    /// </summary>
    /// <param name="path">
    /// <para>目标文件路径。</para>
    /// Target file path.
    /// </param>
    private async Task PersistToAsync(string path)
    {
        if (NodeEditorVM == null)
        {
            Services.LogService.Instance.Write(Abstractions.Logging.LogLevel.Warn, "Menu", "Save aborted: no node editor bound");
            return;
        }
        await Services.WorkflowPersistenceService.SaveAsync(
            path,
            NodeEditorVM.Nodes.ToList(),
            NodeEditorVM.Connections.ToList()).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>导出当前工作区内容，弹出文件保存对话框选择导出路径。</para>
    /// Exports the current workspace contents, showing a file-save dialog to pick the export path.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var topLevel = TopLevel;
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Menu_Export"),
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });

        _ = files?.Path;
    }

    /// <summary>
    /// <para>退出应用程序，发送退出消息由 App 处理。</para>
    /// Exits the application by sending an exit message handled by App.
    /// </summary>
    [RelayCommand]
    private void Exit()
    {
        WeakReferenceMessenger.Default.Send(new ExitApplicationMessage());
    }

    // ===== 编辑 / Edit =====

    /// <summary><para>撤销操作，转发到当前活动视图模型。</para>Undo operation, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void Undo()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Undo));
    }

    /// <summary><para>重做操作，转发到当前活动视图模型。</para>Redo operation, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void Redo()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Redo));
    }

    /// <summary><para>剪切选中内容，转发到当前活动视图模型。</para>Cut selection, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void Cut()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Cut));
    }

    /// <summary><para>复制选中内容，转发到当前活动视图模型。</para>Copy selection, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void Copy()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Copy));
    }

    /// <summary><para>粘贴剪贴板内容，转发到当前活动视图模型。</para>Paste clipboard contents, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void Paste()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Paste));
    }

    /// <summary><para>删除选中内容，转发到当前活动视图模型。</para>Delete selection, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void Delete()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Delete));
    }

    /// <summary><para>全选，转发到当前活动视图模型。</para>Select all, forwarded to the currently active view model.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.SelectAll));
    }

    // ===== 视图 / View =====

    /// <summary><para>切换到工作区选项卡。</para>Switch to the Workspace tab.</summary>
    [RelayCommand]
    private void GoToWorkspace()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
    }

    /// <summary><para>切换到节点编辑器选项卡。</para>Switch to the Node Editor tab.</summary>
    [RelayCommand]
    private void GoToNodeEditor()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
    }

    /// <summary><para>切换到设置选项卡。</para>Switch to the Settings tab.</summary>
    [RelayCommand]
    private void GoToSettings()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Settings));
    }

    /// <summary>
    /// <para>切换节点编辑器的小地图显示状态。</para>
    /// Toggles the minimap display state of the node editor.
    /// </summary>
    [RelayCommand]
    private void ToggleMinimap()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ToggleMinimap));
    }

    /// <summary><para>放大节点画布。</para>Zoom in the node canvas.</summary>
    [RelayCommand]
    private void ZoomIn()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ZoomIn));
    }

    /// <summary><para>缩小节点画布。</para>Zoom out the node canvas.</summary>
    [RelayCommand]
    private void ZoomOut()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ZoomOut));
    }

    /// <summary><para>重置节点画布缩放。</para>Reset the node canvas zoom.</summary>
    [RelayCommand]
    private void ResetZoom()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ResetZoom));
    }

    // ===== 工具 / Tools =====

    /// <summary><para>打开 ADB 终端，弹出设备管理器对话框。</para>Open the ADB shell console, popping up the Device Manager dialog.</summary>
    [RelayCommand]
    private void AdbShell()
    {
        OpenDialog<DeviceManagerDialog>();
        Services.LogService.Instance.Write(Abstractions.Logging.LogLevel.Info, "Menu", "ADB shell requested via Device Manager dialog");
    }

    /// <summary><para>打开 Fastboot 控制台，弹出设备管理器对话框。</para>Open the Fastboot console, popping up the Device Manager dialog.</summary>
    [RelayCommand]
    private void FastbootConsole()
    {
        OpenDialog<DeviceManagerDialog>();
        Services.LogService.Instance.Write(Abstractions.Logging.LogLevel.Info, "Menu", "Fastboot console requested via Device Manager dialog");
    }

    /// <summary><para>打开语言管理器，切换到设置选项卡。</para>Open the language manager, switching to the Settings tab.</summary>
    [RelayCommand]
    private void LanguageManager()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Settings));
    }

    /// <summary><para>打开插件管理器，弹出插件管理器对话框。</para>Open the plugin manager, popping up the Plugin Manager dialog.</summary>
    [RelayCommand]
    private void PluginManager()
    {
        OpenDialog<PluginManagerDialog>();
    }

    /// <summary>
    /// <para>以主窗口为 owner 弹出指定类型的独立对话框窗口。</para>
    /// Pops up a standalone dialog window of the specified type, owned by the main window.
    /// </summary>
    /// <typeparam name="TWindow">
    /// <para>对话框窗口类型，必须具有公共无参构造。</para>
    /// Dialog window type, must have a public parameterless constructor.
    /// </typeparam>
    private static void OpenDialog<TWindow>() where TWindow : Window, new()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            var dialog = new TWindow();
            dialog.Show(owner);
        }
    }

    // ===== 帮助 / Help =====

    /// <summary><para>打开在线文档。</para>Open online documentation.</summary>
    [RelayCommand]
    private async Task Documentation()
    {
        await OpenUrlAsync("https://github.com/UotanStudio/UotanStudio/wiki");
    }

    /// <summary><para>打开问题反馈页面。</para>Open the issue reporting page.</summary>
    [RelayCommand]
    private async Task ReportIssue()
    {
        await OpenUrlAsync("https://github.com/UotanStudio/UotanStudio/issues");
    }

    /// <summary>
    /// <para>使用宿主系统浏览器打开指定 URL，失败时写入统一日志。</para>
    /// Opens the specified URL using the host system browser, logging on failure.
    /// </summary>
    private static async Task OpenUrlAsync(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            Services.LogService.Instance.Write(Abstractions.Logging.LogLevel.Error, "Menu", $"Failed to open URL '{url}': {ex.Message}");
        }
    }

    /// <summary>
    /// <para>显示关于对话框，包含版本与简介信息。</para>
    /// Shows the About dialog with version and description info.
    /// </summary>
    [RelayCommand]
    private void About()
    {
        var dialog = new AboutDialog();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            dialog.ShowDialog(window);
        }
        else if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
        {
            dialog.Show();
        }
    }

    // ===== 辅助方法 =====

    /// <summary>
    /// <para>弹出文件选择/保存对话框选取项目文件路径，返回所选路径或 <c>null</c>。</para>
    /// Shows a file open/save dialog to pick a project file path, returning the chosen path or <c>null</c>.
    /// </summary>
    /// <param name="isSave">
    /// <para>是否为保存对话框；false 则为打开对话框。</para>
    /// Whether this is a save dialog; false means open dialog.
    /// </param>
    /// <returns>
    /// <para>所选文件本地路径；取消时返回 <c>null</c>。</para>
    /// The chosen local file path; returns <c>null</c> when cancelled.
    /// </returns>
    private static async Task<string?> PickProjectFileAsync(bool isSave)
    {
        var topLevel = TopLevel;
        if (topLevel == null) return null;

        if (isSave)
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString("Menu_NewProject"),
                DefaultExtension = "uotan",
                FileTypeChoices =
                [
                    new FilePickerFileType("UotanStudio Project") { Patterns = ["*.uotan"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            return file?.Path.LocalPath;
        }
        else
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Menu_OpenProject"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("UotanStudio Project") { Patterns = ["*.uotan"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }
    }
}

/// <summary>
/// <para>主选项卡导航目标枚举，由顶部菜单触发。</para>
/// Main tab navigation target enum, triggered by the top menu.
/// </summary>
public enum NavigationTarget
{
    /// <summary><para>工作区。</para>Workspace.</summary>
    Workspace,
    /// <summary><para>节点编辑器。</para>Node editor.</summary>
    NodeEditor,
    /// <summary><para>设备管理器。</para>Device manager.</summary>
    DeviceManager,
    /// <summary><para>插件管理器。</para>Plugin manager.</summary>
    PluginManager,
    /// <summary><para>设置。</para>Settings.</summary>
    Settings
}

/// <summary>
/// <para>导航到主选项卡的消息，由顶部菜单发送给 MainView。</para>
/// Navigate-to-tab message, sent by the top menu to the MainView.
/// </summary>
public class NavigateToTabMessage
{
    /// <summary><para>获取要导航到的目标。</para>Gets the navigation target.</summary>
    public NavigationTarget Target { get; }

    /// <summary>
    /// <para>创建导航消息。</para>
    /// Creates a navigation message.
    /// </summary>
    /// <param name="target"><para>导航目标。</para>The navigation target.</param>
    public NavigateToTabMessage(NavigationTarget target) => Target = target;
}

/// <summary>
/// <para>退出应用程序消息，由顶部菜单发送给 App。</para>
/// Exit-application message, sent by the top menu to the App.
/// </summary>
public class ExitApplicationMessage;

/// <summary>
/// <para>节点编辑器动作枚举，由顶部视图菜单触发。</para>
/// Node editor action enum, triggered by the top View menu.
/// </summary>
public enum NodeEditorAction
{
    /// <summary><para>切换小地图。</para>Toggle the minimap.</summary>
    ToggleMinimap,
    /// <summary><para>放大。</para>Zoom in.</summary>
    ZoomIn,
    /// <summary><para>缩小。</para>Zoom out.</summary>
    ZoomOut,
    /// <summary><para>重置缩放。</para>Reset zoom.</summary>
    ResetZoom,
    /// <summary><para>撤销。</para>Undo.</summary>
    Undo,
    /// <summary><para>重做。</para>Redo.</summary>
    Redo,
    /// <summary><para>剪切选中。</para>Cut selection.</summary>
    Cut,
    /// <summary><para>复制选中。</para>Copy selection.</summary>
    Copy,
    /// <summary><para>粘贴。</para>Paste.</summary>
    Paste,
    /// <summary><para>删除选中。</para>Delete selection.</summary>
    Delete,
    /// <summary><para>全选。</para>Select all.</summary>
    SelectAll
}

/// <summary>
/// <para>节点编辑器动作消息，由顶部视图菜单发送给 NodeEditorViewModel。</para>
/// Node editor action message, sent by the top View menu to the NodeEditorViewModel.
/// </summary>
public class NodeEditorActionMessage
{
    /// <summary><para>获取节点编辑器动作。</para>Gets the node editor action.</summary>
    public NodeEditorAction Action { get; }

    /// <summary>
    /// <para>创建节点编辑器动作消息。</para>
    /// Creates a node editor action message.
    /// </summary>
    /// <param name="action"><para>动作类型。</para>The action type.</param>
    public NodeEditorActionMessage(NodeEditorAction action) => Action = action;
}
