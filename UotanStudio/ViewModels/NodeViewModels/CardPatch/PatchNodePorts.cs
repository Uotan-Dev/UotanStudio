using System.Diagnostics;
using UotanStudio.Firmware.Patching;
using UotanStudio.Services;
using UotanStudio.Services.Patching;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Declarative port table for the patch node family: each TypeKey's port set, defaults and panel rules are defined
/// exactly once here, replacing the old Create switch and the dual parameter-schema configuration.
/// <para>Patch 节点家族的声明式端口表：每个 TypeKey 的端口集合、默认值与面板规则在此唯一定义，取代旧的 Create switch 与参数 Schema 双轨配置。</para>
/// </summary>
internal static class PatchNodePorts
{
    private static readonly NodeInputDeclaration CheckOldValue =
        new("Check Old Value", NodePortType.Boolean, false) { Advanced = true };

    private static readonly NodeInputDeclaration ExpectedOldValue =
        new("Expected Old Value", NodePortType.Text, string.Empty) { Advanced = true, VisibleWhen = NodePortVisibility.IsOn(CheckOldValue) };

    private static readonly NodeInputDeclaration ExpectedUid =
        new("Expected UID", NodePortType.Text, "0") { Advanced = true, VisibleWhen = NodePortVisibility.IsOn(CheckOldValue) };

    private static readonly NodeInputDeclaration ExpectedGid =
        new("Expected GID", NodePortType.Text, "0") { Advanced = true, VisibleWhen = NodePortVisibility.IsOn(CheckOldValue) };

    private static readonly NodeInputDeclaration ExpectedMode =
        new("Expected Mode", NodePortType.Text, "0644") { Advanced = true, VisibleWhen = NodePortVisibility.IsOn(CheckOldValue) };

    private static readonly NodeInputDeclaration ExpectedExists =
        new("Expected Exists", NodePortType.Boolean, true) { Advanced = true, VisibleWhen = NodePortVisibility.IsOn(CheckOldValue) };

    private static readonly NodeInputDeclaration EditDeleteOperation =
        new("Operation", NodePortType.Text, "Set") { Choices = ["Query", "Set", "Delete"] };

    private static readonly NodeInputDeclaration ConditionOperation =
        new("Operation", NodePortType.Text, "Equals")
        {
            Choices = ["Equals", "Contains", "Regex", "VersionRange", "FileExists", "DirectoryExists"]
        };

    private const string ConditionValueTitle = "Value";

    private static readonly NodeInputDeclaration ConditionValue =
        new(ConditionValueTitle, NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = current => !NodePortVisibility.EqualsAny(current, ConditionOperation, "FileExists", "DirectoryExists")
                || NodePortVisibility.HasText(current, ConditionValueTitle)
        };

    private static readonly NodeInputDeclaration UidInput = new("UID", NodePortType.Text, "0");
    private static readonly NodeInputDeclaration GidInput = new("GID", NodePortType.Text, "0");
    private static readonly NodeInputDeclaration ModeInput = new("Mode", NodePortType.Text, "0644");

    private static readonly NodeInputDeclaration FileSystemChoice =
        new("File System", NodePortType.Text, "EXT4") { Choices = ["EXT4", "EROFS"] };

    private static readonly NodeInputDeclaration ImagePathBuildProp = new("Image Path", NodePortType.Text, "/build.prop");

    private static readonly NodeInputDeclaration TextEncoding =
        new("Encoding", NodePortType.Text, "utf8") { Choices = ["utf8", "utf8-bom", "utf16-le", "utf16-be"], Advanced = true };

    private static readonly NodeInputDeclaration ExpectedHash =
        new("Expected Hash", NodePortType.Text, string.Empty) { Advanced = true };

    private static readonly NodeInputDeclaration ImageCapacity =
        new("Image Capacity", NodePortType.Text, string.Empty) { Advanced = true, HintResourceKey = "Node_Patch_ImageCapacityHint" };

    private static readonly NodeInputDeclaration ExtractionBudget =
        new("Extraction Budget", NodePortType.Text, "68719476736") { Advanced = true };

    private static readonly NodeInputDeclaration ImageEditsInput =
        new("Image Edits", NodePortType.ImageEdits) { HintResourceKey = "Node_Patch_ImageBatchHint" };

    private static readonly NodeInputDeclaration WorkspaceInput = new("Workspace", NodePortType.PatchWorkspace) { Required = true };
    private static readonly NodeInputDeclaration RelativePathInput = new("Relative Path", NodePortType.Text, string.Empty) { Required = true };
    private static readonly NodeInputDeclaration PartitionPathInput = new("Partition Path", NodePortType.Text, "/etc/build.prop") { Required = true };

