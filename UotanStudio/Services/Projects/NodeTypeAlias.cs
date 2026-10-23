using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Services.Projects;

/// <summary>
/// One type alias: maps a retired node TypeKey onto its merged successor node together with the discriminator
/// defaults, retired-to-successor port renames and retired value renames that make the migration lossless.
/// <para>一条类型别名：把已停用的节点 TypeKey 映射到合并后的继任节点，并记录判别端口默认值、端口标题改名与旧取值改名，使迁移无损。</para>
/// </summary>
/// <param name="TargetTypeKey">
/// The successor node's TypeKey.
/// <para>继任节点的 TypeKey。</para>
/// </param>
/// <param name="InitialValues">
/// Default initial values by port title; only fills ports that exist on the successor but not on the retired node.
/// <para>按端口标题给出的默认初值；只填充继任节点上存在且旧节点没有的端口。</para>
/// </param>
/// <param name="PortRenames">
/// Retired port title to successor port title. The retired discriminator title is renamed away from the
/// successor's own discriminator so the merged node keeps the operation the retired key implied.
/// <para>旧端口标题到继任端口标题的改名。旧判别端口的标题会被改走，避免覆盖继任节点自身的判别端口。</para>
/// </param>
/// <param name="ValueRenames">
/// Value corrections keyed as <c>Title=OldValue</c>, applied to a restored value before it is written.
/// <para>取值修正表，键格式为 <c>标题=旧值</c>，在写回恢复值之前应用。</para>
/// </param>
internal sealed record NodeTypeAliasEntry(
    string TargetTypeKey,
    IReadOnlyDictionary<string, string>? InitialValues = null,
    IReadOnlyDictionary<string, string>? PortRenames = null,
    IReadOnlyDictionary<string, string>? ValueRenames = null)
{
    /// <summary>The shared empty port-defaults map.<para>共享的空端口默认值映射。</para></summary>
    internal static readonly IReadOnlyDictionary<string, string> NoDefaults = new Dictionary<string, string>();

    internal IReadOnlyDictionary<string, string> Defaults => InitialValues ?? NoDefaults;
}

/// <summary>
/// Serialization alias table for merged node types: loading a saved workflow that carries a retired TypeKey
/// creates the successor node with the recorded defaults, keeping old projects loadable.
/// <para>合并节点的序列化别名表：加载携带已停用 TypeKey 的旧工程时创建继任节点并套用记录的默认值，保证旧 <c>.uotan</c> 工程照常打开。</para>
/// </summary>
internal static class NodeTypeAlias
{
    private static readonly IReadOnlyDictionary<string, string> FileModeRename =
        new Dictionary<string, string> { ["Operation"] = "File Mode" };

    private static readonly IReadOnlyDictionary<string, string> JsonRename =
        new Dictionary<string, string>
        {
            ["JSON Pointer"] = "Target",
            ["JSON Value"] = "Value",
            ["Expected Old JSON"] = "Expected Old Value"
        };

    private static readonly IReadOnlyDictionary<string, string> XmlRename =
        new Dictionary<string, string> { ["XPath"] = "Target" };

    private static readonly IReadOnlyDictionary<string, string> FstabRename =
        new Dictionary<string, string> { ["File System"] = "File System Type" };

    private static readonly IReadOnlyDictionary<string, string> InitRcRename =
        new Dictionary<string, string>
        {
            ["Replacement"] = "Value",
            ["Expected Sections"] = "Expected Matches"
        };

