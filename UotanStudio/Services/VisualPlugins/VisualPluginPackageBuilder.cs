using System.Globalization;
using System.Text;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.VisualPlugins;

/// <summary>
/// Builds portable workflow-template and composite-node packages from a canvas graph.
/// <para>从画布图构建可移植的工作流模板与复合节点包。</para>
/// </summary>
public static class VisualPluginPackageBuilder
{
    private const string PatchAssetReadTypeKey = "Resource.Asset.Read";

    private const string PatchAssetOperationTitle = "Operation";

    private const string PatchAssetSelectOperation = "Select";

    private const string PatchAssetIdTitle = "Asset ID";

    private const string PatchAssetInputTitle = "Asset";

    /// <summary>
    /// Creates a package from the current graph and derives dependencies and public ports.
    /// <para>从当前图创建包，并推导依赖与对外端口。</para>
    /// </summary>
    /// <param name="name">The package display name. <para>包的显示名称。</para></param>
    /// <param name="id">The raw package id, normalized through <see cref="NormalizeId"/>. <para>原始包标识，会经 <see cref="NormalizeId"/> 归一化。</para></param>
    /// <param name="kind">The kind of package to build. <para>要构建的包类型。</para></param>
    /// <param name="nodes">The graph nodes to package. <para>待打包的图节点。</para></param>
    /// <param name="connections">The graph connections to package. <para>待打包的图连接。</para></param>
    /// <param name="author">The author attribution written to the manifest. <para>写入清单的作者署名。</para></param>
    /// <param name="portable">True to strip machine-specific path defaults. <para>为真时移除机器相关的路径默认值。</para></param>
    /// <param name="resourceSource">Package supplying the resources referenced by the graph, or <see langword="null"/> to export a graph without resources.
    /// <para>为图提供被引用资源的包；导出不带资源的图时为 <see langword="null"/>。</para></param>
    /// <returns>The built package that passed validation. <para>构建完成并通过校验的包。</para></returns>
    /// <exception cref="InvalidOperationException">The graph is empty or contains unavailable nodes.
    /// <para>图为空或包含不可用节点。</para></exception>
    /// <exception cref="InvalidDataException">The graph references resources without a <paramref name="resourceSource"/>, a referenced resource id is absent from it, or validation reports errors.
    /// <para>图引用资源但缺少 <paramref name="resourceSource"/>、被引用的资源标识在其中不存在，或校验报错。</para></exception>
    public static VisualPluginPackage Create(string name, string id, VisualPluginKind kind,
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections,
        string author = "", bool portable = true, VisualPluginPackage? resourceSource = null)
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
            Connections = [.. WorkflowPersistenceService.BuildConnectionDtos(nodes, connections).Where(edge => edge.SourceInstanceId is not null && edge.TargetInstanceId is not null)]
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

        package.Assets = CollectReferencedAssets(nodes, resourceSource);

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

    /// <summary>Builds a script-node package from inline Lua source, without an internal graph or public ports.
    /// <para>从内联 Lua 源码构建脚本节点包，不含内部图或对外端口。</para></summary>
    /// <param name="name">The user-visible package name.<para>用户可见的包名称。</para></param>
    /// <param name="id">The package id; normalized through <see cref="NormalizeId"/>.
    /// <para>包标识；会经 <see cref="NormalizeId"/> 归一化。</para></param>
    /// <param name="script">The Lua source stored in the package.<para>包内保存的 Lua 源码。</para></param>
    /// <param name="author">The package author attribution.<para>包的作者署名。</para></param>
    /// <param name="version">The package version.<para>包版本。</para></param>
    /// <returns>The validated script-node package.<para>通过校验的脚本节点包。</para></returns>
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
    /// <para>重建已安装的包，同时保留其清单契约与稳定的对外端口标识。</para>
    /// </summary>
    /// <param name="existing">The installed package supplying the retained metadata. <para>提供需保留元数据的已安装包。</para></param>
    /// <param name="nodes">The graph nodes to package. <para>待打包的图节点。</para></param>
    /// <param name="connections">The graph connections to package. <para>待打包的图连接。</para></param>
    /// <param name="portable">True to strip machine-specific path defaults. <para>为真时移除机器相关的路径默认值。</para></param>
    /// <returns>The rebuilt package that passed validation. <para>重建完成并通过校验的包。</para></returns>
    public static VisualPluginPackage CreateUpdated(VisualPluginPackage existing,
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections, bool portable = true)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var updated = Create(existing.Manifest.Name, existing.Manifest.Id, existing.Kind,
            nodes, connections, existing.Manifest.Author, portable, existing);
        updated.Manifest.Version = existing.Manifest.Version;
        updated.Manifest.Description = existing.Manifest.Description;
        updated.Manifest.NodeApiVersion = existing.Manifest.NodeApiVersion;
        updated.Manifest.Platforms = [.. existing.Manifest.Platforms];
        updated.Manifest.Dependencies = [.. existing.Manifest.Dependencies.Select(dependency => new VisualPluginDependency
        {
            Id = dependency.Id,
            MinimumVersion = dependency.MinimumVersion,
            MaximumVersionExclusive = dependency.MaximumVersionExclusive
        })];
        updated.Manifest.Conflicts = [.. existing.Manifest.Conflicts];
        updated.Assets = [.. existing.Assets];
        updated.BundlePath = existing.BundlePath;
        updated.BundleRoot = existing.BundleRoot;