    private static readonly NodeInputDeclaration AssetInput =
        new("Asset", NodePortType.PatchAsset) { HintResourceKey = "Node_Patch_AssetHint", Required = true };

    private static readonly NodeInputDeclaration Selector = new("Selector", NodePortType.Text, string.Empty);
    private static readonly NodeInputDeclaration Directive = new("Directive", NodePortType.Text, string.Empty);

    private static readonly NodeInputDeclaration SectionKind =
        new("Section Kind", NodePortType.Text, "Service") { Choices = Enum.GetNames<InitRcSection>() };

    private static readonly NodeInputDeclaration AddCommandSectionKind =
        new("Section Kind", NodePortType.Text, "Service") { Choices = ["Service", "Action"] };

    private static readonly NodeInputDeclaration ExternalServices =
        new("External Services", NodePortType.Text, string.Empty) { Multiline = true, Advanced = true };

    private static readonly NodeInputDeclaration ExpectedSections =
        new("Expected Sections", NodePortType.Number, 1d) { Advanced = true };

    private static readonly NodeInputDeclaration ExpectedMatches =
        new("Expected Matches", NodePortType.Number, 1d) { Advanced = true };

    private static readonly NodeInputDeclaration IdentityArguments =
        new("Identity Arguments", NodePortType.Number, 0d) { Advanced = true };

    private static readonly NodeInputDeclaration Namespaces =
        new("Namespaces", NodePortType.Text, string.Empty) { Multiline = true, Advanced = true };

    private static readonly NodeInputDeclaration XPathInput = new("XPath", NodePortType.Text, string.Empty);
    private static readonly NodeInputDeclaration MountPoint = new("Mount Point", NodePortType.Text, "/system");

    private static readonly NodeInputDeclaration FileSystemFilter =
        new("File System Filter", NodePortType.Text, string.Empty) { Advanced = true };

    private static readonly NodeInputDeclaration FstabField =
        new("Field", NodePortType.Text, "FsMgrFlags") { Choices = Enum.GetNames<FstabField>() };

    private static readonly NodeInputDeclaration FstabEditOperation =
        new("Operation", NodePortType.Text, "SetValue") { Choices = Enum.GetNames<FstabOperation>() };

    private static readonly NodeInputDeclaration FstabOperationDeclaration =
        new("Operation", NodePortType.Text, "SetValue") { Choices = ["Query", "Add", .. Enum.GetNames<FstabOperation>()] };

    private static readonly NodeInputDeclaration FileContextsOperation =
        new("Operation", NodePortType.Text, "Edit") { Choices = ["Query", "Add", "Delete", "Edit"] };

    private static readonly NodeInputDeclaration FsConfigOperation =
        new("Operation", NodePortType.Text, "Edit") { Choices = ["Query", "Add", "Delete", "Edit"] };

    private static readonly NodeInputDeclaration InitRcEditCommandOperation =
        new("Operation", NodePortType.Text, "Replace") { Choices = Enum.GetNames<InitRcOperation>() };

    private static readonly string[] FormatKinds = [.. Enum.GetNames<PatchFormatKind>().Where(name => name != "Unknown")];

    private static readonly string[] PatchFileModes = Enum.GetNames<PatchFileMode>();

