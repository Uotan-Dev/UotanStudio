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
    /// <para>最近一次编辑是否展开了组合节点，因而可在不丢弃后续图改动的情况下恢复。</para>
    /// </summary>
    /// <value>True when the tracked expansion is still the most recent edit.<para>被跟踪的展开仍是最近一次编辑时为 True。</para></value>
    public bool CanCollapseExpandedComposite => !IsExecuting
        && _expandedCompositeCommand is not null
        && _undoStack.TryPeek(out var command)
        && ReferenceEquals(command, _expandedCompositeCommand);

    /// <summary>Discovers the selected subgraph's public interface without changing the document.<para>发现所选子图的公共接口，不修改文档。</para></summary>
    /// <returns>The boundary analysis of the current selection.<para>当前选区的边界分析结果。</para></returns>
    public CompositeBoundaryAnalysis AnalyzeSelection()
    {
        return CompositeBoundaryAnalyzer.Analyze([.. SelectedNodes], [.. Connections]);
    }

    /// <summary>Encapsulates the selection as a single undoable document operation.<para>将选区封装为单个可撤销的文档操作。</para></summary>
    /// <param name="name">Display name for the new composite.<para>新组合节点的显示名称。</para></param>
    /// <param name="boundary">Public interface the composite must expose.<para>组合节点须暴露的公共接口。</para></param>
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

    /// <summary>Restores an embedded graph and its external connections as one undoable operation.<para>将内嵌图及其外部连接作为单个可撤销操作恢复。</para></summary>
    public void ExpandSelectedComposite()
    {
        if (IsExecuting || SelectedNode is not CompositeNodeViewModel composite)
        {
            return;
        }

        _expandedCompositeCommand = ApplyCompositePlan(CompositeGraphService.CreateExpansion([.. Nodes], [.. Connections], composite));
        OnPropertyChanged(nameof(CanCollapseExpandedComposite));
    }

    /// <summary>Restores the composite most recently expanded by this editor.<para>恢复最近一次展开的那个组合节点。</para></summary>
    public void CollapseExpandedComposite()
    {
        if (!CanCollapseExpandedComposite)
        {
            return;
        }

        Undo();
    }

    /// <summary>Publishes an edited local composite while retaining its stable outside connections.<para>发布已编辑的本地组合节点，同时保留其稳定的外部连接。</para></summary>
    /// <param name="previous">The composite node instance being replaced.<para>被替换的组合节点实例。</para></param>
    /// <param name="package">The edited package definition to publish.<para>要发布的已编辑软件包定义。</para></param>
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