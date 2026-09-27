using System.Collections.Specialized;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
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
            node.ExecutionError = string.Join(Environment.NewLine, issues.Where(issue => ReferenceEquals(issue.Node, node)).Select(issue => issue.Message));
        }
        FailedNodes = issues.Where(issue => issue.Node is not null).Select(issue => issue.Node!).Distinct().ToArray();
        ExecutionStatus = issues.Count == 0 ? LanguageService.Instance.GetString("Node_Graph_Valid")
            : string.Join(Environment.NewLine, issues.Select(issue => issue.DisplayText));
        if (issues.Count > 0)
        {
            IsExecutionLogExpanded = true;
            AppendExecutionLog(ExecutionStatus);
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
            node.IsEditingLocked = IsExecuting;
            foreach (var port in node.Input.Concat(node.Output))
            {
                port.IsEditingLocked = IsExecuting;
            }
            AttachNodePropertyTracking(node);
        }
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        ValidateGraphCommand.NotifyCanExecuteChanged();
    }
}

internal enum NodeViewportAction
{
    FitAll,
    FocusSelection,
    Reset
}