    private static readonly NodeOutputDeclaration WorkspaceOutput = new("Workspace", NodePortType.PatchWorkspace);
    private static readonly NodeOutputDeclaration PathOutput = new("Path", NodePortType.Path);
    private static readonly NodeOutputDeclaration RelativePathOutput = new("Relative Path", NodePortType.Text);
    private static readonly NodeOutputDeclaration ExistsOutput = new("Exists", NodePortType.Boolean);
    private static readonly NodeOutputDeclaration FilesOutput = new("Files", NodePortType.Any);
    private static readonly NodeOutputDeclaration RelativePathsOutput = new("Relative Paths", NodePortType.Any);
    private static readonly NodeOutputDeclaration TextOutput = new("Text", NodePortType.Text);
    private static readonly NodeOutputDeclaration EncodingOutput = new("Encoding", NodePortType.Text);
    private static readonly NodeOutputDeclaration HashOutput = new("Hash", NodePortType.Text);
    private static readonly NodeOutputDeclaration DataOutput = new("Data", NodePortType.Data);
    private static readonly NodeOutputDeclaration ChangedOutput = new("Changed", NodePortType.Boolean);
    private static readonly NodeOutputDeclaration ValidOutput = new("Valid", NodePortType.Boolean);
    private static readonly NodeOutputDeclaration MatchOutput = new("Match", NodePortType.Boolean);
    private static readonly NodeOutputDeclaration ResultCount = new("Count", NodePortType.Number);
    private static readonly NodeOutputDeclaration NameOutput = new("Name", NodePortType.Text);
    private static readonly NodeOutputDeclaration LengthOutput = new("Length", NodePortType.Number);
    private static readonly NodeOutputDeclaration UidOutput = new("UID", NodePortType.Text);
    private static readonly NodeOutputDeclaration GidOutput = new("GID", NodePortType.Text);
    private static readonly NodeOutputDeclaration ModeOutput = new("Mode", NodePortType.Text);
    private static readonly NodeOutputDeclaration QueryValueOutput = new("Value", NodePortType.Text);
    private static readonly NodeOutputDeclaration QueryValuesOutput = new("Values", NodePortType.Any);
    private static readonly NodeOutputDeclaration QueryCountOutput = new("Count", NodePortType.Number);
    private static readonly NodeOutputDeclaration JsonValueOutput = new("JSON Value", NodePortType.Text);
    private static readonly NodeOutputDeclaration ContextOutput = new("Context", NodePortType.Text);
    private static readonly NodeOutputDeclaration AssetOutput = new("Asset", NodePortType.PatchAsset);
    private static readonly NodeOutputDeclaration ImageEditsOutput = new("Image Edits", NodePortType.ImageEdits);
    private static readonly NodeOutputDeclaration FormatOutput = new("Format", NodePortType.Text) { ValueResourcePrefix = "Node_Choice_" };
    private static readonly NodeOutputDeclaration EvidenceOutput = new("Evidence", NodePortType.Text) { ValueResourcePrefix = "Node_Choice_" };
    private static readonly NodeOutputDeclaration DetailOutput = new("Detail", NodePortType.Text) { ValueResourcePrefix = "Node_PatchFormat_" };
    private static readonly NodeOutputDeclaration StructureValidOutput = new("Structure Valid", NodePortType.Boolean);

    private static NodeOutputDeclaration[] ResultOutputs() => [TextOutput, ResultCount, ChangedOutput];
    private static NodeOutputDeclaration[] QueryOutputs() => [QueryValueOutput, QueryValuesOutput, QueryCountOutput];

    private static NodeInputDeclaration RequiredPath(string? hint = null)
        => new("Path", NodePortType.Path) { Required = true, HintResourceKey = hint };

    private static NodeInputDeclaration MultilineText(string? hint = null)
        => new("Text", NodePortType.Text, string.Empty) { Multiline = true, HintResourceKey = hint };

