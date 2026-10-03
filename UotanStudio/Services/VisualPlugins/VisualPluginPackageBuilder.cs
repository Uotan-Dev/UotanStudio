using System.Text;
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
        string author = "", bool portable = true)
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
                .Where(edge => edge.SourceInstanceId is not null && edge.TargetInstanceId is not null).ToList()
        };
        if (portable)
        {
            RemoveMachineSpecificPathDefaults(graph);
        }
        var descriptors = NodeFactoryRegistrar.Descriptors.ToDictionary(
            descriptor => descriptor.TypeKey, StringComparer.OrdinalIgnoreCase);
        var capabilities = nodes
            .Select(node => node is CompositeNodeViewModel composite
                ? composite.Package.Manifest.Capabilities.Aggregate(NodeCapability.None, (current, name) =>
                    Enum.TryParse<NodeCapability>(name, out var capability) ? current | capability : current)
                : descriptors.GetValueOrDefault(node.TypeKey)?.Capabilities ?? NodeCapability.None)
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
            var boundary = CompositeBoundaryAnalyzer.Analyze(nodes, connections);
            package.Inputs = [.. boundary.Inputs];
            package.Outputs = [.. boundary.Outputs];
            foreach (var port in package.Inputs.Where(port => portable && port.PortType == nameof(NodePortType.Path)
                         && LooksAbsolutePath(port.DefaultValue)))
            {
                port.DefaultValue = null;
                port.Required = true;
            }
        }

        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return package;
    }

    /// <summary>
    /// Builds a script-node package from inline Lua source, without an internal graph or public ports.
    /// <para>从内联 Lua 源码构建脚本节点包，不含内部图或对外端口。</para>
    /// </summary>
    /// <param name="name"><para>用户可见的包名称。</para>The user-visible package name.</param>
    /// <param name="id"><para>包标识；会经 <see cref="NormalizeId"/> 归一化。</para>The package id; normalized through <see cref="NormalizeId"/>.</param>
    /// <param name="script"><para>要在宿主沙箱中执行的 Lua 源码。</para>The Lua source executed in the host sandbox.</param>
    /// <param name="author"><para>包的作者署名。</para>The package author attribution.</param>
    /// <param name="version"><para>包版本。</para>The package version.</param>
    /// <returns><para>通过校验的脚本节点包。</para>The validated script-node package.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="script"/> is empty.
    /// <para><paramref name="name"/> 或 <paramref name="script"/> 为空。</para></exception>
    /// <exception cref="InvalidDataException">Validation reports errors, such as an over-long script.
    /// <para>校验报错，例如脚本超长。</para></exception>
    public static VisualPluginPackage CreateScript(string name, string id, string script,
        string author = "", string version = "1.0.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(script);
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("A script-node package requires a non-empty Lua script.", nameof(script));

        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.ScriptNode,
            Script = script,
            Manifest = new VisualPluginManifest
            {
                Id = NormalizeId(id),
                Name = name.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim(),
                Author = author.Trim()
            }
        };
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return package;
    }

    /// <summary>
    /// Rebuilds an installed package while retaining its manifest contract and stable public port identities.
    /// </summary>
    public static VisualPluginPackage CreateUpdated(VisualPluginPackage existing,
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections, bool portable = true)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var updated = Create(existing.Manifest.Name, existing.Manifest.Id, existing.Kind,
            nodes, connections, existing.Manifest.Author, portable);
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
        updated.BundleRoot = existing.BundleRoot;

        if (existing.Kind == VisualPluginKind.CompositeNode)
        {

            updated.Inputs = existing.Inputs.Select(ClonePort).ToList();
            updated.Outputs = existing.Outputs.Select(ClonePort).ToList();
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

    private static VisualPluginPortDefinition ClonePort(VisualPluginPortDefinition port) => new()
    {
        Id = port.Id,
        Name = port.Name,
        PortType = port.PortType,
        NodeInstanceId = port.NodeInstanceId,
        PortId = port.PortId,
        DefaultValue = port.IsSensitive ? null : port.DefaultValue,
        Required = port.Required,
        IsSensitive = port.IsSensitive,
        AdditionalBindings = port.AdditionalBindings.Select(binding => new VisualPluginPortBinding
        {
            NodeInstanceId = binding.NodeInstanceId,
            PortId = binding.PortId
        }).ToList()
    };

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