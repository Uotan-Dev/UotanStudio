using System.Globalization;
using System.Text;
using Avalonia;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.VisualPlugins;

/// <summary>Builds portable workflow-template and composite-node packages from a canvas graph.</summary>
public static class VisualPluginPackageBuilder
{
    /// <summary>Creates a package from the current graph and derives dependencies and public ports.</summary>
    public static VisualPluginPackage Create(string name, string id, VisualPluginKind kind,
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections,
        string author = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        if (nodes.Count == 0) throw new InvalidOperationException("An empty workflow cannot be exported.");
        if (nodes.Any(node => node.IsUnavailable))
            throw new InvalidOperationException("A workflow containing unavailable nodes cannot be exported as a plugin.");

        var normalizedId = NormalizeId(id);
        var graph = new WorkflowSnapshot
        {
            Nodes = WorkflowPersistenceService.BuildNodeDtos(nodes),
            Connections = WorkflowPersistenceService.BuildConnectionDtos(nodes, connections)
        };
        RemoveMachineSpecificPathDefaults(graph);
        var descriptors = NodeFactoryRegistrar.Descriptors.ToDictionary(
            descriptor => descriptor.TypeKey, StringComparer.OrdinalIgnoreCase);
        var capabilities = nodes
            .Select(node => descriptors.GetValueOrDefault(node.TypeKey)?.Capabilities ?? NodeCapability.None)
            .Aggregate(NodeCapability.None, (current, next) => current | next);
        var package = new VisualPluginPackage
        {
            Kind = kind,
            Manifest = new VisualPluginManifest
            {
                Id = normalizedId,
                Name = name.Trim(),
                Version = "1.0.0",
                Author = author.Trim(),
                RequiredNodeTypes = [.. nodes.Select(node => node.TypeKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)],
                Capabilities = [.. Enum.GetValues<NodeCapability>()
                    .Where(capability => capability != NodeCapability.None && capabilities.HasFlag(capability))
                    .Select(capability => capability.ToString())]
            },
            Graph = graph
        };

        if (kind == VisualPluginKind.CompositeNode)
        {
            package.Inputs = BuildPublicPorts(nodes, connections, input: true);
            package.Outputs = BuildPublicPorts(nodes, connections, input: false);
        }

        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return package;
    }

    /// <summary>
    /// Rebuilds an installed package while retaining its manifest contract and stable public port identities.
    /// </summary>
    public static VisualPluginPackage CreateUpdated(VisualPluginPackage existing,
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var updated = Create(existing.Manifest.Name, existing.Manifest.Id, existing.Kind,
            nodes, connections, existing.Manifest.Author);
        updated.Manifest.Version = existing.Manifest.Version;
        updated.Manifest.Description = existing.Manifest.Description;
        updated.Manifest.NodeApiVersion = existing.Manifest.NodeApiVersion;
        updated.Manifest.Platforms = [.. existing.Manifest.Platforms];
        updated.Manifest.Dependencies = existing.Manifest.Dependencies.Select(dependency => new VisualPluginDependency
        {
            Id = dependency.Id,
            MinimumVersion = dependency.MinimumVersion,
            MaximumVersionExclusive = dependency.MaximumVersionExclusive
        }).ToList();
        updated.Manifest.Conflicts = [.. existing.Manifest.Conflicts];
        updated.Assets = [.. existing.Assets];
        updated.BundlePath = existing.BundlePath;

        if (existing.Kind == VisualPluginKind.CompositeNode)
        {
            PreservePublicPortContract(existing.Inputs, updated.Inputs);
            PreservePublicPortContract(existing.Outputs, updated.Outputs);
        }

        var errors = VisualPluginValidation.Validate(updated);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return updated;
    }

