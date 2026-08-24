using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Areas.Device;
using UotanStudio.Areas.Log;
using UotanStudio.Areas.NodeEditor;
using UotanStudio.Areas.Settings;
using UotanStudio.Areas.Workspace;
using UotanStudio.Core.Projects;
using UotanStudio.Messages;

namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // 设备面板是全局单例：首次访问时创建并在构造函数中注册到 DeviceViewModel.Instance，
    // 主窗口与工作区停靠面板共享同一实例，避免双实例双订阅设备变更。
    // The device panel is a global singleton: created on first access and registered in the
    // DeviceViewModel constructor, shared by the main window and the workspace dock so no
    // duplicate instance subscribes to device changes twice.
    public DeviceViewModel DeviceVM { get; } = DeviceViewModel.EnsureInstance();
    public LogViewModel LogVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();

    /// <summary>
    /// <para>获取当前打开工程在顶部显示的名称；未打开工程时为空。</para>
    /// Gets the currently open project name shown at the top; empty when none is open.
    /// </summary>
    [ObservableProperty]
    private string _projectTitle = string.Empty;

    /// <summary>
    /// <para>获取窗口管理器面板视图模型，操作共享的工作区停靠布局。</para>
    /// Gets the window manager panel view model, operating on the shared workspace dock.
    /// </summary>
    public WindowManagerViewModel WindowManagerVM { get; } = new();

    /// <summary>
    /// <para>初始化主视图模型并订阅工程打开消息以刷新顶部项目标题。</para>
    /// Initializes the main view model and subscribes to the project-opened message to
    /// refresh the top project title.
    /// </summary>
    public MainViewModel()
    {
        WeakReferenceMessenger.Default.Register<ProjectOpenedMessage>(this, OnProjectOpened);
        ProjectTitle = FirmwareProjectService.CurrentProject?.Name ?? string.Empty;
    }

    /// <summary>
    /// <para>处理工程打开消息：刷新顶部项目标题。</para>
    /// Handles the project-opened message by refreshing the top project title.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>工程打开消息。</para>The project-opened message.</param>
    private void OnProjectOpened(object recipient, ProjectOpenedMessage message)
        => ProjectTitle = FirmwareProjectService.CurrentProject?.Name ?? string.Empty;
}