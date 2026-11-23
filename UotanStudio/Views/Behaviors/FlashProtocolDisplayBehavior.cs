using Avalonia;
using Avalonia.Controls;
using UotanStudio.Models;

namespace UotanStudio.Views.Behaviors;

/// <summary>
/// Applies view model-managed protocol order and visibility to the flash page's protocol panel host.
/// <para>把视图模型管理的协议顺序与可见性应用到烧录页面的协议面板容器。</para>
/// </summary>
public static partial class FlashProtocolDisplayBehavior
{
    static FlashProtocolDisplayBehavior() =>
        DisplayProperty.Changed.AddClassHandler<Panel>(OnDisplayChanged);

    /// <summary>Gets the ordered flash protocol display source.<para>获取有序的烧录协议显示数据源。</para></summary>
    /// <param name="panel">The protocol panel host.<para>协议面板容器。</para></param>
    /// <returns>The ordered protocol preferences.<para>有序的协议偏好。</para></returns>
    [GeneratedAttachedProperty]
    public static partial IEnumerable<DisplayPreference>? GetDisplay(Panel panel);

    private static void OnDisplayChanged(Panel panel, AvaloniaPropertyChangedEventArgs args) =>
        Apply(panel, args.GetNewValue<IEnumerable<DisplayPreference>?>());

    private static void Apply(Panel panel, IEnumerable<DisplayPreference>? display)
    {
        var preferences = display?.ToArray() ?? [];
        if (preferences.Length == 0)
        {
            return;
        }

        var panels = panel.Children
            .OfType<Expander>()
            .Where(item => KeyForName(item.Name) is not null)
            .ToDictionary(item => KeyForName(item.Name)!, StringComparer.Ordinal);
        foreach (var expander in panels.Values)
        {
            panel.Children.Remove(expander);
        }

        foreach (var preference in preferences)
        {
            if (!panels.TryGetValue(preference.Key, out var expander))
            {
                continue;
            }

            expander.IsVisible = preference.Visible;
            panel.Children.Add(expander);
        }
    }

    private static string? KeyForName(string? name) => name switch
    {
        "EdlExpander" => FlashProtocolCatalog.Edl,
        "MtkExpander" => FlashProtocolCatalog.Mtk,
        "RkExpander" => FlashProtocolCatalog.Rockchip,
        "SunxiExpander" => FlashProtocolCatalog.Sunxi,
        "UnisocExpander" => FlashProtocolCatalog.Unisoc,
        _ => null
    };
}