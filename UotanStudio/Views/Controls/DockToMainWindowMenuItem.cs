using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Dock.Model.Core;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Context-menu item shown on floating dock tabs and floating tool headers across all platforms to
/// return the dockable to the main window layout.
/// <para>全平台浮动停靠窗口标签右键与工具标题栏菜单项：将当前停靠项停靠回主窗口布局。</para>
/// </summary>
public sealed class DockToMainWindowMenuItem : MenuItem
{
    private ContextMenu? _hostContextMenu;

    /// <summary>
    /// Optional <see cref="MenuFlyout"/> hosting this item (e.g. a floating tool window header flyout),
    /// used so the popup can finish closing before the parent floating window is destroyed on Wayland.
    /// <para>承载本菜单项的可选 <see cref="MenuFlyout"/>（例如浮动工具窗口顶栏菜单），
    /// 用于在 Wayland 下等待弹出层完全关闭后再销毁父级浮动窗口。</para>
    /// </summary>
    internal MenuFlyout? HostFlyout { get; set; }

    /// <summary>Uses the standard menu-item theme.<para>使用标准菜单项主题。</para></summary>
    protected override Type StyleKeyOverride => typeof(MenuItem);

    /// <summary>
    /// Creates the return-to-main-window menu item.
    /// <para>创建“停靠回主窗口”菜单项。</para>
    /// </summary>
    public DockToMainWindowMenuItem()
    {
        Bind(HeaderProperty, new DynamicResourceExtension("DockMenu_Dock"));
        Icon = new AppIcon { Kind = AppIconKind.Pin, Width = 16, Height = 16 };
        Click += OnClick;
        DataContextChanged += (_, _) => RefreshVisibility();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        AttachHostContextMenu();
        RefreshVisibility();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        DetachHostContextMenu();
        base.OnDetachedFromLogicalTree(e);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshVisibility();
    }

    /// <summary>
    /// Re-evaluates whether the bound dockable currently lives in a floating window and updates
    /// <see cref="Visual.IsVisible"/> accordingly.
    /// <para>重新评估绑定的停靠项当前是否位于浮动窗口，并据此更新 <see cref="Visual.IsVisible"/>。</para>
    /// </summary>
    internal void RefreshVisibility()
    {
        var dockable = ResolveDockable();
        IsVisible = dockable is { Factory: IDockReturnFactory factory }
            && factory.CanDockToMainWindow(dockable);
    }

    private void AttachHostContextMenu()
    {
        var menu = this.FindLogicalAncestorOfType<ContextMenu>();
        if (ReferenceEquals(_hostContextMenu, menu))
        {
            return;
        }

        DetachHostContextMenu();
        _hostContextMenu = menu;
        if (_hostContextMenu is not null)
        {
            _hostContextMenu.Opening += OnContextMenuOpening;
        }
    }

    private void DetachHostContextMenu()
    {
        if (_hostContextMenu is not null)
        {
            _hostContextMenu.Opening -= OnContextMenuOpening;
            _hostContextMenu = null;
        }
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e) => RefreshVisibility();

    private IDockable? ResolveDockable()
    {
        if (DataContext is IDockable direct)
        {
            return direct;
        }

        if (_hostContextMenu?.PlacementTarget is StyledElement { DataContext: IDockable targetDockable })
        {
            return targetDockable;
        }

        if (this.FindLogicalAncestorOfType<ContextMenu>() is { PlacementTarget: StyledElement { DataContext: IDockable fallback } })
        {
            return fallback;
        }

        return null;
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveDockable() is not { Factory: IDockReturnFactory factory } dockable)
        {
            return;
        }

        // Defer returning (and potentially closing the floating HostWindow) until after the hosting
        // MenuFlyout / ContextMenu popup has completely closed. On Wayland, destroying a parent
        // xdg_toplevel while its child xdg_popup is still alive triggers a fatal protocol error
        // (xdg_wm_base error 2: destroyed popup not top most popup).
        void ScheduleDockReturn()
        {
            if (OperatingSystem.IsLinux())
            {
                // Give Avalonia's WaylandWorker thread a brief turn to flush xdg_popup.destroy
                // before CloseWindow destroys the parent HostWindow's xdg_toplevel.
                DispatcherTimer.RunOnce(() =>
                {
                    if (factory.CanDockToMainWindow(dockable))
                    {
                        factory.DockToMainWindow(dockable);
                    }
                }, TimeSpan.FromMilliseconds(50), DispatcherPriority.Background);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (factory.CanDockToMainWindow(dockable))
                {
                    factory.DockToMainWindow(dockable);
                }
            }, DispatcherPriority.Background);
        }

        if (HostFlyout is { IsOpen: true } flyout)
        {
            void OnFlyoutClosed(object? s, EventArgs args)
            {
                flyout.Closed -= OnFlyoutClosed;
                ScheduleDockReturn();
            }

            flyout.Closed += OnFlyoutClosed;
            flyout.Hide();
            return;
        }

        if (_hostContextMenu is { IsOpen: true } contextMenu)
        {
            void OnContextMenuClosed(object? s, RoutedEventArgs args)
            {
                contextMenu.Closed -= OnContextMenuClosed;
                ScheduleDockReturn();
            }

            contextMenu.Closed += OnContextMenuClosed;
            contextMenu.Close();
            return;
        }

        if (TopLevel.GetTopLevel(this) is PopupRoot { Parent: Popup { IsOpen: true } popup })
        {
            void OnPopupClosed(object? s, EventArgs args)
            {
                popup.Closed -= OnPopupClosed;
                ScheduleDockReturn();
            }

            popup.Closed += OnPopupClosed;
            popup.Close();
            return;
        }

        ScheduleDockReturn();
    }
}