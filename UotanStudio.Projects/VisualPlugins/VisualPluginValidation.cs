using System.Text.RegularExpressions;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Structural validation for untrusted visual-plugin JSON.
/// <para>针对不可信视觉插件 JSON 的结构性校验。</para>
/// </summary>
public static partial class VisualPluginValidation
{
    /// <summary>
    /// Maximum accepted JSON package size.
    /// <para>允许的 JSON 插件包最大体积。</para>
    /// </summary>
    public const long MaximumFileSize = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum number of graph nodes.
    /// <para>图节点数量上限。</para>
    /// </summary>
    public const int MaximumNodeCount = 1024;

    /// <summary>
    /// Maximum number of graph connections.
    /// <para>图连接数量上限。</para>
    /// </summary>
    public const int MaximumConnectionCount = 4096;

    /// <summary>
    /// Maximum total number of declared graph and public ports.
    /// <para>声明的图端口与公共端口总数上限。</para>
    /// </summary>
    public const int MaximumPortCount = 16384;

    /// <summary>
    /// Maximum accepted Lua script source length for a script-node package.
    /// <para>脚本节点插件接受的 Lua 源码长度上限。</para>
    /// </summary>
    public const int MaximumScriptLength = 512 * 1024;

    /// <summary>Validates a package without consulting host node registrations.
    /// <para>校验插件包的结构，不查询宿主节点注册信息。</para></summary>
    /// <param name="package">Package parsed from untrusted JSON, including the composites embedded in its graph nodes.
    /// <para>由不可信 JSON 解析得到的插件包，包含其图节点内嵌的复合体。</para></param>
    /// <returns>An empty list when every structural check passes; otherwise one message per problem found. Composite graphs are walked recursively up to 32 levels with cycle detection on manifest ids, and shared node, asset-count and asset-byte budgets accumulate across the whole tree; a fatal structural problem ends validation of the affected package early, and nested checks are skipped once an error has already been recorded.
    /// <para>所有结构检查均通过时返回空列表；否则每发现一个问题返回一条消息。复合图会被递归遍历至多 32 层，并按清单 ID 进行环路检测； 节点数、资源数与资源字节数的全局预算在整棵树上累计统计。致命的结构问题会提前结束受影响包的校验，且一旦已记录错误， 后续嵌套检查将被跳过。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<string> Validate(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = new List<string>();
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodeBudget = MaximumNodeCount;
        var assetBudget = VisualPluginBundle.MaximumAssetCount;
        var byteBudget = VisualPluginBundle.MaximumTotalAssetSize;
        void Visit(VisualPluginPackage current, int depth)
        {
            if (depth > 32 || current.Manifest is null || !ancestors.Add(current.Manifest.Id ?? string.Empty))
            {
                errors.Add("Composite package nesting is recursive, invalid or exceeds 32 levels.");
                return;
            }
            nodeBudget -= current.Graph?.Nodes?.Count ?? 0;
            if (nodeBudget < 0)
            {
                errors.Add("Composite packages exceed the total embedded node budget.");
                ancestors.Remove(current.Manifest.Id ?? string.Empty);
                return;
            }
            errors.AddRange(ValidateCore(current));
            if (errors.Count == 0)
            {
                assetBudget -= current.Assets.Count;
                byteBudget -= current.Assets.Sum(asset => asset.Length);
                if (assetBudget < 0 || byteBudget < 0)
                {
                    errors.Add("Embedded resources exceed the total bundle count or byte budget.");
                }
            }
            if (errors.Count == 0 && current.Graph is not null)
            {
                foreach (var node in current.Graph.Nodes)
                {
                    if (node.CompositePackage is not { } embedded)
                    {
                        continue;
                    }
                    if (node.TypeKey != embedded.NodeTypeKey || embedded.Kind != VisualPluginKind.CompositeNode)
                    {
                        errors.Add("Embedded composite identity does not match its node type.");
                        break;
                    }
                    Visit(embedded, depth + 1);
                }
            }
            ancestors.Remove(current.Manifest.Id ?? string.Empty);
        }
        Visit(package, 0);
        return errors;
    }

    private static IReadOnlyList<string> ValidateCore(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = new List<string>();
        if (package.Manifest is null || package.Graph is null || package.Inputs is null || package.Outputs is null)
        {
            return ["Plugin manifest, graph and public port collections must not be null."];
        }
        if (!Enum.IsDefined(package.Kind))
        {
            errors.Add("Unknown visual-plugin package kind.");
        }
        if (package.FormatVersion is < VisualPluginPackage.MinimumReadableFormatVersion
            or > VisualPluginPackage.CurrentFormatVersion)
        {
            errors.Add($"Unsupported visual-plugin format version {package.FormatVersion}.");
        }

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
        if (manifest.NodeApiVersion < 1)
            errors.Add($"Invalid node API version {manifest.NodeApiVersion}; the host supports node API versions 1 through {VisualPluginManifest.CurrentNodeApiVersion}.");
        else if (manifest.NodeApiVersion > VisualPluginManifest.CurrentNodeApiVersion)
            errors.Add($"Unsupported node API version {manifest.NodeApiVersion}; the host supports node API versions 1 through {VisualPluginManifest.CurrentNodeApiVersion}.");

        if (manifest.Permissions is null || manifest.Permissions.Count > 16
            || manifest.Permissions.Any(string.IsNullOrWhiteSpace)
            || manifest.Permissions.Any(name => !PluginPermissions.TryParse(name, out _))
            || manifest.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Permissions.Count)
        {
            errors.Add("Plugin permissions must name known capabilities without duplicates.");
        }
        if (manifest.Publisher is { } publisher)
        {
            if (publisher.Id is null || !PluginIdPattern().IsMatch(publisher.Id)
                || string.IsNullOrWhiteSpace(publisher.Name) || publisher.Name.Length > 128
                || !PluginSignature.IsValidFingerprint(publisher.Fingerprint)
                || !PluginSignature.IsValidPublicKey(publisher.PublicKey))
            {
                errors.Add("Plugin publisher identity, fingerprint or public key is invalid.");
            }
        }
        if (manifest.Signature is { } signature)
        {
            if (manifest.Publisher is null)
            {
                errors.Add("A signed plugin must declare its publisher identity.");
            }
            else if (string.IsNullOrEmpty(signature.Value) || !PluginSignatureAlgorithms.IsKnown(signature.Algorithm))
            {
                errors.Add("Plugin signature algorithm or value is invalid.");
            }
            else
            {
                var verification = PluginSignature.Verify(package);
                if (!verification.IsValid)
                {
                    errors.Add(verification.Error ?? "Plugin signature does not match its payload.");
                }
            }
        }

        var graph = package.Graph;
        if (graph.FormatVersion != WorkflowSnapshot.CurrentFormatVersion)
        {
            errors.Add($"Unsupported embedded workflow version {graph.FormatVersion}.");
        }
        if (graph.Nodes is null || graph.Connections is null
            || graph.Nodes.Any(node => node is null || node.InputPorts is null || node.OutputPorts is null)
            || graph.Connections.Any(connection => connection is null)
            || package.Inputs.Any(port => port is null)
            || package.Outputs.Any(port => port is null))
        {
            return ["Plugin graph collections and their elements must not be null."];
        }
        if (package.Script is null)
        {
            errors.Add("Plugin script source must not be null.");
        }
        else if (package.Kind == VisualPluginKind.ScriptNode)
        {
            if (string.IsNullOrWhiteSpace(package.Script))
            {
                errors.Add("A script-node package must provide a non-empty Lua script.");
            }
            else if (package.Script.Length > MaximumScriptLength)
            {
                errors.Add($"Plugin script exceeds {MaximumScriptLength} characters.");
            }
            if (graph.Nodes.Count > 0 || graph.Connections.Count > 0)
            {
                errors.Add("A script-node package cannot declare a graph.");
            }
            if (package.Inputs.Count > 0 || package.Outputs.Count > 0)
            {
                errors.Add("A script-node package cannot declare public ports.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(package.Script))
        {
            errors.Add("Only script-node packages can carry a Lua script.");
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
            if (port.AdditionalBindings is null || port.AdditionalBindings.Any(binding => binding is null)
                || port.AdditionalBindings.Count > MaximumPortCount || !input && port.AdditionalBindings.Count != 0)
            {
                errors.Add($"Composite port '{port.Id}' has invalid additional bindings.");
                continue;
            }
            foreach (var binding in port.GetBindings())
            {
                if (!endpoints.Add((binding.NodeInstanceId, binding.PortId)))
                    errors.Add($"Composite {(input ? "input" : "output")} endpoint '{port.NodeInstanceId}:{port.PortId}' is exposed more than once.");
                if (string.IsNullOrWhiteSpace(binding.NodeInstanceId) || !nodesById.TryGetValue(binding.NodeInstanceId, out var node))
                {
                    errors.Add($"Composite port '{port.Id}' references missing node '{port.NodeInstanceId}'.");
                    continue;
                }

                var candidates = input ? node.InputPorts : node.OutputPorts;
                var candidate = candidates.FirstOrDefault(candidate =>
                    candidate is not null && string.Equals(candidate.Id, binding.PortId, StringComparison.Ordinal));
                if (candidates.Count > 0 && candidate is null)
                    errors.Add($"Composite port '{port.Id}' references missing internal port '{port.PortId}'.");
                else if (candidate is not null
                         && !string.Equals(candidate.PortType, port.PortType, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Composite port '{port.Id}' type does not match internal port '{port.PortId}'.");
            }
        }
    }

    internal static bool IsValidPluginId(string? id) => id is not null && PluginIdPattern().IsMatch(id);

    private static bool IsKnownPortType(string value) => value is
        "Any" or "Device" or "Flow" or "Path" or "Data" or "Text" or "Number" or "Boolean" or "PatchWorkspace" or "PatchAsset"
        or "ImageEdits" or "UiComponent" or "UiForm" or "Record" or "List" or "Artifact";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortIdPattern();
}