    /// <summary>Creates a reverse-domain-compatible package id from arbitrary user-visible text.</summary>
    public static string NormalizeId(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
            if (builder.Length == 120) break;
        }
        while (builder.Length > 0 && builder[^1] is '.' or '_' or '-') builder.Length--;
        var slug = builder.Length == 0 ? "workflow" : builder.ToString();
        return slug.Length >= 3 ? slug : "local." + slug;
    }

    private static List<VisualPluginPortDefinition> BuildPublicPorts(
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections, bool input)
    {
        var result = new List<VisualPluginPortDefinition>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, nodeIndex) in nodes.Select((node, index) => (node, index)))
        {
            var ports = input ? node.Input : node.Output;
            foreach (var port in ports.Where(port => port.PortType != NodePortType.Flow))
            {
                var connected = input
                    ? connections.Any(connection => ReferenceEquals(connection.Target, port))
                    : connections.Any(connection => ReferenceEquals(connection.Source, port));
                if (connected) continue;

                var baseId = $"{(input ? "in" : "out")}-{nodeIndex + 1}-{port.StableId}";
                var publicId = CreateUniquePortId(baseId, usedIds);
                result.Add(new VisualPluginPortDefinition
                {
                    Id = publicId,
                    Name = $"{node.Title} / {port.Title}",
                    PortType = port.PortType.ToString(),
                    NodeInstanceId = node.InstanceId,
                    PortId = port.StableId,
                    DefaultValue = GetPortableDefaultValue(input, port)
                });
            }
        }
        return result;
    }

    private static void PreservePublicPortContract(IReadOnlyList<VisualPluginPortDefinition> existing,
        IList<VisualPluginPortDefinition> updated)
    {
        var existingByEndpoint = existing
            .GroupBy(port => (port.NodeInstanceId, port.PortId))
            .ToDictionary(group => group.Key, group => group.First());
        var matched = new HashSet<VisualPluginPortDefinition>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var port in updated)
        {
            if (!existingByEndpoint.TryGetValue((port.NodeInstanceId, port.PortId), out var previous)) continue;
            port.Id = previous.Id;
            port.Name = previous.Name;
            port.Required = previous.Required;
            matched.Add(port);
            usedIds.Add(port.Id);
        }

        foreach (var port in updated.Where(port => !matched.Contains(port)))
            port.Id = CreateUniquePortId(port.Id, usedIds);
    }

    private static string CreateUniquePortId(string baseId, ISet<string> usedIds)
    {
        const int maximumLength = 128;
        var normalizedBase = baseId[..Math.Min(baseId.Length, maximumLength)];
        if (usedIds.Add(normalizedBase)) return normalizedBase;

        for (var suffix = 2; ; suffix++)
        {
            var ending = $"-{suffix}";
            var candidate = normalizedBase[..Math.Min(normalizedBase.Length, maximumLength - ending.Length)] + ending;
            if (usedIds.Add(candidate)) return candidate;
        }
    }

    private static string? GetPortableDefaultValue(bool input, NodeConnectorViewModel port)
    {
        if (!input || port.InitialValue is null) return null;
        var value = Convert.ToString(port.InitialValue, CultureInfo.InvariantCulture);
        return port.PortType == NodePortType.Path && LooksAbsolutePath(value) ? null : value;
    }

    private static void RemoveMachineSpecificPathDefaults(WorkflowSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            foreach (var port in node.InputPorts.Where(port =>
                         string.Equals(port.PortType, nameof(NodePortType.Path), StringComparison.OrdinalIgnoreCase)))
            {
                RemoveAbsoluteValue(node.InputInitialValuesByPortId, port.Id);
                RemoveAbsoluteValue(node.InputInitialValues, port.Title);
            }
            if (node.InputInitialValuesByPortId is { Count: 0 }) node.InputInitialValuesByPortId = null;
            if (node.InputInitialValues is { Count: 0 }) node.InputInitialValues = null;
        }
    }

    private static void RemoveAbsoluteValue(IDictionary<string, string>? values, string key)
    {
        if (values is not null && values.TryGetValue(key, out var value) && LooksAbsolutePath(value))
            values.Remove(key);
    }

    private static bool LooksAbsolutePath(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (Path.IsPathRooted(value)
               || value.StartsWith("//", StringComparison.Ordinal)
               || value.StartsWith("\\\\", StringComparison.Ordinal)
               || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':'
                   && value[2] is '/' or '\\'));
}