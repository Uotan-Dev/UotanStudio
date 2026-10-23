using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NodifyM.Avalonia.Controls;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView
{
    private bool _isNarrowLayout;
    private bool _viewportSyncScheduled;

    /// <summary>
    /// Subscribes the canvas viewport to the surface size and the zoom so the editor keeps filling the canvas when
    /// either changes.
    /// <para>让画布视口跟随画布尺寸与缩放变化，使编辑器始终填满画布。</para>
    /// </summary>
    private void AttachCanvasViewportTracking()
    {
        EditorSurface.SizeChanged += OnEditorSurfaceSizeChanged;
        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    private void OnEditorSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => SyncCanvasViewportSize();

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != NodifyEditor.ZoomProperty)
        {
            return;
        }

        SyncCanvasViewportSize();
        ScheduleCanvasViewportSync();
    }

    /// <summary>
    /// Writes the editor's explicit size as the surface size divided by the zoom, which is the viewport size NodifyM
    /// expects for its pan and zoom transforms.
    /// <para>把编辑器的显式尺寸写为画布尺寸除以缩放值，即 NodifyM 平移与缩放变换所期望的视口尺寸。</para>
    /// </summary>
    internal void SyncCanvasViewportSize()
    {
        var width = EditorSurface.Bounds.Width;
        var height = EditorSurface.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var zoom = Editor.Zoom > 0 ? Editor.Zoom : 1;
        var targetWidth = width / zoom;
        var targetHeight = height / zoom;
        if (double.IsNaN(Editor.Width) || Math.Abs(Editor.Width - targetWidth) > 0.5)
        {
            Editor.Width = targetWidth;
        }
        if (double.IsNaN(Editor.Height) || Math.Abs(Editor.Height - targetHeight) > 0.5)
        {
            Editor.Height = targetHeight;
        }
    }

    /// <summary>
    /// Re-applies the viewport size after the current dispatcher turn. NodifyM recomputes the editor size from the
    /// surface size it cached when its own handler last ran, and that cache can be stale after the page is re-hosted,
    /// so the current size is written again once every other handler has finished.
    /// <para>在当前调度回合结束后重新套用视口尺寸：编辑器按上次缓存的画布尺寸重算自身大小，页面重新挂载后该缓存可能过期，因此等其余处理器结束后再写回当前尺寸。</para>
    /// </summary>
    private void ScheduleCanvasViewportSync()
    {
        if (_viewportSyncScheduled)
        {
            return;
        }

        _viewportSyncScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _viewportSyncScheduled = false;
            SyncCanvasViewportSize();
        }, DispatcherPriority.Loaded);
    }

    private void OnViewportRequested(NodeViewportAction action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_interactionAttached)
            {
                NavigateCanvas(action);
            }
        }, DispatcherPriority.Loaded);
    }

    internal void NavigateCanvas(NodeViewportAction action)
    {
        if (DataContext is not NodeEditorViewModel vm)
        {
            return;
        }
        Editor.UpdateLayout();
        if (action == NodeViewportAction.Reset)
        {
            SetViewport(1, new Point());
            return;
        }
        var nodes = action == NodeViewportAction.FocusSelection ? vm.SelectedNodes.ToArray() : [.. vm.Nodes];
        if (nodes.Length == 0 || EditorSurface.Bounds.Width <= 0 || EditorSurface.Bounds.Height <= 0)
        {
            return;
        }
        var visuals = Editor.GetVisualDescendants().OfType<NodifyM.Avalonia.Controls.Node>()
            .Where(visual => visual.DataContext is FlowNodeViewModel)
            .GroupBy(visual => (FlowNodeViewModel)visual.DataContext!)
            .ToDictionary(group => group.Key, group => group.First());
        Rect? bounds = null;
        foreach (var node in nodes)
        {
            var size = visuals.TryGetValue(node, out var visual) && visual.Bounds.Width > 0
                ? visual.Bounds.Size : new Size(240, 160);
            var rect = new Rect(node.Location, size);
            bounds = bounds is { } previous ? previous.Union(rect) : rect;
        }
        var content = bounds!.Value;
        var width = Math.Max(1, EditorSurface.Bounds.Width - 48);
        var height = Math.Max(1, EditorSurface.Bounds.Height - 48);
        var zoom = Math.Clamp(Math.Min(width / Math.Max(1, content.Width), height / Math.Max(1, content.Height)), 0.05, 1);
        SetViewport(zoom, new Point((EditorSurface.Bounds.Width / (2 * zoom)) - content.Center.X,
            (EditorSurface.Bounds.Height / (2 * zoom)) - content.Center.Y));
    }

    private void SetViewport(double zoom, Point offset)
    {
        Editor.Zoom = zoom;
        Editor.OffsetX = offset.X;
        Editor.OffsetY = offset.Y;
        Editor.ViewTranslateTransform.X = offset.X;
        Editor.ViewTranslateTransform.Y = offset.Y;
    }

    private Point GetInsertionLocation()
    {
        var zoom = Math.Max(Editor.Zoom, 0.01);
        var position = new Point((EditorSurface.Bounds.Width / (2 * zoom)) - Editor.OffsetX - 100,
            (EditorSurface.Bounds.Height / (2 * zoom)) - Editor.OffsetY - 50);
        if (DataContext is NodeEditorViewModel vm)
        {
            for (var attempt = 0; attempt < 8 && vm.Nodes.Any(node => Math.Abs(node.Location.X - position.X) < 24
                     && Math.Abs(node.Location.Y - position.Y) < 24); attempt++)
            {
                position += new Point(32, 32);
            }
        }
        return position;
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e) => _interactionEditor?.Undo();
    private void OnRedoClick(object? sender, RoutedEventArgs e) => _interactionEditor?.Redo();

    private void OnFocusFailureClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FlowNodeViewModel node })
        {
            _interactionEditor?.FocusFailureCommand.Execute(node);
        }
    }

    private void OnLocalEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors()
                .Any(visual => visual is TextBox or ComboBox or NumericUpDown)
            || DataContext is not NodeEditorViewModel vm)
        {
            return;
        }
        var commandModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers != commandModifier && e.KeyModifiers != (commandModifier | KeyModifiers.Shift))
        {
            return;
        }
        NodeEditorAction? action = e.Key switch
        {
            Key.Z => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NodeEditorAction.Redo : NodeEditorAction.Undo,
            Key.Y => NodeEditorAction.Redo,
            Key.C => NodeEditorAction.Copy,
            Key.X => NodeEditorAction.Cut,
            Key.V => NodeEditorAction.Paste,
            Key.A => NodeEditorAction.SelectAll,
            _ => null
        };
        if (action is { } selected)
        {
            vm.ExecuteAction(selected);
            e.Handled = true;
        }
    }

    private void OnToolboxPaneToggle(object? sender, RoutedEventArgs e)
    {
        if (ToolboxPaneToggle.IsChecked == true)
        {
            PropertiesPaneToggle.IsChecked = false;
        }
        UpdateResponsivePanes();
    }

    private void OnPropertiesPaneToggle(object? sender, RoutedEventArgs e)
    {
        if (PropertiesPaneToggle.IsChecked == true)
        {
            ToolboxPaneToggle.IsChecked = false;
        }
        UpdateResponsivePanes();
    }

    private void UpdateResponsivePanes()
    {
        var narrow = PaneLayout.Bounds.Width < 900;
        var hintWidth = Bounds.Width > 0 ? Bounds.Width : PaneLayout.Bounds.Width;
        CanvasHintVisible = hintWidth >= CanvasHintMinimumWidth;
        if (narrow != _isNarrowLayout)
        {
            _isNarrowLayout = narrow;
            var columns = PaneLayout.ColumnDefinitions;
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(narrow ? 0 : 1);
            columns[2].Width = narrow ? new GridLength(0) : new GridLength(4, GridUnitType.Star);
            columns[3].Width = new GridLength(narrow ? 0 : 1);
            columns[4].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

            EditorToolbar.ColumnDefinitions[1].Width = narrow
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(1, GridUnitType.Auto);
        }
        NarrowPaneControls.IsVisible = narrow;
        LeftPaneDivider.IsVisible = LeftPaneHandle.IsVisible = !narrow;
        RightPaneDivider.IsVisible = RightPaneHandle.IsVisible = !narrow;
        ToolboxPane.IsVisible = !narrow || ToolboxPaneToggle.IsChecked == true;
        PropertiesPane.IsVisible = !narrow || PropertiesPaneToggle.IsChecked == true;
        Grid.SetColumn(EditorSurface, narrow ? 0 : 2);
        Grid.SetColumnSpan(EditorSurface, narrow ? 5 : 1);
        Grid.SetColumn(ToolboxPane, 0);
        Grid.SetColumn(PropertiesPane, narrow ? 0 : 4);
        Grid.SetColumnSpan(ToolboxPane, narrow ? 5 : 1);
        Grid.SetColumnSpan(PropertiesPane, narrow ? 5 : 1);
        Grid.SetRow(ToolboxPane, narrow ? 1 : 0);
        Grid.SetRow(PropertiesPane, narrow ? 1 : 0);
        PaneLayout.RowDefinitions[1].Height = new GridLength(narrow && (ToolboxPane.IsVisible || PropertiesPane.IsVisible)
            ? Math.Min(280, PaneLayout.Bounds.Height * 0.45) : 0);
        if (narrow)
        {
            foreach (var column in PaneLayout.ColumnDefinitions)
            {
                column.MinWidth = 0;
            }
        }
    }
}