using Dock.Model.Core;

namespace UotanStudio.Areas.DockControl;

/// <summary>
/// <para>为 Dock 布局预置非空的能力策略对象，避免主题模板绑定报 "Value is null"。</para>
/// Pre-seeds non-null capability policy objects into the dock layout to prevent
/// "Value is null" binding errors in the dock theme templates.
/// </summary>
internal static class DockCapabilityDefaults
{
    /// <summary>
    /// <para>遍历布局，为所有 dockable 预置 <see cref="DockCapabilityOverrides"/>，为所有 dock 预置 <see cref="DockCapabilityPolicy"/>。</para>
    /// Walks the layout, pre-seeding <see cref="DockCapabilityOverrides"/> on every dockable
    /// and <see cref="DockCapabilityPolicy"/> on every dock.
    /// </summary>
    /// <param name="layout">
    /// <para>根布局。</para>
    /// The root layout.
    /// </param>
    public static void Ensure(IDockable layout)
    {
        if (layout is null)
        {
            return;
        }

        layout.DockCapabilityOverrides ??= new DockCapabilityOverrides();

        if (layout is IDock dock && dock.VisibleDockables is not null)
        {
            dock.DockCapabilityPolicy ??= new DockCapabilityPolicy();

            foreach (var child in dock.VisibleDockables)
            {
                Ensure(child);
            }
        }
    }
}