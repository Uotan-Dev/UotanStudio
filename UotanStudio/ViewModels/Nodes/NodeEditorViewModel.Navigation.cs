using System.Collections.Specialized;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    internal event Action<NodeViewportAction>? ViewportRequested;
    internal Func<Point>? InsertionLocationProvider { get; set; }

    public bool IsExecutionLogExpanded
    {
        get;
        set => SetProperty(ref field, value);
    }

    public IReadOnlyList<FlowNodeViewModel> FailedNodes
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasFailures));
            }
        }
    } = [];

    public bool HasFailures => FailedNodes.Count > 0;

    [RelayCommand]
    private void FitAll() => ViewportRequested?.Invoke(NodeViewportAction.FitAll);

    [RelayCommand]
    private void FocusSelection() => ViewportRequested?.Invoke(NodeViewportAction.FocusSelection);

    [RelayCommand]
    private void FocusFailure(FlowNodeViewModel? node)
    {
        node ??= FailedNodes.FirstOrDefault();
        if (node is not null && Nodes.Contains(node))
        {
            SetSelection([node]);
            FocusSelection();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ValidateGraph()
    {
        var issues = WorkflowGraphValidation.Validate(Nodes, Connections, forExecution: true,
            deferConditionalRequirements: true);
        foreach (var node in Nodes)
        {
            var message = string.Join(Environment.NewLine, issues.Where(issue => ReferenceEquals(issue.Node, node)).Select(issue => issue.Message));
            node.ExecutionError = message;
            node.ValidationMessage = message;
        }
        FailedNodes = [.. issues.Where(issue => issue.Node is not null).Select(issue => issue.Node!).Distinct()];
        var patchPolicy = PatchWorkflowPolicy.Inspect(Nodes);
        var patchError = patchPolicy.HasPatches && !patchPolicy.Safe
            ? LanguageService.Instance.GetString("Node_Patch_UnsupportedGraph")
            : null;
        var graphError = issues.Count == 0 ? null : string.Join(Environment.NewLine, issues.Select(issue => issue.DisplayText));
        ExecutionStatus = string.Join(Environment.NewLine, new[] { graphError, patchError }.Where(error => !string.IsNullOrEmpty(error)));
        if (ExecutionStatus.Length == 0)
        {
            ExecutionStatus = LanguageService.Instance.GetString("Node_Graph_Valid");
        }
        else
        {
            IsExecutionLogExpanded = true;
            AppendExecutionLog(ExecutionStatus);
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressDirtyTracking)
        {
            MarkDirty();
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            DetachAllPropertyTracking();
        }
        else if (e.OldItems is not null)
        {
            foreach (FlowNodeViewModel node in e.OldItems)
            {
                DetachPropertyTracking(node);
                foreach (var port in node.Input.Concat(node.Output))
                {
                    DetachPropertyTracking(port);
                }
            }
        }
        var added = e.Action == NotifyCollectionChangedAction.Reset
            ? Nodes : e.NewItems?.Cast<FlowNodeViewModel>() ?? [];
        foreach (var node in added)
        {
            if (e.Action != NotifyCollectionChangedAction.Reset)
            {
                ClearNodeValidation(node);
            }
            node.IsEditingLocked = IsExecuting;
            foreach (var port in node.Input.Concat(node.Output))
            {
                port.IsEditingLocked = IsExecuting;
            }
            AttachNodePropertyTracking(node);
        }
        if (e.Action == NotifyCollectionChangedAction.Remove)
        {
            foreach (var node in Nodes)
            {
                ClearNodeValidation(node);
            }
        }
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        ValidateGraphCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears stale validation and execution errors on a node after a graph edit.</summary>
    private static void ClearNodeValidation(FlowNodeViewModel node)
    {
        if (!node.HasValidationError && node.ExecutionError.Length == 0)
        {
            return;
        }
        node.ValidationMessage = string.Empty;
        node.ExecutionError = string.Empty;
    }

    /// <summary>Clears stale validation on both endpoints of the added or removed connections.</summary>
    private void ClearConnectionValidation(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            return;
        }
        if (e.OldItems is not null)
        {
            foreach (NodeConnectionViewModel connection in e.OldItems)
            {
                ClearConnectorValidation(connection.Source);
                ClearConnectorValidation(connection.Target);
            }
        }
        if (e.NewItems is not null)
        {
            foreach (NodeConnectionViewModel connection in e.NewItems)
            {
                ClearConnectorValidation(connection.Source);
                ClearConnectorValidation(connection.Target);
            }
        }
    }

    private void ClearConnectorValidation(NodeConnectorViewModel? connector)
    {
        if (connector is null)
        {
            return;
        }
        foreach (var node in Nodes)
        {
            if (node.Input.Contains(connector) || node.Output.Contains(connector))
            {
                ClearNodeValidation(node);
                return;
            }
        }
    }
}

internal enum NodeViewportAction
{
    FitAll,
    FocusSelection,
    Reset
}