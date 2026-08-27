using UotanStudio.Services.Projects;
using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>项目资源管理器视图模型，以当前工程的 <c>images/</c> 与 <c>intermediate/</c> 目录为根展示真实目录树，
/// 订阅工程上下文变更消息并在打开/关闭工程时重建树。</para>
/// Project explorer view model, showing the real directory tree rooted at the current project's
/// <c>images/</c> and <c>intermediate/</c> folders, rebuilding on project open/close via the
/// project-context-changed message.
/// </summary>
internal class ProjectExplorerViewModel : Tool
{
    /// <summary>
    /// <para>树根节点集合，UI 绑定此项；未打开工程时为空白树。</para>
    /// Collection of root tree nodes, bound by the UI; empty when no project is open.
    /// </summary>
    public AvaloniaList<ExplorerNode> TreeViewContent { get; } = [];

    /// <summary>
    /// <para>创建项目资源管理器视图模型，订阅工程上下文变更消息并构建初始目录树。</para>
    /// Creates the project explorer view model, subscribing to project-context-changed messages and building the initial directory tree.
    /// </summary>
    public ProjectExplorerViewModel()
    {
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
        Refresh();
    }

    /// <summary>
    /// <para>响应工程上下文变更消息，重建目录树。</para>
    /// Responds to project-context-changed messages by rebuilding the directory tree.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>工程上下文变更消息。</para>The project-context-changed message.</param>
    private void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message) => Refresh();

    /// <summary>
    /// <para>重建目录树：以当前工程的 <c>images/</c> 与 <c>intermediate/</c> 目录为根；
    /// 未打开工程或目录不存在时显示空树，不抛出异常。</para>
    /// Rebuilds the directory tree rooted at the current project's <c>images/</c> and
    /// <c>intermediate/</c> directories; shows an empty tree without throwing when no project is
    /// open or the directories do not exist.
    /// </summary>
    public void Refresh()
    {
        TreeViewContent.Clear();
        var svc = ProjectContextService.Instance;
        AddRoot(svc.ImagesDirectory);
        AddRoot(svc.IntermediateDirectory);
    }

    private void AddRoot(string? directory)
    {
        if (directory is not null && Directory.Exists(directory))
        {
            TreeViewContent.Add(new ExplorerNode(directory, isDirectory: true));
        }
    }
}

/// <summary>
/// <para>资源管理器树节点，表示一个文件或目录；目录节点的子节点在首次展开时按需扫描加载。</para>
/// Explorer tree node representing a file or directory; directory children are loaded lazily on the first expansion.
/// </summary>
public sealed partial class ExplorerNode : ObservableObject
{
    /// <summary>
    /// <para>资源管理器右键「以文本打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as text" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsTextHeader => LanguageService.Instance.GetString("Explorer_OpenAsText");

    /// <summary>
    /// <para>资源管理器右键「以十六进制打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as hex" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsHexHeader => LanguageService.Instance.GetString("Explorer_OpenAsHex");

    /// <summary>
    /// <para>资源管理器右键「以设备树打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as device tree" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsDtbHeader => LanguageService.Instance.GetString("Explorer_OpenAsDtb");

    /// <summary>
    /// <para>资源管理器右键「以 DEX 打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as DEX" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsDexHeader => LanguageService.Instance.GetString("Explorer_OpenAsDex");

    /// <summary>
    /// <para>资源管理器右键「以 Manifest 打开（AXML）」菜单项的本地化标题。</para>
    /// Localized title of the "open as Manifest (AXML)" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsAxmlHeader => LanguageService.Instance.GetString("Explorer_OpenAsAxml");

    /// <summary>
    /// <para>资源管理器右键「以 ARSC 打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as ARSC" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsArscHeader => LanguageService.Instance.GetString("Explorer_OpenAsArsc");

    /// <summary>
    /// <para>资源管理器右键「以 APK 打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as APK" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsApkHeader => LanguageService.Instance.GetString("Explorer_OpenAsApk");

    /// <summary>
    /// <para>资源管理器右键「打开方式」子菜单的本地化标题。</para>
    /// Localized title of the "open with" submenu in the explorer.
    /// </summary>
    public static string OpenAsMenuHeader => LanguageService.Instance.GetString("Explorer_OpenAsMenu");

    private bool _childrenLoaded;

    /// <summary>
    /// <para>获取或设置节点显示名称。</para>
    /// Gets or sets the display name of the node.
    /// </summary>
    [ObservableProperty] private string _name;

    /// <summary>
    /// <para>获取节点的完整文件系统路径。</para>
    /// Gets the full file-system path of the node.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// <para>获取是否为目录节点。</para>
    /// Gets whether this node is a directory.
    /// </summary>
    public bool IsDirectory { get; }

    /// <summary>
    /// <para>获取是否为占位子节点（用于让尚未展开的目录显示展开箭头）。</para>
    /// Gets whether this node is a placeholder child (keeps the expander arrow visible for not-yet-expanded directories).
    /// </summary>
    public bool IsPlaceholder { get; }

    /// <summary>
    /// <para>获取子节点集合，在目录首次展开时填充。</para>
    /// Gets the collection of child nodes, populated on the directory's first expansion.
    /// </summary>
    public ObservableCollection<ExplorerNode> Children { get; } = [];

    /// <summary>
    /// <para>创建资源管理器节点；目录节点会预置一个占位子节点以显示展开箭头。</para>
    /// Creates an explorer node; directory nodes pre-insert a placeholder child so the expander arrow stays visible.
    /// </summary>
    /// <param name="path"><para>节点路径。</para>The node path.</param>
    /// <param name="isDirectory"><para>是否为目录。</para>Whether the node is a directory.</param>
    /// <param name="isPlaceholder"><para>是否为占位节点。</para>Whether the node is a placeholder.</param>
    public ExplorerNode(string path, bool isDirectory, bool isPlaceholder = false)
    {
        Path = path;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        _name = isPlaceholder ? string.Empty : GetDisplayName(path);
        if (isDirectory && !isPlaceholder)
        {
            Children.Add(new ExplorerNode(string.Empty, isDirectory: false, isPlaceholder: true));
        }
    }

    /// <summary>
    /// <para>首次展开目录时扫描并填充子节点；仅在首次调用时执行一次。</para>
    /// Scans and populates child nodes on the directory's first expansion; runs only once.
    /// </summary>
    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || !IsDirectory)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();

        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path))
            {
                Children.Add(new ExplorerNode(dir, isDirectory: true));
            }

            foreach (var file in Directory.EnumerateFiles(Path))
            {
                Children.Add(new ExplorerNode(file, isDirectory: false));
            }
        }
        catch (Exception)
        {
            // 目录不可读等异常时保持空子节点，不中断资源管理器
        }
    }

    private static string GetDisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}