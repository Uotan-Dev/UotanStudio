using Dock.Model.Controls;
using Dock.Model.Core;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// Shared floating-window lifetime rules for the dock factories.
/// <para>停靠工厂共用的浮动窗口生命周期规则。</para>
/// </summary>
/// <remarks>
/// Dock removes a floating window only when <c>CollapseDock</c> runs on an emptied dock that is
/// collapsable. Every pane this host creates is marked <c>IsCollapsable = false</c> so the main layout
/// cannot collapse, which means a detached window whose pane has been dragged back keeps the emptied
/// pane inside its own root and stays on screen as an empty frame. Factories call
/// <see cref="CloseEmptiedWindows"/> after layout changes to retire those windows.
/// <para>Dock 仅在可折叠的空停靠区上执行 <c>CollapseDock</c> 时才回收浮动窗口。宿主创建的面板一律标记
/// <c>IsCollapsable = false</c> 以避免主布局折叠，因此被拖回内容后的独立窗口会把清空的面板留在自己的根里，
/// 以空框形式留在屏幕上。工厂在布局变更后调用 <see cref="CloseEmptiedWindows"/> 回收这些窗口。</para>
/// </remarks>
internal static class DockWindowLifetime
{
    /// <summary>
    /// Removes the floating windows whose layout no longer holds any tool or document.
    /// <para>移除布局中已不再包含任何工具或文档的浮动窗口。</para>
    /// </summary>
    /// <param name="factory">The factory owning the windows. <para>拥有这些窗口的工厂。</para></param>
    /// <param name="root">The layout root touched by the operation. <para>本次操作涉及的布局根。</para></param>
    internal static void CloseEmptiedWindows(IFactory factory, IRootDock? root)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (FindWindowOwnerRoot(root) is not { Windows: { Count: > 0 } windows })
        {
            return;
        }

        foreach (var window in windows.ToList())
        {
            if (window.Layout is null || ContainsToolOrDocument(window.Layout))
            {
                continue;
            }

            factory.RemoveWindow(window);
        }
    }

    /// <summary>
    /// Resolves the root that owns the floating-window registrations for a layout root. An operation
    /// that starts inside a floating window resolves to that window's detached root, whose window list
    /// is empty; walking up to the main-window root reaches every window the factory tracks.
    /// <para>解析布局根所归属的浮动窗口登记根。源自浮动窗口内的操作会解析到该窗口的独立根，其窗口列表为空；
    /// 上溯到主窗体根才能覆盖工厂跟踪的全部窗口。</para>
    /// </summary>
    /// <param name="root">The layout root resolved from the operation. <para>由操作解析出的布局根。</para></param>
    /// <returns>The owning root, or <see langword="null"/> when none is reachable. <para>归属根；不可达时为 <see langword="null"/>。</para></returns>
    internal static IRootDock? FindWindowOwnerRoot(IRootDock? root)
    {
        IRootDock? current = root;
        var visited = new HashSet<IRootDock>(ReferenceEqualityComparer.Instance);
        while (current is not null && visited.Add(current))
        {
            if (current.Window?.Owner is IRootDock parent)
            {
                current = parent;
                continue;
            }

            return current;
        }

        return current;
    }

    /// <summary>
    /// Whether the layout still holds at least one tool or document.
    /// <para>判定布局中是否仍至少包含一个工具或文档。</para>
    /// </summary>
    /// <param name="layout">The layout to inspect. <para>待检查的布局。</para></param>
    /// <returns>True when a tool or document remains. <para>仍有工具或文档时返回 true。</para></returns>
    internal static bool ContainsToolOrDocument(IDock layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Enumerate(layout).Any(static item => item is ITool or IDocument);
    }

    /// <summary>
    /// Enumerates the layout depth-first without following floating-window registrations.
    /// <para>深度优先枚举布局，不跟踪浮动窗口登记。</para>
    /// </summary>
    /// <param name="root">The layout root to walk. <para>待遍历的布局根。</para></param>
    /// <returns>Every reachable dockable. <para>可达的全部停靠项。</para></returns>
    private static IEnumerable<IDockable> Enumerate(IDockable root)
    {
        var visited = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IDockable>();
        pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item))
            {
                continue;
            }

            yield return item;
            if (item is IDock dock && dock.VisibleDockables is { } children)
            {
                foreach (var child in children.Reverse())
                {
                    pending.Push(child);
                }
            }
        }
    }
}