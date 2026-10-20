using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// Fills in icons for the Dock theme's own menus. The theme's context menus and header MenuFlyout are not
/// ordinary controls and cannot be styled, so after the menu opens we match its resolved headers to Iconica icons.
/// <para>为 Dock 主题自带的菜单补齐图标。主题中的右键菜单与顶栏 MenuFlyout 不是普通控件、无法用样式覆盖，
/// 因此菜单打开后按其已解析的标题匹配对应的 Iconica 图标。</para>
/// </summary>
internal static class DockMenuIcons
{
    private static readonly ConditionalWeakTable<MenuFlyout, object> _attachedFlyouts = new();
    private static int _wired;

    /// <summary>
    /// Wires app-wide: every Dock header (main window or floating window) attaches the Opened handler during the
    /// pointer-press / key-down tunnel phase, before the button pops the menu, so even the first open right after
    /// startup carries icons; the bubbling Click is only a fallback.
    /// <para>全局接线：Dock 顶栏（主窗口与浮动窗口一致）在指针按下/键盘按下的隧道阶段先挂上 Opened 处理，
    /// 按钮随后才弹出菜单，因此启动后第一次打开也带图标；Click 冒泡仅作兜底。</para>
    /// </summary>
    internal static void EnsureWired()
    {
        if (Interlocked.Exchange(ref _wired, 1) == 1)
        {
            return;
        }

        InputElement.PointerPressedEvent.AddClassHandler<ToolChromeControl>(
            (chrome, _) => AttachHeader(chrome), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<ToolChromeControl>(
            (chrome, _) => AttachHeader(chrome), RoutingStrategies.Tunnel, handledEventsToo: true);
        Button.ClickEvent.AddClassHandler<ToolChromeControl>(
            (chrome, _) => AttachHeader(chrome), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal static void Apply(ContextMenu menu) => ApplyItems(menu.Items);

    internal static void Apply(MenuFlyout menu) => ApplyItems(menu.Items);

    /// <summary>
    /// Registers a one-time <c>Opened</c> handler for the header MenuFlyout, filling icons after the menu opens
    /// and its headers have resolved.
    /// <para>为顶栏 MenuFlyout 注册一次性的 <c>Opened</c> 处理，在其打开、标题解析完成后再补齐图标。</para>
    /// </summary>
    private static void AttachHeader(ToolChromeControl chrome)
    {
        if (chrome.ToolFlyout is not MenuFlyout flyout || _attachedFlyouts.TryGetValue(flyout, out _))
        {
            return;
        }

        _attachedFlyouts.Add(flyout, new object());
        flyout.Opened += (_, _) => Apply(flyout);
    }

    private static void ApplyItems(System.Collections.IEnumerable items)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem item)
            {
                continue;
            }

            if (item.Icon is null && ResolveIcon(item.Header) is { } kind)
            {
                item.Icon = new AppIcon { Kind = kind, Width = 16, Height = 16 };
            }

            ApplyItems(item.Items);
        }
    }

    private static AppIconKind? ResolveIcon(object? header)
    {
        var text = (header?.ToString() ?? string.Empty).Trim();
        var accessKey = text.LastIndexOf(" (_", StringComparison.Ordinal);
        if (accessKey > 0)
        {
            text = text[..accessKey].Trim();
        }

        if (text.Length == 0)
        {
            return null;
        }

        var lang = LanguageService.Instance;
        if (text == lang.GetString("DockMenu_Float"))
        {
            return AppIconKind.Float;
        }

        if (text == lang.GetString("DockMenu_FloatAll"))
        {
            return AppIconKind.FloatAll;
        }

        if (text == lang.GetString("DockMenu_Show"))
        {
            return AppIconKind.Show;
        }

        if (text == lang.GetString("DockMenu_Dock"))
        {
            return AppIconKind.Pin;
        }

        if (text == lang.GetString("DockMenu_AutoHide"))
        {
            return AppIconKind.PinOff;
        }

        if (text == lang.GetString("DockMenu_DockAsDocument"))
        {
            return AppIconKind.Tab;
        }

        if (text == lang.GetString("DockMenu_CloseOthers"))
        {
            return AppIconKind.CloseOthers;
        }

        if (text == lang.GetString("DockMenu_CloseAll"))
        {
            return AppIconKind.CloseAll;
        }

        if (text == lang.GetString("DockMenu_Close"))
        {
            return AppIconKind.Close;
        }

        if (text == lang.GetString("DockMenu_TabLayout"))
        {
            return AppIconKind.Tab;
        }

        if (text == lang.GetString("DockMenu_TabsLeft"))
        {
            return AppIconKind.TabsLeft;
        }

        if (text == lang.GetString("DockMenu_TabsTop"))
        {
            return AppIconKind.TabsTop;
        }

        if (text == lang.GetString("DockMenu_TabsRight"))
        {
            return AppIconKind.TabsRight;
        }

        return null;
    }
}