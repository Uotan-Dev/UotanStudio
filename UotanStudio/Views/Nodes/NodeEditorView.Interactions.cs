using Avalonia;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView
{
    private NodeEditorViewModel? _interactionEditor;
    private bool _interactionAttached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _interactionAttached = true;
        BindInteractionHost();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _interactionAttached = false;
        BindInteractionHost();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindInteractionHost();
    }

    private void BindInteractionHost()
    {
        if (_interactionEditor is not null)
        {
            _interactionEditor.ViewportRequested -= OnViewportRequested;
            _interactionEditor.InsertionLocationProvider = null;
        }
        if (_interactionEditor is not null && ReferenceEquals(_interactionEditor.Interactions, WorkflowInteractionHost.Desktop))
        {
            _interactionEditor.Interactions = UnsupportedWorkflowInteractionService.Instance;
        }
        _interactionEditor = _interactionAttached ? DataContext as NodeEditorViewModel : null;
        if (_interactionEditor is not null)
        {
            _interactionEditor.ViewportRequested += OnViewportRequested;
            _interactionEditor.InsertionLocationProvider = GetInsertionLocation;
        }
        if (_interactionEditor is not null && ReferenceEquals(_interactionEditor.Interactions, UnsupportedWorkflowInteractionService.Instance))
        {
            _interactionEditor.Interactions = WorkflowInteractionHost.Desktop;
        }
    }
}