using System.Globalization;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.VisualPlugins;

/// <summary>Public and optional interfaces discovered without modifying a canvas.</summary>
public sealed record CompositeBoundaryAnalysis(
    IReadOnlyList<VisualPluginPortDefinition> Inputs,
    IReadOnlyList<VisualPluginPortDefinition> Outputs,
    IReadOnlyList<VisualPluginPortDefinition> InputCandidates,
    IReadOnlyList<VisualPluginPortDefinition> OutputCandidates);

/// <summary>Derives interfaces from actual graph crossings, never from coincident port names.</summary>
public static class CompositeBoundaryAnalyzer
{
    /// <summary>Finds crossing edges, missing required inputs, and optional literal/output candidates.</summary>
    public static CompositeBoundaryAnalysis Analyze(IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnectionViewModel> connections)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        var inputs = new List<VisualPluginPortDefinition>();
        var outputs = new List<VisualPluginPortDefinition>();
        var inputCandidates = new List<VisualPluginPortDefinition>();
        var outputCandidates = new List<VisualPluginPortDefinition>();
        var inputOwners = nodes.SelectMany(node => node.Input.Select(port => (node, port)))
            .ToDictionary(pair => pair.port, pair => pair.node);
        var outputOwners = nodes.SelectMany(node => node.Output.Select(port => (node, port)))
            .ToDictionary(pair => pair.port, pair => pair.node);
        var crossingInputs = connections.Where(edge => edge.Source is not null && edge.Target is not null
            && inputOwners.ContainsKey(edge.Target) && !outputOwners.ContainsKey(edge.Source));
        foreach (var group in crossingInputs.GroupBy(edge => (edge.Source, edge.Target!.PortType,
                     Choices: string.Join("\u001f", edge.Target.Choices.Select(choice => choice.Value)))))
        {
            var targets = group.Select(edge => edge.Target!).Distinct().ToArray();
            var definition = Define(inputOwners[targets[0]], targets[0], true);
            definition.Required = targets[0].PortType != NodePortType.Flow;
            definition.IsSensitive = group.Key.Source!.IsSensitive || targets.Any(target => target.IsSensitive);
            definition.DefaultValue = null;
            foreach (var target in targets.Skip(1))
            {
                definition.AdditionalBindings.Add(new VisualPluginPortBinding
                {
                    NodeInstanceId = inputOwners[target].InstanceId,
                    PortId = target.StableId
                });
            }
            inputs.Add(definition);
        }
        foreach (var (port, node) in inputOwners)
        {
            if (connections.Any(edge => ReferenceEquals(edge.Target, port)))
            {
                continue;
            }
            var definition = Define(node, port, true);
            var missing = port.InitialValue is null || port.InitialValue is string text && string.IsNullOrWhiteSpace(text);
            definition.Required = port.PortType != NodePortType.Flow && missing && port.IsRequired;
            if (definition.Required || port.ExposeOnComposite || port.PortType == NodePortType.Flow)
            {
                inputs.Add(definition);
            }
            else
            {
                inputCandidates.Add(definition);
            }
        }
        foreach (var (port, node) in outputOwners)
        {
            var definition = Define(node, port, false);
            var edges = connections.Where(edge => ReferenceEquals(edge.Source, port)).ToArray();
            if (edges.Any(edge => edge.Target is not null && !inputOwners.ContainsKey(edge.Target)))
            {
                outputs.Add(definition);
            }
            else if (edges.Length == 0)
            {
                if (port.PortType == NodePortType.Flow || port.ExposeOnComposite)
                {
                    outputs.Add(definition);
                }
                else
                {
                    outputCandidates.Add(definition);
                }
            }
        }
        return new CompositeBoundaryAnalysis(inputs, outputs, inputCandidates, outputCandidates);
    }

    private static VisualPluginPortDefinition Define(FlowNodeViewModel node, NodeConnectorViewModel port, bool input)
    {
        // Identity follows endpoints, not list positions or translated titles.
        var identity = $"{node.InstanceId}:{port.StableId}:{input}";
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return new VisualPluginPortDefinition
        {
            Id = (input ? "in-" : "out-") + Convert.ToHexStringLower(digest.AsSpan(0, 12)),
            Name = $"{node.LocalizedTitle} / {port.LocalizedTitle}",
            PortType = port.PortType.ToString(),
            NodeInstanceId = node.InstanceId,
            PortId = port.StableId,
            IsSensitive = port.IsSensitive,
            DefaultValue = input && !port.IsSensitive && port.InitialValue is not null
                ? Convert.ToString(port.InitialValue, CultureInfo.InvariantCulture) : null
        };
    }
}