    private static readonly IReadOnlyDictionary<string, NodeTypeAliasEntry> Aliases =
        new Dictionary<string, NodeTypeAliasEntry>(StringComparer.Ordinal)
        {
            ["Network.GitHub.Download"] = new("Network.File.Download",
                new Dictionary<string, string> { ["Source"] = "GitHub Mirror" }),

            // Image filesystem text editing, formerly one type key per direction.
            ["Image.Text.Read"] = new("Image.Text.Edit", Operation("Read")),
            ["Image.Text.Stage"] = new("Image.Text.Edit", Operation("Write")),

            // Image file attributes, formerly split into a read node and a set node.
            ["Image.File.ReadAttributes"] = new("Image.File.Attributes", Operation("Read")),
            ["Image.File.SetAttributes"] = new("Image.File.Attributes", Operation("Set")),

            // Extended attributes: the retired Set/Delete defaulted the attribute name to user.patch,
            // the merged node defaults to security.selinux, so the retired value is restored from the project.
            ["Image.Xattr.Read"] = new("Image.Xattr.Edit", Operation("Read")),
            ["Image.Xattr.Set"] = new("Image.Xattr.Edit", Operation("Set")),
            ["Image.Xattr.Delete"] = new("Image.Xattr.Edit", Operation("Delete")),

            // Package resources.
            ["Resource.Asset.Open"] = new("Resource.Asset.Read", Operation("Select")),
            ["Resource.Asset.ReadText"] = new("Resource.Asset.Read", Operation("ReadText")),

            // Hashing.

            // Binary pattern patching.
            ["Binary.Pattern.Find"] = new("Binary.Search", Operation("Find")),
            ["Binary.Pattern.Replace"] = new("Binary.Search", Operation("Replace")),

            // Workspace files: the retired per-operation node carried its own file-mode port titled
            // "Operation", which renames onto the merged node's "File Mode" port.
            ["Workspace.File.Find"] = new("Workspace.File", Operation("Find")),
            ["Workspace.File.Exists"] = new("Workspace.File", Operation("Exists")),
            ["Workspace.File.Import"] = new("Workspace.File", Operation("Import"), FileModeRename),
            ["Workspace.File.Delete"] = new("Workspace.File", Operation("Delete")),
            ["Workspace.Text.Stage"] = new("Workspace.File", Operation("Stage"), FileModeRename),

            // Configuration syntaxes: one node with Format + Operation discriminators.
            ["Config.Text.Edit"] = new("Config.Edit", Format("Text"), null,
                new Dictionary<string, string> { ["Operation=Replace"] = "Set" }),
            ["Config.Json.Edit"] = new("Config.Edit", Format("Json"), JsonRename),
            ["Config.Json.Query"] = new("Config.Edit", Discriminator("Format", "Json", "Operation", "Query"), JsonRename),
            ["Config.Ini.Edit"] = new("Config.Edit", Format("Ini")),
            ["Config.Ini.Query"] = new("Config.Edit", Discriminator("Format", "Ini", "Operation", "Query")),
            ["Config.Xml.Edit"] = new("Config.Edit", Format("Xml"), XmlRename),
            ["Config.Xml.Query"] = new("Config.Edit", Discriminator("Format", "Xml", "Operation", "Query"), XmlRename),
            ["Config.AndroidProperty.Edit"] = new("Config.Edit", Format("Property")),
            ["Config.AndroidProperty.Query"] = new("Config.Edit", Discriminator("Format", "Property", "Operation", "Query")),
            ["Config.Fstab.Query"] = new("Config.Edit", Discriminator("Format", "Fstab", "Operation", "Query"), FstabRename),
            ["Config.Fstab.Add"] = new("Config.Edit", Discriminator("Format", "Fstab", "Operation", "Add"), FstabRename),
            ["Config.FileContexts.Edit"] = new("Config.Edit", Format("FileContexts"), null,
                new Dictionary<string, string> { ["Operation=Edit"] = "Set" }),
            ["Config.FileContexts.Query"] = new("Config.Edit", Discriminator("Format", "FileContexts", "Operation", "Query")),
            ["Config.FileContexts.Add"] = new("Config.Edit", Discriminator("Format", "FileContexts", "Operation", "Add")),
            ["Config.FileContexts.Delete"] = new("Config.Edit", Discriminator("Format", "FileContexts", "Operation", "Delete")),
            ["Config.FsConfig.Edit"] = new("Config.Edit", Format("FsConfig"), null,
                new Dictionary<string, string> { ["Operation=Edit"] = "Set" }),
            ["Config.FsConfig.Query"] = new("Config.Edit", Discriminator("Format", "FsConfig", "Operation", "Query")),
            ["Config.FsConfig.Add"] = new("Config.Edit", Discriminator("Format", "FsConfig", "Operation", "Add")),
            ["Config.FsConfig.Delete"] = new("Config.Edit", Discriminator("Format", "FsConfig", "Operation", "Delete")),
            ["Config.InitRc.Query"] = new("Config.Edit", Discriminator("Format", "InitRc", "Operation", "Query"), InitRcRename),
            ["Config.InitRc.FindReferences"] = new("Config.Edit",
                Discriminator("Format", "InitRc", "Operation", "FindReferences")),
            ["Config.InitRc.EditHeader"] = new("Config.Edit",
                Discriminator("Format", "InitRc", "Operation", "ReplaceHeader"), InitRcRename),
            ["Config.InitRc.AddSection"] = new("Config.Edit",
                Discriminator("Format", "InitRc", "Operation", "AddSection")),
            ["Config.InitRc.AddCommand"] = new("Config.Edit",
                Discriminator("Format", "InitRc", "Operation", "AddStatement"), InitRcRename),
            ["Config.InitRc.EditCommand"] = new("Config.Edit", Format("InitRc"), InitRcRename,
                new Dictionary<string, string> { ["Operation=Replace"] = "Set" }),

            // Interaction element builders: one node per element family with a Kind discriminator.
            ["UI.Field.Text"] = FieldKind("TextField"),
            ["UI.Field.Number"] = FieldKind("NumberField"),
            ["UI.Field.Boolean"] = FieldKind("BooleanField"),
            ["UI.Field.Choice"] = FieldKind("ChoiceField"),
            ["UI.Field.MultiChoice"] = FieldKind("MultiChoiceField"),
            ["UI.Field.File"] = FieldKind("FileField"),
            ["UI.Field.Directory"] = FieldKind("DirectoryField"),
            ["UI.Field.SavePath"] = FieldKind("SavePathField"),
            ["UI.Layout.Row"] = LayoutKind("Row"),
            ["UI.Layout.Column"] = LayoutKind("Column"),
            ["UI.Layout.Group"] = LayoutKind("Group"),
            ["UI.Layout.Pages"] = LayoutKind("Pages"),
            ["UI.Content.Text"] = ContentKind("Text"),
            ["UI.Content.Divider"] = ContentKind("Divider"),
            ["UI.Content.Image"] = ContentKind("Image")

        };

