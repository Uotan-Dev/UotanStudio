using Avalonia;
using Avalonia.Controls;
using UotanStudio.Models;

namespace UotanStudio.Views.Behaviors;

/// <summary>
/// Applies view model-managed protocol order and visibility to the flash page's protocol panel host.
/// <para>把视图模型管理的协议顺序与可见性应用到烧录页面的协议面板容器。</para>
/// </summary>
public static class FlashProtocolDisplayBehavior
{
    /// <summary>Defines the ordered flash protocol display source.<para>定义有序的烧录协议显示数据源。</para></summary>
    public static readonly AttachedProperty<IEnumerable<DisplayPreference>?> DisplayProperty =
        AvaloniaProperty.RegisterAttached<Panel, IEnumerable<DisplayPreference>?>(
            "Display",
            typeof(FlashProtocolDisplayBehavior));

    static FlashProtocolDisplayBehavior() =>
        DisplayProperty.Changed.AddClassHandler<Panel>(OnDisplayChanged);

    /// <summary>Gets the protocol display source.<para>获取协议显示数据源。</para></summary>
    /// <param name="panel">The protocol panel host.<para>协议面板容器。</para></param>
    /// <returns>The ordered protocol preferences.<para>有序的协议偏好。</para></returns>
    public static IEnumerable<DisplayPreference>? GetDisplay(Panel panel) =>
        panel.GetValue(DisplayProperty);

    /// <summary>Sets the protocol display source.<para>设置协议显示数据源。</para></summary>
    /// <param name="panel">The protocol panel host.<para>协议面板容器。</para></param>
    /// <param name="value">The ordered protocol preferences.<para>有序的协议偏好。</para></param>
    public static void SetDisplay(Panel panel, IEnumerable<DisplayPreference>? value) =>
        panel.SetValue(DisplayProperty, value);

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
        _ => null
    };
}