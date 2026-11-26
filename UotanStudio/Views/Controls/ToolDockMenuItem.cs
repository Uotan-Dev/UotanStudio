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
/// Semantic action performed by a <see cref="ToolDockMenuItem"/> inside tool chrome and tool tab menus.
/// <para>工具窗口顶栏与标签右键菜单中 <see cref="ToolDockMenuItem"/> 执行的语义动作。</para>
/// </summary>
public enum ToolDockMenuAction
{
    /// <summary>Floats the active tool into its own window.<para>将当前工具窗口浮动为独立窗口。</para></summary>
    Float,

    /// <summary>Floats every tool in the current tool dock.<para>将当前工具停靠区中的所有工具浮动。</para></summary>
    FloatAll,

    /// <summary>Returns a floating or auto-hidden tool back to the main window.<para>将浮动或自动隐藏的工具停靠回主窗口。</para></summary>
    Dock,

    /// <summary>Auto-hides a docked tool to its corresponding edge strip.<para>将主窗口中已停靠的工具自动隐藏到对应边缘。</para></summary>
    AutoHide,

    /// <summary>Moves the tool into the tabbed document pane.<para>将工具停靠为文档窗口。</para></summary>
    DockAsDocument,

    /// <summary>Closes the dockable.<para>关闭当前标签页。</para></summary>
    Close,

    /// <summary>Closes all other dockables in the same dock.<para>关闭同停靠区内的其他标签页。</para></summary>
    CloseOthers
}

/// <summary>
/// Context-menu and flyout item for tool window headers, tool tab strips, and pinned tool strips that
/// keeps <see cref="ToolDockMenuAction.Dock"/> strictly bound to returning to the main window and
/// <see cref="ToolDockMenuAction.AutoHide"/> strictly bound to pinning docked tools.
/// <para>用于工具窗口顶栏、底部多页标签和自动隐藏边缘条的菜单项：保证“停靠”严格用于停靠回主窗口
/// （或取消自动隐藏），“自动隐藏”严格用于收起主窗口中已停靠的工具。</para>
/// </summary>
public sealed class ToolDockMenuItem : MenuItem
{
    /// <summary>
    /// Identifies the <see cref="Action"/> styled property.
    /// <para>标识 <see cref="Action"/> 样式属性。</para>
    /// </summary>
    public static readonly StyledProperty<ToolDockMenuAction> ActionProperty =
        AvaloniaProperty.Register<ToolDockMenuItem, ToolDockMenuAction>(nameof(Action));

    private ContextMenu? _hostContextMenu;
    private IDockable? _explicitDockable;

    /// <summary>Uses the standard menu-item theme.<para>使用标准菜单项主题。</para></summary>
    protected override Type StyleKeyOverride => typeof(MenuItem);

    /// <summary>
    /// Gets or sets the semantic action bound to this menu item.
    /// <para>获取或设置此菜单项绑定的语义动作。</para>
    /// </summary>
    public ToolDockMenuAction Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <summary>
    /// Creates a new tool dock menu item.
    /// <para>创建新的工具停靠菜单项。</para>
    /// </summary>
    public ToolDockMenuItem()
    {
        Click += OnClick;
        DataContextChanged += (_, _) => RefreshState();
        ApplyPresentation(Action);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActionProperty)
        {
            ApplyPresentation(change.GetNewValue<ToolDockMenuAction>());
            RefreshState();
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        AttachHostContextMenu();
        RefreshState();
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
        RefreshState();
    }

    /// <summary>
    /// Updates <see cref="Visual.IsVisible"/> and <see cref="InputElement.IsEnabled"/> using an explicit
    /// dockable resolved by the hosting flyout.
    /// <para>使用宿主菜单显式解析的停靠项刷新 <see cref="Visual.IsVisible"/> 与启用状态。</para>
    /// </summary>
    /// <param name="explicitDockable">The active dockable for the opening menu.<para>当前打开菜单对应的活动停靠项。</para></param>
    internal void RefreshState(IDockable? explicitDockable)
    {
        _explicitDockable = explicitDockable;
        RefreshState();
    }

