using System.Text.RegularExpressions;

namespace UotanStudio.Services.Projects;

/// <summary>Structural validation for untrusted visual-plugin JSON.</summary>
public static partial class VisualPluginValidation
{
    /// <summary>Maximum accepted JSON package size.</summary>
    public const long MaximumFileSize = 8 * 1024 * 1024;

    /// <summary>Maximum number of graph nodes.</summary>
    public const int MaximumNodeCount = 1024;

    /// <summary>Maximum number of graph connections.</summary>
    public const int MaximumConnectionCount = 4096;

    /// <summary>Maximum total number of declared graph and public ports.</summary>
    public const int MaximumPortCount = 16384;

    /// <summary>Validates a package without consulting host node registrations.</summary>
    public static IReadOnlyList<string> Validate(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = new List<string>();
        // Nullable annotations do not prevent explicit JSON nulls in untrusted packages.
        if (package.Manifest is null || package.Graph is null || package.Inputs is null || package.Outputs is null)
        {
            return ["Plugin manifest, graph and public port collections must not be null."];
        }
        if (!Enum.IsDefined(package.Kind))
        {
            errors.Add("Unknown visual-plugin package kind.");
        }
        if (package.FormatVersion is not (1 or VisualPluginPackage.CurrentFormatVersion))
            errors.Add($"Unsupported visual-plugin format version {package.FormatVersion}.");

        if (package.Assets is null || package.Assets.Count > VisualPluginBundle.MaximumAssetCount)
        {
            return ["Patch resource collection is null or exceeds the resource count limit."];
        }
        if (package.Assets.Count > 0 && (package.FormatVersion < 2 || package.Kind != VisualPluginKind.CompositeNode))
        {
            errors.Add("Resources require a version 2 composite-node package.");
        }
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        long assetBytes = 0;
        foreach (var asset in package.Assets)
        {
            if (asset is null || asset.Id is null || !PortIdPattern().IsMatch(asset.Id)
                || !assetIds.Add(asset.Id) || string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 255
                || asset.Length is < 0 or > VisualPluginBundle.MaximumAssetSize
                || asset.Sha256 is null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            {
                errors.Add("Patch resource has an invalid identity, name, length or SHA-256.");
                continue;
            }
            assetBytes += asset.Length;
        }
        if (assetBytes > VisualPluginBundle.MaximumTotalAssetSize)
        {
            errors.Add("Patch resources exceed the total size limit.");
        }

        var manifest = package.Manifest;
        if (manifest.Dependencies is null || manifest.Conflicts is null
            || manifest.Dependencies.Count > 256 || manifest.Conflicts.Count > 256)
        {
            return ["Package dependency/conflict lists must not be null or exceed 256 entries."];
        }
        if ((manifest.Dependencies.Count > 0 || manifest.Conflicts.Count > 0)
            && (package.FormatVersion < 2 || package.Kind != VisualPluginKind.CompositeNode))
        {
            errors.Add("Package dependency/conflict declarations require a version 2 composite package.");
        }
        var dependencyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency is null || dependency.Id is null || !PluginIdPattern().IsMatch(dependency.Id)
                || !dependencyIds.Add(dependency.Id) || string.Equals(dependency.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Package dependency id is invalid, duplicated or self-referential.");
                continue;
            }
            if (dependency.MinimumVersion is not null && !VisualPluginDependency.TryParseVersion(dependency.MinimumVersion, out _)
                || dependency.MaximumVersionExclusive is not null && !VisualPluginDependency.TryParseVersion(dependency.MaximumVersionExclusive, out _))
            {
                errors.Add("Dependency bounds must use canonical major.minor.patch numeric versions.");
            }
            else if (dependency.MinimumVersion is not null && dependency.MaximumVersionExclusive is not null
                     && Version.Parse(dependency.MinimumVersion) >= Version.Parse(dependency.MaximumVersionExclusive))
            {
                errors.Add("Dependency version range must have an inclusive minimum below its exclusive maximum.");
            }
        }
        var conflictIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in manifest.Conflicts)
        {
            if (conflict is null || !PluginIdPattern().IsMatch(conflict) || !conflictIds.Add(conflict)
                || string.Equals(conflict, manifest.Id, StringComparison.OrdinalIgnoreCase) || dependencyIds.Contains(conflict))
            {
                errors.Add("Package conflict id is invalid, duplicated, self-referential or also required.");
            }
        }
        if (manifest.RequiredNodeTypes is null || manifest.Platforms is null || manifest.Capabilities is null
            || manifest.RequiredNodeTypes.Any(string.IsNullOrWhiteSpace)
            || manifest.Platforms.Any(string.IsNullOrWhiteSpace)
            || manifest.Capabilities.Any(string.IsNullOrWhiteSpace))
        {
            return ["Plugin dependency, platform and capability lists must contain nonempty values and must not be null."];
        }
        if (manifest.Id is null || !PluginIdPattern().IsMatch(manifest.Id))
            errors.Add("Plugin id must use lowercase ASCII letters, digits, dots, underscores, or hyphens.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128)
            errors.Add("Plugin name must contain between 1 and 128 characters.");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64)
            errors.Add("Plugin version must contain between 1 and 64 characters.");
        if (manifest.NodeApiVersion != 1)
            errors.Add($"Unsupported node API version {manifest.NodeApiVersion}.");

