using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Areas.Editor;
using UotanStudio.Core.Projects;
using UotanStudio.Messages;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Workspace;

/// <summary>
/// <para>窗口管理器：提供顶部「窗口」菜单所需的数据与动作（工具窗口显隐、工程部件与文档切换），
/// 并在工程打开时自动启用与工程相关的窗口。</para>
/// Window manager: supplies the data and actions for the top-level "Window" menu (toggle tool
/// windows, switch project components and documents), auto-enabling project-related windows
/// when a project is opened.
/// </summary>
public class WindowManagerViewModel : ViewModelBase
{
    /// <summary>
    /// <para>可切换的工具窗口 id 集合（顺序即菜单展示顺序）。</para>
    /// Ids of the toggleable tool windows (order defines the menu display order).
    /// </summary>
    private static readonly string[] ToolIds =
        ["ProjectExplorer", "Properties", "Analyzer", "Output", "AdbLog"];

    /// <summary>
    /// <para>工程打开时需要自动启用的窗口 id。</para>
    /// Window ids auto-enabled when a project is opened.
    /// </summary>
    private static readonly string[] ProjectToolIds = ["ProjectExplorer", "Analyzer", "Output"];

    /// <summary>
    /// <para>获取当前工作区实例；工作区视图尚未创建时为空（此时窗口菜单不应操作布局）。</para>
    /// Gets the current workspace instance; null until the workspace view is created (the
    /// window menu should then not attempt to modify the layout).
    /// </summary>
    private static WorkspaceViewModel? Workspace => WorkspaceViewModel.Instance;

    /// <summary>
    /// <para>获取可切换的工具窗口 id 集合。</para>
    /// Gets the toggleable tool window ids.
    /// </summary>
    public IReadOnlyList<string> ToolWindowIds => ToolIds;

    /// <summary>
    /// <para>初始化窗口管理器并订阅工程打开消息。</para>
    /// Initializes the window manager and subscribes to the project-opened message.
    /// </summary>
    public WindowManagerViewModel()
    {
        WeakReferenceMessenger.Default.Register<ProjectOpenedMessage>(this, OnProjectOpened);
    }

    /// <summary>
    /// <para>获取指定工具窗口的显示标题。</para>
    /// Gets the display title of the given tool window.
    /// </summary>
    /// <param name="id"><para>窗口 id。</para>The window id.</param>
    /// <returns>窗口标题；未知 id 时返回原 id。</returns>
    public string ToolTitle(string id)
        => Workspace is not null
        && Workspace.WindowCatalog.TryGetValue(id, out var entry)
            ? entry.Window.Title
            : id;

    /// <summary>
    /// <para>判断指定工具窗口当前是否显示。</para>
    /// Determines whether the given tool window is currently shown.
    /// </summary>
    /// <param name="id"><para>窗口 id。</para>The window id.</param>
    /// <returns>是否显示。</returns>
    public bool IsToolShown(string id) => Workspace?.IsWindowShown(id) ?? false;

    /// <summary>
    /// <para>显示或隐藏指定工具窗口。</para>
    /// Shows or hides the given tool window.
    /// </summary>
    /// <param name="id"><para>窗口 id。</para>The window id.</param>
    /// <param name="visible"><para>是否显示。</para>Whether to show.</param>
    public void SetToolVisible(string id, bool visible) => Workspace?.SetWindowVisible(id, visible);

    /// <summary>
    /// <para>获取当前工程部件文件的完整路径列表（仅保留磁盘上存在的文件）。</para>
    /// Gets the full paths of the current project's component files (only those existing on disk).
    /// </summary>
    public IReadOnlyList<string> ComponentPaths
    {
        get
        {
            var project = FirmwareProjectService.CurrentProject;
            if (project is null)
            {
                return [];
            }

            var list = new List<string>();
            foreach (var relative in project.Components)
            {
                var full = Path.IsPathRooted(relative)
                    ? relative
                    : Path.Combine(project.RootDirectory, relative);
                if (File.Exists(full))
                {
                    list.Add(full);
                }
            }

            return list;
        }
    }

    /// <summary>
    /// <para>获取当前打开的文档集合（编辑器实例）。</para>
    /// Gets the currently open documents (editor instance).
    /// </summary>
    public IReadOnlyList<OpenDocument> Documents => EditorViewModel.Instance.Documents;

    /// <summary>
    /// <para>在编辑器中打开指定工程部件文件。</para>
    /// Opens the given project component file in the editor.
    /// </summary>
    /// <param name="fullPath"><para>部件文件完整路径。</para>The component file full path.</param>
    public void OpenComponent(string fullPath)
        => WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(fullPath));

    /// <summary>
    /// <para>激活（切换到）编辑器中指定路径的文档。</para>
    /// Activates (switches to) the document with the given path in the editor.
    /// </summary>
    /// <param name="path"><para>文档路径。</para>The document path.</param>
    public void ActivateDocument(string path) => _ = EditorViewModel.Instance.OpenFileAsync(path);

    /// <summary>
    /// <para>处理工程打开消息：自动启用与工程相关的窗口。</para>
    /// Handles the project-opened message by auto-enabling the project-related windows.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>工程打开消息。</para>The project-opened message.</param>
    private void OnProjectOpened(object recipient, ProjectOpenedMessage message)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        foreach (var id in ProjectToolIds)
        {
            workspace.SetWindowVisible(id, visible: true);
        }
    }
}