    private static readonly IReadOnlyDictionary<string, NodePortSet> Sets = new Dictionary<string, NodePortSet>(StringComparer.Ordinal)
    {
        ["Image.Text.Read"] = Set(
            [RequiredPath("Node_Patch_ImageReadTextHint"), FileSystemChoice, ImagePathBuildProp],
            [TextOutput, EncodingOutput, HashOutput]),
        ["Image.Text.Stage"] = Set(
            [RequiredPath("Node_Patch_ImageWriteTextHint"), FileSystemChoice, ImagePathBuildProp,
                new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true },
                TextEncoding, ExpectedHash, ImageCapacity, ExtractionBudget],
            [ChangedOutput]),
        ["Image.Edit.CollectText"] = Set(
            [ImageEditsInput, ImagePathBuildProp,
                new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true },
                TextEncoding, ExpectedHash],
            [ImageEditsOutput, ResultCount]),
        ["Image.Edit.Apply"] = Set(
            [ImageEditsInput, RequiredPath(), FileSystemChoice, ImageCapacity, ExtractionBudget],
            [ChangedOutput]),
        ["Image.File.ReadAttributes"] = Set(
            [RequiredPath("Node_Patch_ImageAttributesHint"), FileSystemChoice, ImagePathBuildProp],
            [UidOutput, GidOutput, ModeOutput]),
        ["Image.File.SetAttributes"] = Set(
            [RequiredPath("Node_Patch_ImageAttributesHint"), FileSystemChoice, ImagePathBuildProp,
                UidInput, GidInput, ModeInput, CheckOldValue, ExpectedUid, ExpectedGid, ExpectedMode,
                ImageCapacity, ExtractionBudget],
            [ChangedOutput]),
        ["Image.Xattr.Read"] = Set(
            [RequiredPath("Node_Patch_ImageReadXattrHint"), FileSystemChoice, ImagePathBuildProp,
                new NodeInputDeclaration("Attribute Name", NodePortType.Text, "security.selinux")],
            [DataOutput, ExistsOutput]),
        ["Image.Xattr.Set"] = ImageXattrEdit(withHexValue: true),
        ["Image.Xattr.Delete"] = ImageXattrEdit(withHexValue: false),
        ["Config.FileContexts.Edit"] = FileContexts(),
        ["Config.FsConfig.Edit"] = FsConfig(),
        ["Config.InitRc.FindReferences"] = InitRc(typeKey: "Config.InitRc.FindReferences"),
        ["Config.InitRc.EditArgument"] = InitRc(typeKey: "Config.InitRc.EditArgument"),
        ["Config.InitRc.EditHeader"] = InitRc(typeKey: "Config.InitRc.EditHeader"),
        ["Config.InitRc.AddSection"] = InitRc(typeKey: "Config.InitRc.AddSection"),
        ["Config.InitRc.AddCommand"] = InitRc(typeKey: "Config.InitRc.AddCommand"),
        ["Config.InitRc.Query"] = InitRc(typeKey: "Config.InitRc.Query"),
        ["Config.InitRc.EditCommand"] = InitRc(typeKey: "Config.InitRc.EditCommand"),
        ["Config.Fstab.Edit"] = Fstab(),
        ["Binary.Pattern.Replace"] = BinaryPattern(isReplace: true),
        ["Binary.Pattern.Find"] = BinaryPattern(isReplace: false),
        ["Workspace.Partition.Resolve"] = Set(
            [WorkspaceInput,
                new NodeInputDeclaration("Partition", NodePortType.Text, "vendor")
                {
                    Choices = ["system", "vendor", "product", "system_ext", "odm", "vendor_dlkm", "odm_dlkm", "system_dlkm"]
                },
                new NodeInputDeclaration("Layout", NodePortType.Text, "PartitionDirectories")
                {
                    Choices = ["PartitionDirectories", "PartitionRoot", "SystemAsRoot"],
                    HintResourceKey = "Node_Patch_PartitionPathHint"
                },
                PartitionPathInput],
            [WorkspaceOutput, PathOutput, RelativePathOutput, ExistsOutput]),
        ["Flow.Applicability.Gate"] = Applicability(gate: true),
        ["Validation.Applicability.Check"] = Applicability(gate: false),
        ["Resource.Asset.Open"] = Set(
            [new NodeInputDeclaration("Asset ID", NodePortType.Text, string.Empty) { HintResourceKey = "Node_Patch_AssetHint" }],
            [AssetOutput, NameOutput, LengthOutput, HashOutput]),
        ["Resource.Asset.ReadText"] = Set(
            [AssetInput],
            [TextOutput, EncodingOutput, HashOutput]),
        ["Workspace.Asset.Stage"] = Set(
            [AssetInput, WorkspaceInput, RelativePathInput, new NodeInputDeclaration("Operation", NodePortType.Text, "Add") { Choices = PatchFileModes }],
            [WorkspaceOutput]),
        ["Validation.Format.Detect"] = Set(
            [RequiredPath("Node_Patch_FormatHint"),
                new NodeInputDeclaration("Expected Format", NodePortType.Text, string.Empty) { Choices = FormatKinds },
                new NodeInputDeclaration("Required Evidence", NodePortType.Text, "Structure") { Choices = ["Signature", "Structure"] }],
            [MatchOutput, FormatOutput, EvidenceOutput, DetailOutput, StructureValidOutput]),
        ["Workspace.File.Find"] = Set(
            [WorkspaceInput,
                new NodeInputDeclaration("Pattern", NodePortType.Text, "vendor/etc/*.xml"),
                ExpectedMatches],
            [WorkspaceOutput, PathOutput, FilesOutput, RelativePathOutput, RelativePathsOutput, ResultCount]),
        ["Config.Json.Edit"] = Structured(isJson: true),
        ["Config.Ini.Edit"] = Structured(isJson: false),
        ["Workspace.Directory.Open"] = Set(
            [new NodeInputDeclaration("Root", NodePortType.Path) { Directory = true, Required = true }],
            [WorkspaceOutput]),
        ["Workspace.Text.Stage"] = Set(
            [WorkspaceInput, RelativePathInput,
                new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true },
                TextEncoding,
                new NodeInputDeclaration("Operation", NodePortType.Text, "Replace") { Choices = PatchFileModes }],
            [WorkspaceOutput]),
        ["Workspace.File.Import"] = Set(
            [WorkspaceInput,
                new NodeInputDeclaration("Source", NodePortType.Path, string.Empty),
                RelativePathInput,
                new NodeInputDeclaration("Operation", NodePortType.Text, "Add") { Choices = PatchFileModes },
                ExpectedHash],
            [WorkspaceOutput]),
        ["Workspace.File.Delete"] = Set(
            [WorkspaceInput, RelativePathInput, ExpectedHash],
            [WorkspaceOutput]),
        ["Workspace.File.Exists"] = Set(
            [WorkspaceInput, RelativePathInput],
            [WorkspaceOutput, ExistsOutput]),
        ["Validation.File.Hash"] = Set(
            [RequiredPath()],
            [HashOutput]),
        ["Validation.Hash.Verify"] = Set(
            [RequiredPath(), ExpectedHash],
            [ValidOutput, HashOutput]),
        ["Validation.Count.Assert"] = Set(
            [new NodeInputDeclaration("Count", NodePortType.Number, 0d), ExpectedMatches],
            [ValidOutput]),
        ["Validation.Changes.Verify"] = Set(
            [WorkspaceInput,
                new NodeInputDeclaration("Expected Paths", NodePortType.Text, string.Empty) { Multiline = true }],
            [WorkspaceOutput, ValidOutput]),
        ["File.Text.Read"] = Set(
            [RequiredPath()],
            [TextOutput, EncodingOutput, HashOutput]),
        ["Validation.Condition.Evaluate"] = Set(
            [ConditionValue,
                new NodeInputDeclaration("Path", NodePortType.Path, string.Empty) { VisibleWhen = NodePortVisibility.MayEqualAny(ConditionOperation, "FileExists", "DirectoryExists") },
                new NodeInputDeclaration("Expected Value", NodePortType.Text, string.Empty) { VisibleWhen = NodePortVisibility.NotEqualsAny(ConditionOperation, "FileExists", "DirectoryExists") },
                new NodeInputDeclaration("Upper Version", NodePortType.Text, string.Empty) { VisibleWhen = NodePortVisibility.MayEqualAny(ConditionOperation, "VersionRange") },
                ConditionOperation],
            [MatchOutput]),
        ["Validation.Boolean.Assert"] = Set(
            [new NodeInputDeclaration("Condition", NodePortType.Boolean, false),
                new NodeInputDeclaration("Reason", NodePortType.Text, string.Empty)],
            [ValidOutput]),
        ["Config.Text.Edit"] = Set(
            [new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true },
                new NodeInputDeclaration("Find", NodePortType.Text, string.Empty) { Multiline = true },
                new NodeInputDeclaration("Replacement", NodePortType.Text, string.Empty) { Multiline = true },
                new NodeInputDeclaration("Operation", NodePortType.Text, "Replace") { Choices = Enum.GetNames<TextPatchOperation>() },
                new NodeInputDeclaration("Regex", NodePortType.Boolean, false),
                ExpectedMatches],
            ResultOutputs()),
        ["Config.AndroidProperty.Edit"] = Set(
            [new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true },
                new NodeInputDeclaration("Key", NodePortType.Text, string.Empty),
                new NodeInputDeclaration("Value", NodePortType.Text, string.Empty) { VisibleWhen = NodePortVisibility.NotEquals(EditDeleteOperation, "Delete") },
                EditDeleteOperation,
                new NodeInputDeclaration("Allow Create", NodePortType.Boolean, false) { Advanced = true, VisibleWhen = NodePortVisibility.NotEquals(EditDeleteOperation, "Delete") },
                CheckOldValue, ExpectedOldValue],
            [QueryValueOutput, ExistsOutput, TextOutput, ResultCount, ChangedOutput]),
        ["Config.Xml.Edit"] = Set(
            [MultilineText(), XPathInput, Namespaces, ExpectedMatches,
                new NodeInputDeclaration("Operation", NodePortType.Text, "SetValue") { Choices = ["Query", .. Enum.GetNames<XmlPatchOperation>()] },
                new NodeInputDeclaration("Value", NodePortType.Text, string.Empty) { Multiline = true },
                new NodeInputDeclaration("Attribute Name", NodePortType.Text, string.Empty),
                CheckOldValue, ExpectedOldValue],
            [QueryValueOutput, QueryValuesOutput, QueryCountOutput, TextOutput, ChangedOutput]),
        ["File.Text.Publish"] = Set(
            [new NodeInputDeclaration("Output Path", NodePortType.Path) { Required = true },
                new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true },
                TextEncoding],
            [new NodeOutputDeclaration("Path", NodePortType.Path)])
    };

    internal static bool TryGetSet(string typeKey, out NodePortSet set) => Sets.TryGetValue(typeKey, out set!);

    /// <summary>
    /// Attaches the declared panel flags (required, advanced, configuration semantics, visibility) to the node ports.
    /// Matching the old behavior, flags are applied only at the registrar Prepare boundary; directly constructed nodes stay bare.
    /// <para>把声明表中的面板旗标（必填、高级、配置语义、可见性）附加到节点端口。与旧行为一致，旗标只由注册表 Prepare 边界应用，直接构造的节点保持裸端口。</para>
    /// </summary>
    internal static void ApplyPanelFlags(FlowNodeViewModel node)
    {
        if (!Sets.TryGetValue(node.TypeKey, out var set))
        {
            return;
        }
        foreach (var declaration in set.Inputs)
        {
            if (node.Input.FirstOrDefault(port => port.Title == declaration.Title) is not { } port)
            {
                Debug.Fail($"Declared port '{declaration.Title}' is missing on '{node.TypeKey}'.");
                LogService.Instance.Write(LogLevel.Warn, "NodeSchema",
                    $"Declared port '{declaration.Title}' is missing on '{node.TypeKey}'.");
                continue;
            }
            port.IsRequired = declaration.Required;
            port.IsAdvanced = declaration.Advanced;
            port.IsConfiguration = declaration.IsConfiguration;
            port.IsVisibleWhen = declaration.VisibleWhen;
        }
    }

    private static NodePortSet Set(IReadOnlyList<NodeInputDeclaration> inputs, IReadOnlyList<NodeOutputDeclaration> outputs) => new(inputs, outputs);

    private static NodePortSet ImageXattrEdit(bool withHexValue)
    {
        var inputs = new List<NodeInputDeclaration>
        {
            RequiredPath("Node_Patch_ImageXattrEditHint"), FileSystemChoice, ImagePathBuildProp,
            new NodeInputDeclaration("Attribute Name", NodePortType.Text, "user.patch")
        };
        if (withHexValue)
        {
            inputs.Add(new NodeInputDeclaration("Hex Value", NodePortType.Text, string.Empty) { Multiline = true });
        }
        inputs.Add(CheckOldValue);
        inputs.Add(ExpectedExists);
        inputs.Add(new NodeInputDeclaration("Expected Hex Value", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            Advanced = true,
            VisibleWhen = NodePortVisibility.IsOnAll(CheckOldValue, ExpectedExists)
        });
        inputs.Add(ImageCapacity);
        inputs.Add(ExtractionBudget);
        return Set(inputs, [ChangedOutput]);
    }

    private static NodePortSet FileContexts()
    {
        return Set(
            [MultilineText("Node_Patch_FileContextHint"),
                new NodeInputDeclaration("Path Expression", NodePortType.Text, string.Empty),
                new NodeInputDeclaration("File Kind", NodePortType.Text, "Any")
                {
                    Choices = ["Any", "Regular", "Directory", "Symlink", "Block", "Character", "Pipe", "Socket"]
                },
                FileContextsOperation,
                new NodeInputDeclaration("Context", NodePortType.Text, "u:object_r:vendor_file:s0"),
                CheckOldValue, ExpectedOldValue],
            [ContextOutput, TextOutput, ResultCount, ChangedOutput]);
    }

    private static NodePortSet FsConfig()
    {
        return Set(
            [MultilineText("Node_Patch_FsConfigHint"),
                new NodeInputDeclaration("Image Path", NodePortType.Text, string.Empty),
                FsConfigOperation,
                UidInput, GidInput, ModeInput, CheckOldValue, ExpectedUid, ExpectedGid, ExpectedMode],
            [UidOutput, GidOutput, ModeOutput, TextOutput, ResultCount, ChangedOutput]);
    }

    private static NodePortSet InitRc(string typeKey)
    {
        if (typeKey == "Config.InitRc.FindReferences")
        {
            return Set(
                [MultilineText("Node_Patch_InitRcReferencesHint"), ExternalServices],
                [new NodeOutputDeclaration("References Valid", NodePortType.Boolean), new NodeOutputDeclaration("Services", NodePortType.Any),
                    new NodeOutputDeclaration("Missing Services", NodePortType.Any), new NodeOutputDeclaration("Dynamic References", NodePortType.Any),
                    new NodeOutputDeclaration("Invalid References", NodePortType.Any), new NodeOutputDeclaration("Ambiguous Services", NodePortType.Any),
                    ResultCount]);
        }
        if (typeKey == "Config.InitRc.AddSection")
        {
            return Set(
                [MultilineText("Node_Patch_InitRcAddSectionHint"), new NodeInputDeclaration("Section Text", NodePortType.Text, string.Empty) { Multiline = true }],
                ResultOutputs());
        }
        var hint = typeKey switch
        {
            "Config.InitRc.AddCommand" => "Node_Patch_InitRcAddHint",
            "Config.InitRc.EditHeader" => "Node_Patch_InitRcHeaderHint",
            "Config.InitRc.EditArgument" => "Node_Patch_InitRcArgumentHint",
            _ => "Node_Patch_InitRcHint"
        };
        var inputs = new List<NodeInputDeclaration>
        {
            MultilineText(hint),
            typeKey == "Config.InitRc.AddCommand" ? AddCommandSectionKind : SectionKind,
            Selector
        };
        if (typeKey == "Config.InitRc.EditHeader")
        {
            inputs.Add(new NodeInputDeclaration("Replacement", NodePortType.Text, string.Empty) { Multiline = true });
            inputs.Add(CheckOldValue);
            inputs.Add(ExpectedOldValue);
            return Set(inputs, ResultOutputs());
        }
        if (typeKey == "Config.InitRc.AddCommand")
        {
            var placement = new NodeInputDeclaration("Placement", NodePortType.Text, "End")
            {
                Choices = Enum.GetNames<InitRcInsertion>(),
                HintResourceKey = "Node_Patch_InitRcPlacementHint"
            };
            inputs.Add(new NodeInputDeclaration("Statement", NodePortType.Text, string.Empty) { Multiline = true });
            inputs.Add(IdentityArguments);
            inputs.Add(ExpectedSections);
            inputs.Add(placement);
            inputs.Add(new NodeInputDeclaration("Anchor Statement", NodePortType.Text, string.Empty)
            {
                Multiline = true,
                VisibleWhen = NodePortVisibility.NotEquals(placement, "End")
            });
            return Set(inputs, ResultOutputs());
        }
        inputs.Add(Directive);
        inputs.Add(ExpectedSections);
        inputs.Add(ExpectedMatches);
        if (typeKey == "Config.InitRc.EditArgument")
        {
            inputs.Add(new NodeInputDeclaration("Argument Index", NodePortType.Number, 0d));
            inputs.Add(new NodeInputDeclaration("Value", NodePortType.Text, string.Empty) { Multiline = true });
            inputs.Add(CheckOldValue);
            inputs.Add(ExpectedOldValue);
            return Set(inputs, ResultOutputs());
        }
        if (typeKey == "Config.InitRc.EditCommand")
        {
            inputs.Add(InitRcEditCommandOperation);
            inputs.Add(new NodeInputDeclaration("Replacement", NodePortType.Text, string.Empty)
            {
                Multiline = true,
                VisibleWhen = NodePortVisibility.NotEquals(InitRcEditCommandOperation, "Delete")
            });
            inputs.Add(CheckOldValue);
            inputs.Add(ExpectedOldValue);
            return Set(inputs, ResultOutputs());
        }
        return Set(inputs, QueryOutputs());
    }

    private static NodePortSet Fstab()
    {
        return Set(
            [MultilineText("Node_Patch_FstabHint"), MountPoint,
                new NodeInputDeclaration("Block Device", NodePortType.Text, "system"),
                new NodeInputDeclaration("File System", NodePortType.Text, "ext4"),
                new NodeInputDeclaration("Mount Options", NodePortType.Text, "ro"),
                new NodeInputDeclaration("Fs Mgr Flags", NodePortType.Text, "wait,logical"),
                FileSystemFilter, FstabField, ExpectedMatches, FstabOperationDeclaration,
                new NodeInputDeclaration("Value", NodePortType.Text, string.Empty)
                {
                    VisibleWhen = NodePortVisibility.MayEqualAny(FstabOperationDeclaration, "SetValue", "SetFlag")
                },
                new NodeInputDeclaration("Flag Name", NodePortType.Text, string.Empty)
                {
                    VisibleWhen = NodePortVisibility.MayEqualAny(FstabOperationDeclaration, "SetFlag", "DeleteFlag")
                },
                CheckOldValue, ExpectedOldValue],
            [QueryValueOutput, QueryValuesOutput, QueryCountOutput, TextOutput, ChangedOutput]);
    }

    private static NodePortSet BinaryPattern(bool isReplace)
    {
        var inputs = new List<NodeInputDeclaration>
        {
            RequiredPath(),
            new NodeInputDeclaration("Find", NodePortType.Text, string.Empty)
            {
                HintResourceKey = isReplace ? "Node_Patch_BinaryReplaceHint" : "Node_Patch_BinaryFindHint"
            },
            new NodeInputDeclaration("Find Mask", NodePortType.Text, string.Empty) { Advanced = true }
        };
        if (isReplace)
        {
            inputs.Add(new NodeInputDeclaration("Replacement", NodePortType.Text, string.Empty));
            inputs.Add(new NodeInputDeclaration("Replace Mask", NodePortType.Text, string.Empty) { Advanced = true });
        }
        inputs.Add(new NodeInputDeclaration("Start Offset", NodePortType.Text, "0") { Advanced = true });
        inputs.Add(new NodeInputDeclaration("Search Length", NodePortType.Text, "-1") { Advanced = true });
        inputs.Add(new NodeInputDeclaration("Alignment", NodePortType.Text, "1") { Advanced = true });
        inputs.Add(new NodeInputDeclaration("Skip Matches", NodePortType.Text, "0") { Advanced = true });
        inputs.Add(ExpectedMatches);
        return Set(inputs, [new NodeOutputDeclaration("Offsets", NodePortType.Any), ResultCount]);
    }

    private static NodePortSet Structured(bool isJson)
    {
        var inputs = new List<NodeInputDeclaration>
        {
            MultilineText(isJson ? "Node_Patch_JsonHint" : "Node_Patch_IniHint")
        };
        if (isJson)
        {
            inputs.Add(new NodeInputDeclaration("JSON Pointer", NodePortType.Text, string.Empty));
        }
        else
        {
            inputs.Add(new NodeInputDeclaration("Section", NodePortType.Text, string.Empty));
            inputs.Add(new NodeInputDeclaration("Key", NodePortType.Text, string.Empty));
        }
        inputs.Add(EditDeleteOperation);
        inputs.Add(new NodeInputDeclaration(isJson ? "JSON Value" : "Value", NodePortType.Text, isJson ? "null" : string.Empty)
        {
            Multiline = isJson,
            VisibleWhen = NodePortVisibility.NotEquals(EditDeleteOperation, "Delete")
        });
        inputs.Add(new NodeInputDeclaration("Allow Create", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.NotEquals(EditDeleteOperation, "Delete")
        });
        inputs.Add(CheckOldValue);
        inputs.Add(new NodeInputDeclaration(isJson ? "Expected Old JSON" : "Expected Old Value", NodePortType.Text, isJson ? "null" : string.Empty)
        {
            Multiline = isJson,
            Advanced = true,
            VisibleWhen = NodePortVisibility.IsOn(CheckOldValue)
        });
        return Set(inputs, isJson
            ? [JsonValueOutput, ExistsOutput, TextOutput, ResultCount, ChangedOutput]
            : [QueryValueOutput, ExistsOutput, TextOutput, ResultCount, ChangedOutput]);
    }

    private static NodePortSet Applicability(bool gate)
    {
        var inputs = new List<NodeInputDeclaration>
        {
            new NodeInputDeclaration("Compatible", NodePortType.Boolean, false)
            {
                HintResourceKey = gate ? "Node_Patch_ApplyGateHint" : "Node_Patch_ApplicabilityHint"
            },
            new NodeInputDeclaration("Original State Matches", NodePortType.Boolean, false),
            new NodeInputDeclaration("Desired State Matches", NodePortType.Boolean, false)
        };
        var outputs = new List<NodeOutputDeclaration>
        {
            new NodeOutputDeclaration("Applicability", NodePortType.Text) { ValueResourcePrefix = "Node_Applicability_" },
            new NodeOutputDeclaration("Should Apply", NodePortType.Boolean)
        };
        if (gate)
        {
            outputs.Add(new NodeOutputDeclaration("Apply", NodePortType.Flow));
            outputs.Add(new NodeOutputDeclaration("Skip", NodePortType.Flow));
        }
        return new NodePortSet(inputs, outputs) { CompletesFlow = !gate };
    }
}