    /// <summary>
    /// Type keys removed from the node library. Loading a workflow that still carries one of them is refused with an
    /// explicit message instead of degrading to a placeholder, because the operation these nodes performed is no
    /// longer expressible in the node view.
    /// <para>已从节点库删除的类型键。仍携带这些键的工程在加载时被明确拒绝，而不是退化为占位符，因为这些节点原先承担的操作在节点视图里已无法表达。</para>
    /// </summary>
    internal static IReadOnlySet<string> RefusedTypeKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Validation.Applicability.Check", "Validation.Assert", "Validation.Changes.Verify",
        "Validation.Format.Detect", "Validation.File.Sha256",
        "Validation.File.Hash", "Validation.Hash.Verify", "Validation.Boolean.Assert", "Validation.Count.Assert",
        "Validation.Condition.Evaluate",
        "Firmware.Cpio.Unpack", "Firmware.Cpio.Pack", "Image.Sparse.Convert",
        "Firmware.Container.Extract", "Build.Dat.Convert", "Build.Ota.CreateDelta", "Build.Apk.Sign",
        "Device.Storage.EraseAll",
        "Device.Recovery.Clean", "Device.Ota.Sideload", "Device.State.Query",
        "Mirroring.Session.Start", "Mirroring.Session.Stop",
        "Data.Value.Constant", "Feedback.Log.Write", "Flow.Execution.Start", "Flow.Execution.End",
        "UI.Dialog.Show", "UI.Dialog.Confirm", "UI.Progress.Open", "UI.Progress.Handle",
        "UI.Progress.Update", "UI.Progress.Close", "Validation.Format.Check"
    };

    private static IReadOnlyDictionary<string, string> Operation(string value)
        => new Dictionary<string, string> { ["Operation"] = value };

    private static IReadOnlyDictionary<string, string> Mode(string value)
        => new Dictionary<string, string> { ["Mode"] = value };

    private static IReadOnlyDictionary<string, string> Format(string value)
        => new Dictionary<string, string> { ["Format"] = value };

    private static IReadOnlyDictionary<string, string> Discriminator(string firstTitle, string firstValue,
        string secondTitle, string secondValue) =>
        new Dictionary<string, string> { [firstTitle] = firstValue, [secondTitle] = secondValue };

    private static NodeTypeAliasEntry FieldKind(string kind) => KindAlias("UI.Field.Create", kind);

    private static NodeTypeAliasEntry LayoutKind(string kind) => KindAlias("UI.Layout.Create", kind);

    private static NodeTypeAliasEntry ContentKind(string kind) => KindAlias("UI.Content.Create", kind);

    private static NodeTypeAliasEntry KindAlias(string target, string kind) =>
        new(target, new Dictionary<string, string> { ["Kind"] = kind });

    /// <summary>
    /// Looks up the alias for a TypeKey; null when the key has no alias.
    /// <para>查找 TypeKey 的别名；无别名时返回 null。</para>
    /// </summary>
    /// <param name="typeKey">The node type key to resolve. <para>待解析的节点类型键。</para></param>
    /// <returns>The matching alias entry, or null. <para>命中的别名条目，或 null。</para></returns>
    internal static NodeTypeAliasEntry? Find(string typeKey)
        => Aliases.TryGetValue(typeKey, out var entry) ? entry : null;

    /// <summary>
    /// Writes the alias defaults into the successor node; workflow data restored afterwards overrides
    /// these values per port title.
    /// <para>把别名默认值写入继任节点；随后恢复的工程数据会按端口标题覆盖这些值。</para>
    /// </summary>
    /// <param name="node">The created successor node. <para>已创建的继任节点。</para></param>
    /// <param name="entry">The matched alias entry. <para>命中的别名条目。</para></param>
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