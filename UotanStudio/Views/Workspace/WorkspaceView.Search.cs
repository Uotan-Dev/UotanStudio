using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;

namespace UotanStudio.Views.Workspace;

public partial class WorkspaceView
{
    private DocumentDockControl? _searchDocumentDock;
    private InputElement? _searchReturnFocus;

    private void InitializeDocumentSearch()
    {
        LayoutUpdated += OnSearchLayoutUpdated;
        DocumentSearchPanel.CloseRequested += (_, _) => CloseDocumentSearch();
        DocumentSearchPanel.OptionsChanged += (_, _) =>
        {
            _searchOperationStatus = null;
            _searchResumeOffset = null;
            _documentSearchCancellation?.Cancel();
            QueueDocumentSearch();
        };
        DocumentSearchPanel.ActionRequested += ExecuteSearchAction;
        DetachedFromVisualTree += (_, _) => StopDocumentSearch();
        AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnSearchLayoutUpdated(object? sender, EventArgs e)
    {
        if (DocumentSearchPanel.IsVisible && !_searchActionRunning && !_searchNavigating
            && (!ReferenceEquals(_searchContextDocument, ActiveSearchDocument)
                || _searchProjectRoot != Services.Projects.ProjectContextService.Instance.RootDirectory))
        {
            _searchProjectRoot = Services.Projects.ProjectContextService.Instance.RootDirectory;
            CaptureSearchSelection();
            QueueDocumentSearch();
        }
        if (_searchDocumentDock is null || !_searchDocumentDock.IsEffectivelyVisible
            || !_searchDocumentDock.GetVisualAncestors().Contains(DockControl))
        {
            _searchDocumentDock = DockControl.GetVisualDescendants().OfType<DocumentDockControl>()
                .FirstOrDefault(control => control.IsEffectivelyVisible && control.DataContext is IDocumentDock { Id: "DocumentsPane" });
        }

        if (_searchDocumentDock is null || _searchDocumentDock.TranslatePoint(default, this) is not { } origin)
        {
            DocumentSearchHost.IsVisible = false;
            return;
        }

        var tabs = _searchDocumentDock.GetVisualDescendants().OfType<DocumentTabStrip>()
            .FirstOrDefault(control => control.IsEffectivelyVisible);
        var contentTop = tabs?.TranslatePoint(new Point(0, tabs.Bounds.Height), _searchDocumentDock)?.Y ?? 0;
        var availableHeight = Math.Max(0, _searchDocumentDock.Bounds.Height - contentTop);
        DocumentSearchHost.IsVisible = _searchDocumentDock.Bounds.Width >= 260 && availableHeight >= 40;
        DocumentSearchHost.Width = _searchDocumentDock.Bounds.Width;
        DocumentSearchHost.Height = availableHeight;
        Canvas.SetLeft(DocumentSearchHost, origin.X);
        Canvas.SetTop(DocumentSearchHost, origin.Y + contentTop);
        DocumentSearchPanel.HorizontalAlignment = DocumentSearchHost.Width >= 308
            ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        DocumentSearchPanel.Width = Math.Min(300, Math.Max(0, DocumentSearchHost.Width - 8));
        DocumentSearchPanel.MaxHeight = Math.Max(0, availableHeight - 8);
    }

    internal void ShowDocumentSearch(bool showReplace)
    {
        OnSearchLayoutUpdated(this, EventArgs.Empty);
        if (!DocumentSearchHost.IsVisible) return;

        if (!DocumentSearchPanel.IsVisible)
        {
            _searchReturnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as InputElement;
            CaptureSearchSelection();
            if (SearchEditor(ActiveSearchDocument) is { SelectionLength: > 0 } editor
                && editor.SelectedText.Length <= 256 && !editor.SelectedText.Contains('\n'))
                DocumentSearchPanel.SetQuery(editor.SelectedText);
        }
        DocumentSearchPanel.IsVisible = true;
        QueueDocumentSearch();
        Dispatcher.UIThread.Post(() => DocumentSearchPanel.FocusFind(showReplace), DispatcherPriority.Input);
    }

    private void CloseDocumentSearch()
    {
        DocumentSearchPanel.IsVisible = false;
        StopDocumentSearch();
        if (_searchReturnFocus is { IsEffectivelyVisible: true }
            && _searchReturnFocus.GetVisualAncestors().Contains(this)) _searchReturnFocus.Focus();
        else Focus();
        _searchReturnFocus = null;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DocumentSearchPanel.IsVisible && !DocumentSearchPanel.IsScopeDropDownOpen)
        {
            CloseDocumentSearch();
            e.Handled = true;
        }
    }
}