    /// <summary>
    /// Re-evaluates visibility and enabled state against the current dockable's floating and pinned state.
    /// <para>根据当前停靠项的浮动与自动隐藏状态重新计算可见性与启用状态。</para>
    /// </summary>
    internal void RefreshState()
    {
        var dockable = ResolveDockable();
        var factory = dockable?.Factory;
        if (dockable is null || factory is null)
        {
            IsVisible = false;
            IsEnabled = false;
            return;
        }

        var isFloating = factory is IDockReturnFactory returnFactory && returnFactory.CanDockToMainWindow(dockable);
        var isPinned = factory.IsDockablePinned(dockable);
        var isDockedInMain = !isFloating && !isPinned;

        switch (Action)
        {
            case ToolDockMenuAction.Float:
                IsVisible = dockable.CanFloat;
                IsEnabled = dockable.CanFloat && (!isFloating || HasMultipleVisiblePeers(dockable));
                break;

            case ToolDockMenuAction.FloatAll:
                IsVisible = dockable.CanFloat;
                IsEnabled = dockable.CanFloat && !isFloating;
                break;

            case ToolDockMenuAction.Dock:
                IsVisible = true;
                IsEnabled = isFloating || isPinned;
                break;

            case ToolDockMenuAction.AutoHide:
                IsVisible = dockable.CanPin && isDockedInMain;
                IsEnabled = dockable.CanPin && isDockedInMain;
                break;

            case ToolDockMenuAction.DockAsDocument:
                IsVisible = dockable.CanDockAsDocument;
                IsEnabled = dockable.CanDockAsDocument;
                break;

            case ToolDockMenuAction.Close:
                IsVisible = dockable.CanClose;
                IsEnabled = dockable.CanClose;
                break;

            case ToolDockMenuAction.CloseOthers:
                IsVisible = true;
                IsEnabled = HasMultipleVisiblePeers(dockable);
                break;
        }
    }

    private void ApplyPresentation(ToolDockMenuAction action)
    {
        var (headerKey, iconKind) = action switch
        {
            ToolDockMenuAction.Float => ("DockMenu_Float", AppIconKind.Float),
            ToolDockMenuAction.FloatAll => ("DockMenu_FloatAll", AppIconKind.FloatAll),
            ToolDockMenuAction.Dock => ("DockMenu_Dock", AppIconKind.Pin),
            ToolDockMenuAction.AutoHide => ("DockMenu_AutoHide", AppIconKind.PinOff),
            ToolDockMenuAction.DockAsDocument => ("DockMenu_DockAsDocument", AppIconKind.Tab),
            ToolDockMenuAction.Close => ("DockMenu_Close", AppIconKind.Close),
            ToolDockMenuAction.CloseOthers => ("DockMenu_CloseOthers", AppIconKind.CloseOthers),
            _ => ("DockMenu_Dock", AppIconKind.Pin)
        };

        Bind(HeaderProperty, new DynamicResourceExtension(headerKey));
        Icon = new AppIcon { Kind = iconKind, Width = 16, Height = 16 };
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

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        _explicitDockable = null;
        RefreshState();
    }

    private IDockable? ResolveDockable()
    {
        if (_explicitDockable is not null)
        {
            return _explicitDockable;
        }

        if (ExtractDockable(DataContext) is { } direct)
        {
            return direct;
        }

        if (_hostContextMenu?.PlacementTarget is StyledElement target
            && ExtractDockable(target.DataContext) is { } targetDockable)
        {
            return targetDockable;
        }

        if (this.FindLogicalAncestorOfType<ContextMenu>() is { PlacementTarget: StyledElement fallbackTarget }
            && ExtractDockable(fallbackTarget.DataContext) is { } fallbackDockable)
        {
            return fallbackDockable;
        }

        return null;
    }

    private static IDockable? ExtractDockable(object? context)
        => context switch
        {
            IDock dock => dock.ActiveDockable,
            IDockable dockable => dockable,
            _ => null
        };

    private static bool HasMultipleVisiblePeers(IDockable dockable)
        => dockable.Owner is IDock { VisibleDockables.Count: > 1 };

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        var dockable = ResolveDockable();
        var factory = dockable?.Factory;
        if (dockable is null || factory is null)
        {
            return;
        }

        switch (Action)
        {
            case ToolDockMenuAction.Float:
                if (dockable.CanFloat)
                {
                    factory.FloatDockable(dockable);
                }
                break;

            case ToolDockMenuAction.FloatAll:
                if (dockable.CanFloat)
                {
                    factory.FloatAllDockables(dockable);
                }
                break;

            case ToolDockMenuAction.Dock:
                if (factory is IDockReturnFactory returnFactory && returnFactory.CanDockToMainWindow(dockable))
                {
                    returnFactory.DockToMainWindow(dockable);
                }
                else if (factory.IsDockablePinned(dockable))
                {
                    factory.UnpinDockable(dockable);
                }
                break;

            case ToolDockMenuAction.AutoHide:
                if (dockable.CanPin
                    && !factory.IsDockablePinned(dockable)
                    && (factory is not IDockReturnFactory rf || !rf.CanDockToMainWindow(dockable)))
                {
                    factory.PinDockable(dockable);
                }
                break;

            case ToolDockMenuAction.DockAsDocument:
                if (dockable.CanDockAsDocument)
                {
                    factory.DockAsDocument(dockable);
                }
                break;

            case ToolDockMenuAction.Close:
                if (dockable.CanClose)
                {
                    factory.CloseDockable(dockable);
                }
                break;

            case ToolDockMenuAction.CloseOthers:
                if (HasMultipleVisiblePeers(dockable))
                {
                    factory.CloseOtherDockables(dockable);
                }
                break;
        }
    }
}