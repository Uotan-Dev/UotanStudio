using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>
/// <para>一条类型别名：把已停用的节点 TypeKey 映射到合并后的继任节点，并记录需要补上的端口默认值。</para>
/// One type alias: maps a retired node TypeKey onto its merged successor node together with the port
/// defaults that must be restored for it.
/// </summary>
/// <param name="TargetTypeKey">
/// <para>继任节点的 TypeKey。</para>
/// The successor node's TypeKey.
/// </param>
/// <param name="InitialValues">
/// <para>按端口标题给出的默认初值；只填充继任节点上存在且旧节点没有的端口。</para>
/// Default initial values by port title; only fills ports that exist on the successor but not on the retired node.
/// </param>
internal sealed record NodeTypeAliasEntry(
    string TargetTypeKey,
    IReadOnlyDictionary<string, string>? InitialValues = null)
{
    /// <summary>An alias entry without extra defaults.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NoDefaults = new Dictionary<string, string>();

    internal IReadOnlyDictionary<string, string> Defaults => InitialValues ?? NoDefaults;
}

/// <summary>
/// <para>合并节点的序列化别名表：加载携带已停用 TypeKey 的旧工程时创建继任节点并套用记录的默认值，保证旧 <c>.uotan</c> 工程照常打开。</para>
/// Serialization alias table for merged node types: loading a saved workflow that carries a retired TypeKey
/// creates the successor node with the recorded defaults, keeping old projects loadable.
/// </summary>
internal static class NodeTypeAlias
{
    private static readonly IReadOnlyDictionary<string, NodeTypeAliasEntry> Aliases =
        new Dictionary<string, NodeTypeAliasEntry>(StringComparer.Ordinal)
        {
            ["Network.GitHub.Download"] = new("Network.File.Download",
                new Dictionary<string, string> { ["Source"] = "GitHub Mirror" }),
            ["Validation.Format.Check"] = new("Validation.Format.Detect"),
            ["Config.Xml.Query"] = new("Config.Xml.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.Json.Query"] = new("Config.Json.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.Ini.Query"] = new("Config.Ini.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.AndroidProperty.Query"] = new("Config.AndroidProperty.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.FileContexts.Query"] = new("Config.FileContexts.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.FileContexts.Add"] = new("Config.FileContexts.Edit",
                new Dictionary<string, string> { ["Operation"] = "Add" }),
            ["Config.FileContexts.Delete"] = new("Config.FileContexts.Edit",
                new Dictionary<string, string> { ["Operation"] = "Delete" }),
            ["Config.FsConfig.Query"] = new("Config.FsConfig.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.FsConfig.Add"] = new("Config.FsConfig.Edit",
                new Dictionary<string, string> { ["Operation"] = "Add" }),
            ["Config.FsConfig.Delete"] = new("Config.FsConfig.Edit",
                new Dictionary<string, string> { ["Operation"] = "Delete" }),
            ["Config.Fstab.Query"] = new("Config.Fstab.Edit",
                new Dictionary<string, string> { ["Operation"] = "Query" }),
            ["Config.Fstab.Add"] = new("Config.Fstab.Edit",
                new Dictionary<string, string> { ["Operation"] = "Add" })
        };

    /// <summary>
    /// <para>查找 TypeKey 的别名；无别名时返回 null。</para>
    /// Looks up the alias for a TypeKey; null when the key has no alias.
    /// </summary>
    /// <param name="typeKey"><para>待解析的节点类型键。</para>The node type key to resolve.</param>
    /// <returns><para>命中的别名条目，或 null。</para>The matching alias entry, or null.</returns>
    internal static NodeTypeAliasEntry? Find(string typeKey)
        => Aliases.TryGetValue(typeKey, out var entry) ? entry : null;

    /// <summary>
    /// <para>把别名默认值写入继任节点；随后恢复的工程数据会按端口标题覆盖这些值。</para>
    /// Writes the alias defaults into the successor node; workflow data restored afterwards overrides
    /// these values per port title.
    /// </summary>
    /// <param name="node"><para>已创建的继任节点。</para>The created successor node.</param>
    /// <param name="entry"><para>命中的别名条目。</para>The matched alias entry.</param>
    internal static void ApplyDefaults(FlowNodeViewModel node, NodeTypeAliasEntry entry)
    {
        foreach (var (title, value) in entry.Defaults)
        {
            var input = node.Input.FirstOrDefault(port => string.Equals(port.Title, title, StringComparison.Ordinal));
            if (input is null || input.PortType == NodePortType.Flow) continue;
            input.InitialValue = WorkflowReconstruction.ConvertInputValue(input.PortType, value);
        }
    }
}