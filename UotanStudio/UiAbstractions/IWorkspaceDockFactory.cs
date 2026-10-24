using Dock.Model.Core;

namespace UotanStudio.UiAbstractions;

/// <summary>
/// Workspace dock factory surface consumed by view models and service adapters without referencing the
/// concrete factory type.
/// <para>视图模型与服务适配器使用的工作区 Dock 工厂接口，不引用具体工厂类型。</para>
/// </summary>
public interface IWorkspaceDockFactory : IFactory
{
    /// <summary>
    /// The project-explorer panel instance owned by the factory, or null before it is created.
    /// <para>工厂持有的工程资源管理器面板实例；尚未创建时为 null。</para>
    /// </summary>
    object? ProjectExplorer { get; }

    /// <summary>
    /// Closes every document dockable in the layout.
    /// <para>关闭布局中的全部文档型停靠项。</para>
    /// </summary>
    /// <param name="layout">The layout root to walk. <para>待遍历的布局根。</para></param>
    void CloseAllDocuments(IDockable? layout);

    /// <summary>
    /// Stops every terminal process owned by the workspace.
    /// <para>停止工作区拥有的全部终端进程。</para>
    /// </summary>
    void DisposeTerminal();

    /// <summary>
    /// Docks the specified dockable into a tool dock in the active layout.
    /// <para>将指定停靠项停靠到当前布局的工具停靠区。</para>
    /// </summary>
    /// <param name="dockable">The dockable to dock as a tool window. <para>要停靠为工具窗口的停靠项。</para></param>
    void DockAsTool(IDockable? dockable);

    /// <summary>
    /// Docks the dockable supplied as a command parameter into a tool dock in the active layout.
    /// <para>将作为命令参数传入的停靠项停靠到当前布局的工具停靠区。</para>
    /// </summary>
    /// <param name="parameter">The command parameter carrying the dockable. <para>携带停靠项的命令参数。</para></param>
    void DockAsTool(object? parameter);
}