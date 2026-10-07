using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    private ReplaceGraphCommand? _expandedCompositeCommand;

    /// <summary>
    /// Gets whether the most recent edit expanded a composite and can therefore be restored without
    /// discarding any subsequent graph changes.
    /// </summary>
    public bool CanCollapseExpandedComposite => !IsExecuting
        && _expandedCompositeCommand is not null
        && _undoStack.TryPeek(out var command)
        && ReferenceEquals(command, _expandedCompositeCommand);

    /// <summary>Discovers the selected subgraph's public interface without changing the document.</summary>
    public CompositeBoundaryAnalysis AnalyzeSelection()
    {
        return CompositeBoundaryAnalyzer.Analyze([.. SelectedNodes], [.. Connections]);
    }

    /// <summary>Encapsulates the selection as a single undoable document operation.</summary>
    public void EncapsulateSelection(string name, CompositeBoundaryAnalysis boundary)
    {
        if (IsExecuting)
        {
            throw new InvalidOperationException("Cannot change a running graph.");
        }
        var plan = CompositeGraphService.CreateEncapsulation(name, "local-" + Guid.NewGuid().ToString("N"),
            [.. Nodes], [.. Connections], [.. SelectedNodes], boundary);
        ApplyCompositePlan(plan);
    }

    /// <summary>Restores an embedded graph and its external wiring as one undoable operation.</summary>
    public void ExpandSelectedComposite()
    {
        if (IsExecuting || SelectedNode is not CompositeNodeViewModel composite)
        {
            return;
        }

        _expandedCompositeCommand = ApplyCompositePlan(CompositeGraphService.CreateExpansion([.. Nodes], [.. Connections], composite));
        OnPropertyChanged(nameof(CanCollapseExpandedComposite));
    }

    /// <summary>Restores the composite most recently expanded by this editor.</summary>
    public void CollapseExpandedComposite()
    {
        if (!CanCollapseExpandedComposite)
        {
            return;
        }

        Undo();
    }

    /// <summary>Publishes an edited local composite while retaining stable outside wiring.</summary>
    public void ReplaceComposite(CompositeNodeViewModel previous, VisualPluginPackage package)
    {
        if (IsExecuting || !Nodes.Contains(previous))
        {
            throw new InvalidOperationException("The source composite is unavailable or running.");
        }
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        var replacement = new CompositeNodeViewModel(package) { InstanceId = previous.InstanceId, Location = previous.Location };
        foreach (var port in replacement.Input)
        {
            var original = previous.Input.FirstOrDefault(candidate => candidate.StableId == port.StableId);
            if (original is not null)
            {
                port.InitialValue = original.InitialValue;
            }
        }
        var connections = Connections.Select(edge => new NodeConnectionViewModel(
            previous.Output.Contains(edge.Source!) ? replacement.Output.Single(port => port.StableId == edge.Source!.StableId) : edge.Source!,
            previous.Input.Contains(edge.Target!) ? replacement.Input.Single(port => port.StableId == edge.Target!.StableId) : edge.Target!)).ToArray();
        ApplyCompositePlan(new CompositeGraphEditPlan([.. Nodes.Select(node => ReferenceEquals(node, previous) ? replacement : node)],
            connections, replacement));
    }

    private ReplaceGraphCommand ApplyCompositePlan(CompositeGraphEditPlan plan)
    {
        EnsureEditable();
        var issues = WorkflowGraphValidation.Validate(plan.Nodes, plan.Connections);
        if (issues.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, issues.Select(issue => issue.DisplayText)));
        }
        var command = new ReplaceGraphCommand(this, [.. Nodes], [.. Connections], [.. SelectedNodes], plan);
        ExecuteCommand(command);
        return command;
    }

    private sealed class ReplaceGraphCommand(NodeEditorViewModel editor, IReadOnlyList<FlowNodeViewModel> oldNodes,
        IReadOnlyList<NodeConnectionViewModel> oldConnections, IReadOnlyList<FlowNodeViewModel> oldSelection,
        CompositeGraphEditPlan plan) : IEditorCommand
    {
        public string DisplayName => LanguageService.Instance.GetString("Node_Encapsulate");

        public void Execute() => Apply(plan.Nodes, plan.Connections, plan.Composite is { } composite ? [composite] : []);

        public void Undo() => Apply(oldNodes, oldConnections, oldSelection);

        private void Apply(IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections,
            IReadOnlyList<FlowNodeViewModel> selection)
        {
            editor.SetSelection([]);
            editor.Connections.Clear();
            editor.Nodes.Clear();
            foreach (var node in nodes)
            {
                editor.Nodes.Add(node);
                editor.AttachNodePropertyTracking(node);
            }
            foreach (var edge in connections)
            {
                editor.Connections.Add(edge);
            }
            editor.SetSelection(selection);
        }
    }
}