        var graph = package.Graph;
        if (graph.Nodes is null || graph.Connections is null
            || graph.Nodes.Any(node => node is null || node.InputPorts is null || node.OutputPorts is null)
            || graph.Connections.Any(connection => connection is null)
            || package.Inputs.Any(port => port is null)
            || package.Outputs.Any(port => port is null))
        {
            return ["Plugin graph collections and their elements must not be null."];
        }
        if (graph.Nodes.Count > MaximumNodeCount)
            errors.Add($"Plugin graph exceeds {MaximumNodeCount} nodes.");
        if (graph.Connections.Count > MaximumConnectionCount)
            errors.Add($"Plugin graph exceeds {MaximumConnectionCount} connections.");
        if (errors.Count > 0)
        {
            return errors;
        }

        var nodesById = new Dictionary<string, NodeDto>(StringComparer.Ordinal);
        long portCount = package.Inputs.Count + package.Outputs.Count;
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.InstanceId))
            {
                errors.Add("Every graph node must have an instance id.");
                continue;
            }
            if (!nodesById.TryAdd(node.InstanceId, node))
                errors.Add($"Duplicate graph node id '{node.InstanceId}'.");
            if (string.IsNullOrWhiteSpace(node.TypeKey))
                errors.Add($"Graph node '{node.InstanceId}' has no type key.");
            if (string.Equals(node.TypeKey, package.NodeTypeKey, StringComparison.OrdinalIgnoreCase))
                errors.Add("A composite plugin cannot contain itself.");
            ValidateNodePorts(node.InstanceId, node.InputPorts, "input", errors);
            ValidateNodePorts(node.InstanceId, node.OutputPorts, "output", errors);
            portCount += node.InputPorts.Count + node.OutputPorts.Count;
        }
        if (portCount > MaximumPortCount)
            errors.Add($"Plugin graph exceeds {MaximumPortCount} declared ports.");

        foreach (var connection in graph.Connections)
        {
            ValidateEndpoint(connection.SourceInstanceId, connection.SourcePortId,
                connection.SourceConnectorTitle, "source", true, nodesById, errors);
            ValidateEndpoint(connection.TargetInstanceId, connection.TargetPortId,
                connection.TargetConnectorTitle, "target", false, nodesById, errors);
        }

        if (package.Kind == VisualPluginKind.WorkflowTemplate
            && (package.Inputs.Count != 0 || package.Outputs.Count != 0))
            errors.Add("Workflow templates cannot declare composite-node ports.");

        if (package.Kind == VisualPluginKind.CompositeNode)
        {
            ValidatePublicPorts(package.Inputs, true, nodesById, errors);
            ValidatePublicPorts(package.Outputs, false, nodesById, errors);
        }

        var declaredTypes = manifest.RequiredNodeTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var typeKey in graph.Nodes.Select(node => node.TypeKey).Where(typeKey => !string.IsNullOrWhiteSpace(typeKey)))
        {
            if (!declaredTypes.Contains(typeKey))
                errors.Add($"Required node type '{typeKey}' is not declared by the manifest.");
        }

        return errors;
    }

    private static void ValidateNodePorts(string nodeId, IReadOnlyList<NodePortDto> ports, string direction,
        ICollection<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            if (port is null)
            {
                errors.Add($"Graph node '{nodeId}' has a null {direction} port.");
                continue;
            }
            if (port.Id is null || !PortIdPattern().IsMatch(port.Id) || !ids.Add(port.Id))
                errors.Add($"Graph node '{nodeId}' {direction} port id '{port.Id}' is invalid or duplicated.");
            if (string.IsNullOrWhiteSpace(port.Title) || port.Title.Length > 128)
                errors.Add($"Graph node '{nodeId}' {direction} port '{port.Id}' has an invalid title.");
            if (!IsKnownPortType(port.PortType))
                errors.Add($"Graph node '{nodeId}' {direction} port '{port.Id}' has unknown type '{port.PortType}'.");
            if (port.MaxConnections < 0)
                errors.Add($"Graph node '{nodeId}' {direction} port '{port.Id}' has a negative connection limit.");
        }
    }

    private static void ValidateEndpoint(string? nodeId, string? portId, string title, string role, bool output,
        IReadOnlyDictionary<string, NodeDto> nodesById, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || !nodesById.TryGetValue(nodeId, out var node))
        {
            errors.Add($"Connection {role} node '{nodeId}' does not exist.");
            return;
        }
        if (string.IsNullOrWhiteSpace(portId) && string.IsNullOrWhiteSpace(title))
            errors.Add($"Connection {role} port has neither a stable id nor a legacy title.");
        var ports = output ? node.OutputPorts : node.InputPorts;
        if (!string.IsNullOrWhiteSpace(portId) && ports.Count > 0
            && !ports.Any(port => port is not null && string.Equals(port.Id, portId, StringComparison.Ordinal)))
        {
            errors.Add($"Connection {role} port '{portId}' does not exist on node '{nodeId}'.");
        }
    }

    private static void ValidatePublicPorts(IEnumerable<VisualPluginPortDefinition> ports, bool input,
        IReadOnlyDictionary<string, NodeDto> nodesById, ICollection<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = new HashSet<(string NodeId, string PortId)>();
        foreach (var port in ports)
        {
            if (port.Id is null || !PortIdPattern().IsMatch(port.Id) || !ids.Add(port.Id))
                errors.Add($"Composite {(input ? "input" : "output")} id '{port.Id}' is invalid or duplicated.");
            if (string.IsNullOrWhiteSpace(port.Name))
                errors.Add($"Composite port '{port.Id}' has no display name.");
            if (!IsKnownPortType(port.PortType))
                errors.Add($"Composite port '{port.Id}' has unknown type '{port.PortType}'.");
            if (!input && port.Required)
                errors.Add($"Composite output '{port.Id}' cannot be required.");
            if (!endpoints.Add((port.NodeInstanceId, port.PortId)))
                errors.Add($"Composite {(input ? "input" : "output")} endpoint '{port.NodeInstanceId}:{port.PortId}' is exposed more than once.");
            if (string.IsNullOrWhiteSpace(port.NodeInstanceId) || !nodesById.TryGetValue(port.NodeInstanceId, out var node))
            {
                errors.Add($"Composite port '{port.Id}' references missing node '{port.NodeInstanceId}'.");
                continue;
            }

            var candidates = input ? node.InputPorts : node.OutputPorts;
            var candidate = candidates.FirstOrDefault(candidate =>
                candidate is not null && string.Equals(candidate.Id, port.PortId, StringComparison.Ordinal));
            if (candidates.Count > 0 && candidate is null)
                errors.Add($"Composite port '{port.Id}' references missing internal port '{port.PortId}'.");
            else if (candidate is not null
                     && !string.Equals(candidate.PortType, port.PortType, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Composite port '{port.Id}' type does not match internal port '{port.PortId}'.");
        }
    }

    private static bool IsKnownPortType(string value) => value is
        "Any" or "Device" or "Flow" or "Path" or "Data" or "Text" or "Number" or "Boolean" or "PatchWorkspace" or "PatchAsset";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortIdPattern();
}