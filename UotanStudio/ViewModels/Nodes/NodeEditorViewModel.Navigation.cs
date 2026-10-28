using System.Collections.Specialized;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.Workflow.Nodes;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    internal event Action<NodeViewportAction>? ViewportRequested;
    internal Func<Point>? InsertionLocationProvider { get; set; }

    public bool IsExecutionLogExpanded
    {
        get;
        set => SetProperty(ref field, value);
    } = false;

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
            deferConditionalRequirements: true).ToList();
        issues.AddRange(InspectCrossStageConnections());
        var stagePolicies = InspectStagePolicies();
        foreach (var node in Nodes)
        {
            var messages = issues.Where(issue => ReferenceEquals(issue.Node, node)).Select(issue => issue.Message)
                .Concat(stagePolicies.Where(policy => policy.Stage == node.ExecutionStage).Select(policy => policy.Message));
            var message = string.Join(Environment.NewLine, messages);
            node.ExecutionError = message;
            node.ValidationMessage = message;
        }
        FailedNodes = [.. issues.Where(issue => issue.Node is not null).Select(issue => issue.Node!)
            .Concat(Nodes.Where(node => stagePolicies.Any(policy => policy.Stage == node.ExecutionStage)))
            .Distinct()];
        var errors = issues.Select(issue => issue.DisplayText).Concat(stagePolicies.Select(policy => policy.Message))
            .Distinct(StringComparer.Ordinal).ToList();
        if (errors.Count == 0)
        {
            ExecutionStatus = LanguageService.Instance.GetString("Node_Graph_Valid");
        }
        else
        {
            ExecutionStatus = string.Join(Environment.NewLine, errors);
            IsExecutionLogExpanded = true;
            AppendExecutionLog(ExecutionStatus);
        }
    }

    /// <summary>
    /// Reports every connection whose endpoints belong to different execution stages.
    /// <para>报告两端属于不同执行阶段的每条连接。</para>
    /// </summary>
    /// <returns>One issue per cross-stage connection, attached to the target node.
    /// <para>每条跨阶段连接一条问题，挂到目标节点。</para></returns>
    private IReadOnlyList<WorkflowGraphIssue> InspectCrossStageConnections()
    {
        Dictionary<NodeConnectorViewModel, FlowNodeViewModel> owners = [];
        foreach (var node in Nodes)
        {
            foreach (var connector in node.Input.Concat(node.Output))
            {
                owners[connector] = node;
            }
        }

        var stageLabel = LanguageService.Instance.GetString("Node_Choice_Stage");
        List<WorkflowGraphIssue> issues = [];
        foreach (var connection in Connections)
        {
            if (connection.Source is not { } source || connection.Target is not { } target
                || !owners.TryGetValue(source, out var sourceNode)
                || !owners.TryGetValue(target, out var targetNode)
                || sourceNode.ExecutionStage == targetNode.ExecutionStage)
            {
                continue;
            }

            issues.Add(new WorkflowGraphIssue(targetNode,
                $"{sourceNode.DisplayHeader}.{source.Title} -> {targetNode.DisplayHeader}.{target.Title} | {stageLabel} S{sourceNode.ExecutionStage} -> S{targetNode.ExecutionStage}"));
        }
        return issues;
    }

    /// <summary>
    /// Rejects every stage that mixes patch writes with nodes outside the patch-safe set; device-only stages stay valid.
    /// <para>拒绝把补丁写入与补丁安全集合之外的节点混在一起的每个阶段；仅含设备的阶段保持有效。</para>
    /// </summary>
    /// <returns>One stage index and message for each unsafe patch stage.
    /// <para>每个不安全补丁阶段包含阶段索引与消息各一条。</para></returns>
    private IReadOnlyList<(int Stage, string Message)> InspectStagePolicies()
    {
        var stageLabel = LanguageService.Instance.GetString("Node_Choice_Stage");
        var unsupported = LanguageService.Instance.GetString("Node_Patch_UnsupportedGraph");
        List<(int Stage, string Message)> policies = [];
        foreach (var policy in PatchWorkflowPolicy.InspectStages(Nodes))
        {
            if (!policy.HasPatches || policy.Safe)
            {
                continue;
            }

            policies.Add((policy.Stage, $"{stageLabel} S{policy.Stage}: {unsupported}"));
        }
        return policies;
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
        RefreshExecutionStagePresentation();
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