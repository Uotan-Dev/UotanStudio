using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Threading.Tasks;
using UotanStudio.Messages;
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
        await PickProjectFileAsync(isSave: true);
    }

    /// <summary>
    /// <para>打开现有项目，弹出文件打开对话框选择 .uotan 工程文件。</para>
    /// Opens an existing project by showing a file-open dialog to pick a .uotan project file.
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        await PickProjectFileAsync(isSave: false);
    }

    /// <summary>
    /// <para>保存当前项目，若无路径则触发另存流程。</para>
    /// Saves the current project, triggering Save As if there is no project path yet.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        // 后端实现：将当前工作区状态序列化到项目文件
        // 此处由后续项目持久化服务接入
    }

    /// <summary>
    /// <para>另存为，弹出文件保存对话框选择新项目路径。</para>
    /// Saves the project under a new path, showing a file-save dialog.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        await PickProjectFileAsync(isSave: true);
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

    /// <summary><para>撤销操作。</para>Undo operation.</summary>
    [RelayCommand]
    private void Undo() { }

    /// <summary><para>重做操作。</para>Redo operation.</summary>
    [RelayCommand]
    private void Redo() { }

    /// <summary><para>剪切选中内容。</para>Cut selection.</summary>
    [RelayCommand]
    private void Cut() { }

    /// <summary><para>复制选中内容。</para>Copy selection.</summary>
    [RelayCommand]
    private void Copy() { }

    /// <summary><para>粘贴剪贴板内容。</para>Paste clipboard contents.</summary>
    [RelayCommand]
    private void Paste() { }

    /// <summary><para>删除选中内容。</para>Delete selection.</summary>
    [RelayCommand]
    private void Delete() { }

    /// <summary><para>全选。</para>Select all.</summary>
    [RelayCommand]
    private void SelectAll() { }

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

    /// <summary><para>打开 ADB 终端。</para>Open the ADB shell console.</summary>
    [RelayCommand]
    private void AdbShell() { }

    /// <summary><para>打开 Fastboot 控制台。</para>Open the Fastboot console.</summary>
    [RelayCommand]
    private void FastbootConsole() { }

    /// <summary><para>打开语言管理器。</para>Open the language manager.</summary>
    [RelayCommand]
    private void LanguageManager() { }

    /// <summary><para>打开插件管理器。</para>Open the plugin manager.</summary>
    [RelayCommand]
    private void PluginManager() { }

    // ===== 帮助 / Help =====

    /// <summary><para>打开在线文档。</para>Open online documentation.</summary>
    [RelayCommand]
    private void Documentation() { }

    /// <summary><para>打开问题反馈页面。</para>Open the issue reporting page.</summary>
    [RelayCommand]
    private void ReportIssue() { }

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
    /// <para>弹出文件选择/保存对话框选取项目文件路径。</para>
    /// Shows a file open/save dialog to pick a project file path.
    /// </summary>
    /// <param name="isSave">
    /// <para>是否为保存对话框；false 则为打开对话框。</para>
    /// Whether this is a save dialog; false means open dialog.
    /// </param>
    private static async Task PickProjectFileAsync(bool isSave)
    {
        var topLevel = TopLevel;
        if (topLevel == null) return;

        if (isSave)
        {
            _ = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString("Menu_NewProject"),
                DefaultExtension = "uotan",
                FileTypeChoices =
                [
                    new FilePickerFileType("UotanStudio Project") { Patterns = ["*.uotan"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
        }
        else
        {
            _ = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Menu_OpenProject"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("UotanStudio Project") { Patterns = ["*.uotan"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
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
    ResetZoom
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
