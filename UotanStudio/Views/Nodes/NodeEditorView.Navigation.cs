using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView
{
    private bool _isNarrowLayout;

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
        var nodes = action == NodeViewportAction.FocusSelection ? vm.SelectedNodes.ToArray() : vm.Nodes.ToArray();
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