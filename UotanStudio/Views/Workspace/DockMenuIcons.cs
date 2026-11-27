using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
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
    private sealed class HeaderFlyoutState
    {
        public WeakReference<ToolChromeControl>? LastChrome { get; set; }
    }

    private static readonly ConditionalWeakTable<MenuFlyout, HeaderFlyoutState> _attachedFlyouts = new();
    private static readonly ConditionalWeakTable<ToolPinItemControl, object> _attachedPinItems = new();
    private static int _wired;

    /// <summary>
    /// Wires app-wide: every Dock header (main window or floating window) attaches the Opened handler during the
    /// pointer-press / key-down tunnel phase, before the button pops the menu, so even the first open right after
    /// startup carries icons; the bubbling Click is only a fallback. Also wires auto-hidden edge tabs
    /// (<see cref="ToolPinItemControl"/>) to toggle preview on click and expose the unified pin context menu.
    /// <para>全局接线：Dock 顶栏（主窗口与浮动窗口一致）在指针按下/键盘按下的隧道阶段先挂上 Opened 处理，
    /// 按钮随后才弹出菜单，因此启动后第一次打开也带图标；Click 冒泡仅作兜底。同时为自动隐藏边缘标签
    /// （<see cref="ToolPinItemControl"/>）接线点击展开/收起切换与统一的右键菜单。</para>
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

        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ToolPinItemControl>(
            (pinItem, _) => AttachPinItem(pinItem));
        InputElement.PointerPressedEvent.AddClassHandler<ToolPinItemControl>(
            (pinItem, _) => AttachPinItem(pinItem), RoutingStrategies.Tunnel, handledEventsToo: true);
        Control.ContextRequestedEvent.AddClassHandler<ToolPinItemControl>(
            OnPinItemContextRequested, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnPinItemContextRequested(ToolPinItemControl pinItem, ContextRequestedEventArgs e)
    {
        AttachPinItem(pinItem);
        if (pinItem.ContextMenu is { } menu)
        {
            menu.Open(pinItem);
            e.Handled = true;
        }
    }

    private static void AttachPinItem(ToolPinItemControl pinItem)
    {
        foreach (var textBlock in pinItem.GetVisualDescendants().OfType<TextBlock>())
        {
            textBlock.ContextMenu = null;
        }

        if (_attachedPinItems.TryGetValue(pinItem, out _))
        {
            return;
        }

        var button = pinItem.GetVisualDescendants().OfType<Button>().FirstOrDefault();
        if (button is null)
        {
            return;
        }

        _attachedPinItems.Add(pinItem, new object());
        pinItem.ContextMenu = CreatePinItemContextMenu();

        button.ClearValue(Button.CommandProperty);
        button.Click += (_, _) =>
        {
            if (pinItem.DataContext is IDockable { Factory: { } factory } dockable)
            {
                factory.TogglePreviewPinnedDockable(dockable);
            }
        };
    }

    private static ContextMenu CreatePinItemContextMenu()
    {
        var menu = new ContextMenu();
        var showItem = new MenuItem
        {
            Icon = new AppIcon { Kind = AppIconKind.Show, Width = 16, Height = 16 }
        };
        showItem.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("DockMenu_Show"));
        showItem.Click += (_, _) =>
        {
            var dockable = showItem.DataContext as IDockable
                ?? menu.PlacementTarget?.DataContext as IDockable;
            if (dockable is { Factory: { } factory })
            {
                factory.TogglePreviewPinnedDockable(dockable);
            }
        };

        menu.Items.Add(new ToolDockMenuItem { Action = ToolDockMenuAction.Dock });
        menu.Items.Add(showItem);
        menu.Items.Add(new ToolDockMenuItem { Action = ToolDockMenuAction.Float });
        menu.Items.Add(new ToolDockMenuItem { Action = ToolDockMenuAction.DockAsDocument });
        menu.Items.Add(new Separator());
        menu.Items.Add(new ToolDockMenuItem { Action = ToolDockMenuAction.Close });
        return menu;
    }

    internal static void Apply(ContextMenu menu) => ApplyItems(menu.Items);

    internal static void Apply(MenuFlyout menu) => ApplyItems(menu.Items);

    /// <summary>
    /// Registers a one-time handler for the header MenuFlyout, injecting the floating-window
    /// return-to-main-window item and filling icons when the menu opens. Because the theme can share a
    /// single MenuFlyout across multiple <see cref="ToolChromeControl"/> instances, the active chrome is
    /// resolved dynamically on every open.
    /// <para>为顶栏 MenuFlyout 注册一次性处理，在打开前刷新浮动窗口“停靠回主窗口”菜单项并在打开后补齐图标。
    /// 由于主题可能在多个 <see cref="ToolChromeControl"/> 之间复用同一个 MenuFlyout，因此每次打开时动态解析当前标题栏。</para>
    /// </summary>
    private static void AttachHeader(ToolChromeControl chrome)
    {
        if (chrome.ToolFlyout is not MenuFlyout flyout)
        {
            return;
        }

        if (_attachedFlyouts.TryGetValue(flyout, out var existing))
        {
            existing.LastChrome = new WeakReference<ToolChromeControl>(chrome);
            return;
        }

        var state = new HeaderFlyoutState
        {
            LastChrome = new WeakReference<ToolChromeControl>(chrome)
        };
        _attachedFlyouts.Add(flyout, state);
        flyout.Opening += (_, _) =>
        {
            var activeChrome = (flyout.Target as Visual)?.FindAncestorOfType<ToolChromeControl>();
            if (activeChrome is null && state.LastChrome is { } weak && weak.TryGetTarget(out var last))
            {
                activeChrome = last;
            }

            var activeDockable = (activeChrome?.DataContext as IDock)?.ActiveDockable
                ?? activeChrome?.DataContext as IDockable;
            foreach (var entry in flyout.Items)
            {
                if (entry is ToolDockMenuItem toolItem)
                {
                    toolItem.HostFlyout = flyout;
                    toolItem.RefreshState(activeDockable);
                }
            }
        };
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

        if (text == lang.GetString("DockMenu_Dock")
            || text == lang.GetString("DockMenu_DockAsTool"))
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