        if (existing.Kind == VisualPluginKind.CompositeNode)
        {

            updated.Inputs = [.. existing.Inputs.Select(ClonePort)];
            updated.Outputs = [.. existing.Outputs.Select(ClonePort)];
        }

        var errors = VisualPluginValidation.Validate(updated);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return updated;
    }

    /// <summary>
    /// Creates a reverse-domain-compatible package id from arbitrary text.
    /// <para>由任意文本生成兼容反向域名风格的包标识。</para>
    /// </summary>
    /// <param name="value">The text to slugify. <para>待转换为 slug 的文本。</para></param>
    /// <returns>The normalized package id. <para>归一化后的包标识。</para></returns>
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

    /// <summary>Gathers the portable resources referenced by asset-reading nodes so the exported bundle carries them.
    /// <para>收集资源读取节点所引用的可移植资源，使导出的 bundle 一并携带。</para>
    /// </summary>
    /// <param name="nodes">The graph nodes to scan for resource references. <para>待扫描资源引用的图节点。</para></param>
    /// <param name="resourceSource">The package supplying the resources, or <see langword="null"/>. <para>提供资源的包，或 <see langword="null"/>。</para></param>
    /// <returns>Independent resource snapshots ordered by id, deduplicated ordinally. <para>按标识排序、按逐字符比较去重的独立资源快照。</para></returns>
    /// <remarks>The graph is exported in <c>Select</c> mode only by reading the asset id, because the receiving host resolves
    /// the resource through <c>PatchAsset.Resolve</c>; a resource the receiver cannot resolve is a defect reported here, not at run time.
    /// <para>只有在 <c>Select</c> 模式下图才会按资源标识读取资源，因为接收方宿主通过 <c>PatchAsset.Resolve</c> 解析资源；
    /// 接收方无法解析的资源必须在此处报错，而不是留到运行时。</para></remarks>
    private static List<VisualPluginAsset> CollectReferencedAssets(IReadOnlyList<FlowNodeViewModel> nodes,
        VisualPluginPackage? resourceSource)
    {
        var referencedIds = nodes
            .Where(node => string.Equals(node.TypeKey, PatchAssetReadTypeKey, StringComparison.Ordinal))
            .SelectMany(ReadAssetReferenceIds)
            .ToHashSet(StringComparer.Ordinal);
        if (referencedIds.Count == 0)
        {
            return [];
        }

        if (resourceSource is null)
        {
            throw new InvalidDataException(
                $"The graph references patch resources ({string.Join(", ", referencedIds.Order(StringComparer.Ordinal))}) "
                + "but no resource package was supplied for the export.");
        }

        var available = new Dictionary<string, VisualPluginAsset>(StringComparer.Ordinal);
        foreach (var asset in resourceSource.Assets)
        {
            available.TryAdd(asset.Id, asset);
        }

        var missing = referencedIds.Where(id => !available.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"The graph references patch resources that the resource package does not provide: {string.Join(", ", missing)}.");
        }

        return [.. referencedIds.Order(StringComparer.Ordinal).Select(id => available[id].Snapshot())];
    }

    private static IEnumerable<string> ReadAssetReferenceIds(FlowNodeViewModel node)
    {
        var operationPort = node.Input.FirstOrDefault(port => string.Equals(port.Title, PatchAssetOperationTitle, StringComparison.Ordinal));
        if (operationPort is not null
            && !string.Equals(ToInvariantText(operationPort.InitialValue), PatchAssetSelectOperation, StringComparison.Ordinal))
        {
            foreach (var id in ReadAssetIdsOfPort(node.Input.FirstOrDefault(
                         port => string.Equals(port.Title, PatchAssetInputTitle, StringComparison.Ordinal))))
            {
                yield return id;
            }
            yield break;
        }

        foreach (var port in node.Input.Where(port => string.Equals(port.Title, PatchAssetIdTitle, StringComparison.Ordinal)))
        {
            foreach (var id in ReadAssetIdsOfPort(port))
            {
                yield return id;
            }
        }
    }

    private static IEnumerable<string> ReadAssetIdsOfPort(NodeConnectorViewModel? port)
    {
        if (port is null || port.PortType != NodePortType.Text || port.IsSensitive)
        {
            yield break;
        }
        if (ToInvariantText(port.InitialValue) is { } text && NormalizeAssetId(text) is { } id)
        {
            yield return id;
        }
    }

    private static string? NormalizeAssetId(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? ToInvariantText(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

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
        AdditionalBindings = [.. port.AdditionalBindings.Select(binding => new VisualPluginPortBinding
        {
            NodeInstanceId = binding.NodeInstanceId,
            PortId = binding.PortId
        })]
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