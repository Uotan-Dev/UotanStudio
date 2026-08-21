using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System;
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
    /// <para>当前激活的底栏主面板；影响视图菜单中节点编辑器相关命令（小地图/缩放）的可用性。</para>
    /// Currently active bottom main panel; gates availability of node-editor-related View menu commands (minimap/zoom).
    /// </summary>
    /// <remarks>
    /// <para>由 <c>MainView</c> 通过 <see cref="ActiveTabChangedMessage"/> 在面板切换时更新；初始为 <see cref="ActiveTab.Workspace"/>（与 MainView 默认 SelectedIndex=3 对应）。</para>
    /// Updated by <c>MainView</c> via <see cref="ActiveTabChangedMessage"/> on tab switch; defaults to <see cref="ActiveTab.Workspace"/> (matching MainView default SelectedIndex=3).
    /// </remarks>
    private ActiveTab _currentTab = ActiveTab.Workspace;

    /// <summary>
    /// <para>创建菜单视图模型，订阅激活面板切换消息以维护 <see cref="_currentTab"/>。</para>
    /// Creates the menu view model, subscribing to active-tab-changed messages to maintain <see cref="_currentTab"/>.
    /// </summary>
    public MenuViewModel()
    {
        WeakReferenceMessenger.Default.Register<ActiveTabChangedMessage>(this, OnActiveTabChanged);
    }

    /// <summary>
    /// <para>响应激活面板切换：更新 <see cref="_currentTab"/> 并通知节点编辑器相关命令重新计算可用性。</para>
    /// Responds to active-tab changes: updates <see cref="_currentTab"/> and notifies node-editor-related commands to re-evaluate their availability.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>包含新激活面板的消息。</para>The message carrying the newly active tab.</param>
    private void OnActiveTabChanged(object recipient, ActiveTabChangedMessage message)
    {
        if (_currentTab == message.Tab) return;
        _currentTab = message.Tab;
        // 通知节点编辑器相关命令重新评估 CanExecute
        ToggleMinimapCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ResetZoomCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// <para>当前是否在节点编辑器面板；仅供 <c>CanExecute</c> 私有方法使用。</para>
    /// Whether the Node Editor panel is currently active; used solely by <c>CanExecute</c> helpers.
    /// </summary>
    private bool IsNodeEditorActive => _currentTab == ActiveTab.NodeEditor;

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

    /// <summary><para>切换到日志面板选项卡。</para>Switch to the global Log panel tab.</summary>
    [RelayCommand]
    private void GoToLog()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Log));
    }

    /// <summary>
    /// <para>切换节点编辑器的小地图显示状态。仅在节点编辑器面板激活时可用。</para>
    /// Toggles the minimap display state of the node editor. Only available when the Node Editor panel is active.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ToggleMinimap()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ToggleMinimap));
    }

    /// <summary><para>放大节点画布。仅在节点编辑器面板激活时可用。</para>Zoom in the node canvas. Only available when the Node Editor panel is active.</summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ZoomIn()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ZoomIn));
    }

    /// <summary><para>缩小节点画布。仅在节点编辑器面板激活时可用。</para>Zoom out the node canvas. Only available when the Node Editor panel is active.</summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ZoomOut()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ZoomOut));
    }

    /// <summary><para>重置节点画布缩放。仅在节点编辑器面板激活时可用。</para>Reset the node canvas zoom. Only available when the Node Editor panel is active.</summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ResetZoom()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ResetZoom));
    }

    // ===== 工具 / Tools =====

    /// <summary>
    /// <para>当前已打开的插件管理器对话框引用，同一时间仅允许一个实例；已关闭时为 null。</para>
    /// Currently open Plugin Manager dialog reference; only one instance is allowed at a time; null when closed.
    /// </summary>
    private Window? _openPluginManagerDialog;

    /// <summary><para>打开插件管理器页面；若已存在则将焦点转移到已打开的页面而非新开。</para>Opens the Plugin Manager page; if one is already open, transfers focus to it instead of opening a new one.</summary>
    [RelayCommand]
    private void PluginManager()
    {
        OpenSingletonDialog(() => _openPluginManagerDialog, d => _openPluginManagerDialog = d, () => new PluginManagerDialog());
    }

    /// <summary>
    /// <para>以主窗口为 owner 打开指定类型的单例管理器对话框：若同类型已打开则转移焦点，否则新建并记录引用；对话框关闭时清空引用以便再次打开。</para>
    /// Opens a singleton manager dialog owned by the main window: if the same type is already open, transfers focus to it; otherwise creates a new instance and records the reference. The reference is cleared on close so it can be reopened later.
    /// </summary>
    /// <param name="getSlot"><para>读取该类型当前已打开对话框引用的委托。</para>Delegate reading the currently open dialog reference of this type.</param>
    /// <param name="setSlot"><para>写入该类型当前已打开对话框引用的委托。</para>Delegate writing the currently open dialog reference of this type.</param>
    /// <param name="factory"><para>构造新对话框实例的工厂。</para>Factory producing a fresh dialog instance.</param>
    private void OpenSingletonDialog(Func<Window?> getSlot, Action<Window?> setSlot, Func<Window> factory)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        // 同类型已打开：转移焦点而非新开
        if (getSlot() is { } existing)
        {
            if (!existing.IsActive)
            {
                existing.Activate();
            }
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.Focus();
            return;
        }

        // 新建并记录引用；关闭时清空以便之后再次打开
        var dialog = factory();
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(getSlot(), dialog))
            {
                setSlot(null);
            }
        };
        setSlot(dialog);
        dialog.Show(owner);
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
    /// <summary><para>全局日志面板。</para>Global log panel.</summary>
    Log,
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
/// <para>当前激活的底栏主面板枚举，由 MainView 在 TabbedPage 切换时发送。</para>
/// Currently active bottom main panel enum, sent by MainView when the TabbedPage switches.
/// </summary>
public enum ActiveTab
{
    /// <summary><para>工作区面板。</para>Workspace panel.</summary>
    Workspace,
    /// <summary><para>节点编辑器面板。</para>Node editor panel.</summary>
    NodeEditor,
    /// <summary><para>全局日志面板。</para>Global log panel.</summary>
    Log,
    /// <summary><para>设置面板。</para>Settings panel.</summary>
    Settings
}

/// <summary>
/// <para>激活面板切换消息，由 MainView 发送给订阅者（如 MenuViewModel）以告知当前激活的底栏主面板。</para>
/// Active-tab-changed message, sent by MainView to subscribers (e.g. MenuViewModel) to inform them of the currently active bottom main panel.
/// </summary>
public class ActiveTabChangedMessage
{
    /// <summary><para>获取新激活的面板。</para>Gets the newly active tab.</summary>
    public ActiveTab Tab { get; }

    /// <summary>
    /// <para>创建激活面板切换消息。</para>
    /// Creates an active-tab-changed message.
    /// </summary>
    /// <param name="tab"><para>新激活的面板。</para>The newly active tab.</param>
    public ActiveTabChangedMessage(ActiveTab tab) => Tab = tab;
}

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
