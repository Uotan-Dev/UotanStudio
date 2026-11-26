using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
        if (ResolveDockable() is { Factory: IDockReturnFactory factory } dockable)
        {
            factory.DockToMainWindow(dockable);
        }
    }
}