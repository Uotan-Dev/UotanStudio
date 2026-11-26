using Dock.Model.Core;

namespace UotanStudio.UiAbstractions;

/// <summary>
/// Dock factory capability for returning a floating dockable back to the main window layout.
/// <para>用于将浮动停靠项停靠回主窗口布局的 Dock 工厂能力。</para>
/// </summary>
public interface IDockReturnFactory
{
    /// <summary>
    /// Reports whether the supplied dockable currently lives in a floating window and can be docked
    /// back into the main window.
    /// <para>报告给定停靠项当前是否位于浮动窗口中，且可以停靠回主窗口。</para>
    /// </summary>
    /// <param name="dockable">The dockable to inspect.<para>待检查的停靠项。</para></param>
    /// <returns><see langword="true"/> when the dockable can be returned to the main window.<para>可以停靠回主窗口时返回 <see langword="true"/>。</para></returns>
    bool CanDockToMainWindow(IDockable? dockable);

    /// <summary>
    /// Moves the supplied floating dockable back into an appropriate dock in the main window and
    /// closes its floating window when no dockables remain in it.
    /// <para>将给定的浮动停靠项移回主窗口中合适的停靠区；若原浮动窗口不再包含任何停靠项，则将其关闭。</para>
    /// </summary>
    /// <param name="dockable">The floating dockable to return.<para>要停靠回主窗口的浮动停靠项。</para></param>
    void DockToMainWindow(IDockable? dockable);
}