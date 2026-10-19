using System.Diagnostics;
using System.Globalization;
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

    private static readonly NodeInputDeclaration AssetReadOperation =
        new("Operation", NodePortType.Text, "Select") { Choices = ["Select", "ReadText"] };

    private static readonly NodeInputDeclaration AssetReadId =
        new("Asset ID", NodePortType.Text, string.Empty)
        {
            HintResourceKey = "Node_Patch_AssetHint",
            VisibleWhen = NodePortVisibility.MayEqualAny(AssetReadOperation, "Select")
        };

    private static readonly NodeInputDeclaration XattrOperation =
        new("Operation", NodePortType.Text, "Read") { Choices = ["Read", "Set", "Delete"] };

    private static readonly NodeInputDeclaration XattrAttributeName =
        new("Attribute Name", NodePortType.Text, "security.selinux");

    private static readonly NodeInputDeclaration XattrHexValue =
        new("Hex Value", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(XattrOperation, "Set")
        };

    private static readonly NodeInputDeclaration XattrCheckOldValue =
        new("Check Old Value", NodePortType.Boolean, false) { Advanced = true, VisibleWhen = NodePortVisibility.MayEqualAny(XattrOperation, "Set", "Delete") };

    private static readonly NodeInputDeclaration XattrExpectedExists =
        new("Expected Exists", NodePortType.Boolean, true) { Advanced = true, VisibleWhen = NodePortVisibility.NotEquals(XattrOperation, "Read") };

    private static readonly NodeInputDeclaration XattrExpectedHexValue =
        new("Expected Hex Value", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.MayEqualAny(XattrOperation, "Set")(node)
                && NodePortVisibility.IsOnAll(XattrCheckOldValue, XattrExpectedExists)(node)
        };

    private static readonly NodeInputDeclaration XattrImageCapacity =
        new("Image Capacity", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            HintResourceKey = "Node_Patch_ImageCapacityHint",
            VisibleWhen = NodePortVisibility.NotEquals(XattrOperation, "Read")
        };

    private static readonly NodeInputDeclaration XattrExtractionBudget =
        new("Extraction Budget", NodePortType.Text, "68719476736")
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.NotEquals(XattrOperation, "Read")
        };

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

    private const string InitRcOperationTitle = "Operation";

    private const string InitRcEditOperationTitle = "Edit Operation";

    private const string InitRcBodyEditOperation = "EditCommand";

    private const string InitRcDeleteOperation = "Delete";

    private static readonly NodeInputDeclaration InitRcEditOperation =
        new(InitRcOperationTitle, NodePortType.Text, "Query")
        {
            Choices = ["Query", "FindReferences", "EditCommand", "EditArgument", "EditHeader", "AddSection", "AddCommand"]
        };

    /// <summary>
    /// Selects replace or delete for the body-statement edit operation. It keeps the legacy title of the former
    /// <c>Config.InitRc.EditCommand</c> node but cannot reuse plain "Operation", because port titles are stable identities
    /// and a second "Operation" input would duplicate the dispatch selector's identity on the same node.
    /// <para>为正文语句编辑选择替换或删除。沿用旧 <c>Config.InitRc.EditCommand</c> 节点的语义，但不能复用纯 "Operation"：
    /// 端口标题就是稳定标识，同一节点上第二个 "Operation" 输入会与分派选择器重复标识。</para>
    /// </summary>
    private static readonly NodeInputDeclaration InitRcEditCommandMode =
        new(InitRcEditOperationTitle, NodePortType.Text, "Replace")
        {
            Choices = Enum.GetNames<InitRcOperation>(),
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, InitRcBodyEditOperation)
        };

    private static readonly NodeInputDeclaration InitRcSection =
        new("Section Kind", NodePortType.Text, "Service")
        {
            Choices = ["Service", "Action", "Import"],
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "Query", "EditCommand", "EditArgument", "EditHeader")
        };

    private static readonly NodeInputDeclaration InitRcSelector =
        new("Selector", NodePortType.Text, string.Empty)
        {
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "Query", "EditCommand", "EditArgument", "EditHeader", "AddCommand")
        };

    private static readonly NodeInputDeclaration InitRcDirective =
        new("Directive", NodePortType.Text, string.Empty)
        {
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "Query", "EditCommand", "EditArgument")
        };

    private static readonly NodeInputDeclaration InitRcReplacement =
        new("Replacement", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => NodePortVisibility.MayEqualAny(InitRcEditOperation, "EditHeader")(node)
                || NodePortVisibility.MayEqualAny(InitRcEditOperation, InitRcBodyEditOperation)(node)
                && TextValue(node, InitRcEditCommandMode.Title) != InitRcDeleteOperation
        };

    private static readonly NodeInputDeclaration InitRcArgumentIndex =
        new("Argument Index", NodePortType.Number, 0d)
        {
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "EditArgument")
        };

    private static readonly NodeInputDeclaration InitRcArgumentValue =
        new("Value", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "EditArgument")
        };

    private static readonly NodeInputDeclaration InitRcSectionText =
        new("Section Text", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "AddSection")
        };

    private static readonly NodeInputDeclaration InitRcStatement =
        new("Statement", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "AddCommand")
        };

    private static readonly NodeInputDeclaration InitRcIdentityArguments =
        new("Identity Arguments", NodePortType.Number, 0d)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "AddCommand")
        };

    private static readonly NodeInputDeclaration InitRcEditPlacement =
        new("Placement", NodePortType.Text, "End")
        {
            Choices = ["End", "BeforeAnchor", "AfterAnchor"],
            HintResourceKey = "Node_Patch_InitRcPlacementHint",
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "AddCommand")
        };

    private static readonly NodeInputDeclaration InitRcAnchorStatement =
        new("Anchor Statement", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => NodePortVisibility.MayEqualAny(InitRcEditOperation, "AddCommand")(node)
                && NodePortVisibility.NotEquals(InitRcEditPlacement, "End")(node)
        };

    private static readonly NodeInputDeclaration InitRcExternalServiceNames =
        new("External Services", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "FindReferences")
        };

    private static readonly NodeInputDeclaration InitRcSectionCount =
        new("Expected Sections", NodePortType.Number, 1d)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "Query", "EditCommand", "EditArgument", "AddCommand")
        };

    private static readonly NodeInputDeclaration InitRcMatchCount =
        new("Expected Matches", NodePortType.Number, 1d)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "Query", "EditCommand", "EditArgument")
        };

    private static readonly NodeInputDeclaration InitRcOldValueCheck =
        new("Check Old Value", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(InitRcEditOperation, "EditCommand", "EditArgument", "EditHeader")
        };

    private static readonly NodeInputDeclaration InitRcOldValue =
        new("Expected Old Value", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.MayEqualAny(InitRcEditOperation, "EditCommand", "EditArgument", "EditHeader")(node)
                && NodePortVisibility.IsOn(InitRcOldValueCheck)(node)
        };

    private static readonly NodeOutputDeclaration InitRcTextOutput = new("Text", NodePortType.Text);
    private static readonly NodeOutputDeclaration InitRcCountOutput = new("Count", NodePortType.Number);
    private static readonly NodeOutputDeclaration InitRcChangedOutput = new("Changed", NodePortType.Boolean);
    private static readonly NodeOutputDeclaration InitRcValueOutput = new("Value", NodePortType.Text);
    private static readonly NodeOutputDeclaration InitRcValuesOutput = new("Values", NodePortType.Any);
    private static readonly NodeOutputDeclaration InitRcReferencesValidOutput = new("References Valid", NodePortType.Boolean);
    private static readonly NodeOutputDeclaration InitRcServicesOutput = new("Services", NodePortType.Any);
    private static readonly NodeOutputDeclaration InitRcMissingServicesOutput = new("Missing Services", NodePortType.Any);
    private static readonly NodeOutputDeclaration InitRcDynamicReferencesOutput = new("Dynamic References", NodePortType.Any);
    private static readonly NodeOutputDeclaration InitRcInvalidReferencesOutput = new("Invalid References", NodePortType.Any);
    private static readonly NodeOutputDeclaration InitRcAmbiguousServicesOutput = new("Ambiguous Services", NodePortType.Any);

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
        ["Image.Text.Edit"] = ImageTextEdit(),
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
        ["Image.File.Attributes"] = ImageFileAttributes(),
        ["Image.Xattr.Read"] = Set(
            [RequiredPath("Node_Patch_ImageReadXattrHint"), FileSystemChoice, ImagePathBuildProp,
                new NodeInputDeclaration("Attribute Name", NodePortType.Text, "security.selinux")],
            [DataOutput, ExistsOutput]),
        ["Image.Xattr.Set"] = ImageXattrEdit(withHexValue: true),
        ["Image.Xattr.Delete"] = ImageXattrEdit(withHexValue: false),
        ["Image.Xattr.Edit"] = Set(
            [RequiredPath("Node_Patch_ImageReadXattrHint"), FileSystemChoice, ImagePathBuildProp, XattrOperation,
                XattrAttributeName, XattrHexValue, XattrCheckOldValue, XattrExpectedExists, XattrExpectedHexValue,
                XattrImageCapacity, XattrExtractionBudget],
            [DataOutput, ExistsOutput, ChangedOutput]),
        ["Config.Edit"] = ConfigEdit(),
        ["Config.FileContexts.Edit"] = FileContexts(),
        ["Config.FsConfig.Edit"] = FsConfig(),
        ["Config.InitRc.FindReferences"] = InitRc(typeKey: "Config.InitRc.FindReferences"),
        ["Config.InitRc.EditArgument"] = InitRc(typeKey: "Config.InitRc.EditArgument"),
        ["Config.InitRc.EditHeader"] = InitRc(typeKey: "Config.InitRc.EditHeader"),
        ["Config.InitRc.AddSection"] = InitRc(typeKey: "Config.InitRc.AddSection"),
        ["Config.InitRc.AddCommand"] = InitRc(typeKey: "Config.InitRc.AddCommand"),
        ["Config.InitRc.Query"] = InitRc(typeKey: "Config.InitRc.Query"),
        ["Config.InitRc.EditCommand"] = InitRc(typeKey: "Config.InitRc.EditCommand"),
        ["Config.InitRc.Edit"] = InitRcEdit(),
        ["Config.Fstab.Edit"] = Fstab(),
        ["Binary.Pattern.Replace"] = BinaryPattern(isReplace: true),
        ["Binary.Search"] = BinarySearch(),
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
        ["Resource.Asset.Open"] = Set(
            [new NodeInputDeclaration("Asset ID", NodePortType.Text, string.Empty) { HintResourceKey = "Node_Patch_AssetHint" }],
            [AssetOutput, NameOutput, LengthOutput, HashOutput]),
        ["Resource.Asset.ReadText"] = Set(
            [AssetInput],
            [TextOutput, EncodingOutput, HashOutput]),
        ["Resource.Asset.Read"] = AssetRead(),
        ["Workspace.Asset.Stage"] = Set(
            [AssetInput, WorkspaceInput, RelativePathInput, new NodeInputDeclaration("Operation", NodePortType.Text, "Add") { Choices = PatchFileModes }],
            [WorkspaceOutput]),
        ["Workspace.File"] = WorkspaceFile(),
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
        ["File.Text.Read"] = Set(
            [RequiredPath()],
            [TextOutput, EncodingOutput, HashOutput]),
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

    private static NodePortSet AssetRead()
    {
        var asset = new NodeInputDeclaration("Asset", NodePortType.PatchAsset)
        {
            HintResourceKey = "Node_Patch_AssetHint",
            VisibleWhen = NodePortVisibility.MayEqualAny(AssetReadOperation, "ReadText")
        };
        return Set(
            [AssetReadOperation, AssetReadId, asset],
            [AssetOutput, NameOutput, LengthOutput, HashOutput, TextOutput, EncodingOutput]);
    }

    private static NodePortSet ImageFileAttributes()
    {
        var path = new NodeInputDeclaration("Path", NodePortType.Path)
        {
            Required = true,
            HintResourceKey = "Node_Patch_ImageAttributesHint"
        };
        var operation = new NodeInputDeclaration("Operation", NodePortType.Text, "Read")
        {
            Choices = ["Read", "Set"]
        };
        var system = new NodeInputDeclaration("File System", NodePortType.Text, "EXT4") { Choices = ["EXT4", "EROFS"] };
        var imagePath = new NodeInputDeclaration("Image Path", NodePortType.Text, "/build.prop");
        var uid = new NodeInputDeclaration("UID", NodePortType.Text, "0") { VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Set") };
        var gid = new NodeInputDeclaration("GID", NodePortType.Text, "0") { VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Set") };
        var mode = new NodeInputDeclaration("Mode", NodePortType.Text, "0644") { VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Set") };
        var guard = new NodeInputDeclaration("Check Old Value", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Set")
        };
        var expectedUid = new NodeInputDeclaration("Expected UID", NodePortType.Text, "0")
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(guard)(node) && NodePortVisibility.MayEqualAny(operation, "Set")(node)
        };
        var expectedGid = new NodeInputDeclaration("Expected GID", NodePortType.Text, "0")
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(guard)(node) && NodePortVisibility.MayEqualAny(operation, "Set")(node)
        };
        var expectedMode = new NodeInputDeclaration("Expected Mode", NodePortType.Text, "0644")
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(guard)(node) && NodePortVisibility.MayEqualAny(operation, "Set")(node)
        };
        var capacity = new NodeInputDeclaration("Image Capacity", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            HintResourceKey = "Node_Patch_ImageCapacityHint",
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Set")
        };
        var budget = new NodeInputDeclaration("Extraction Budget", NodePortType.Text, "68719476736")
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Set")
        };
        return Set(
            [path, operation, system, imagePath, uid, gid, mode, guard, expectedUid, expectedGid, expectedMode, capacity, budget],
            [UidOutput, GidOutput, ModeOutput, ChangedOutput]);
    }

    private static NodePortSet ImageTextEdit()
    {
        var path = new NodeInputDeclaration("Path", NodePortType.Path) { Required = true };
        var operation = new NodeInputDeclaration("Operation", NodePortType.Text, "Read")
        {
            Choices = ["Read", "Write"]
        };
        var system = new NodeInputDeclaration("File System", NodePortType.Text, "EXT4") { Choices = ["EXT4", "EROFS"] };
        var imagePath = new NodeInputDeclaration("Image Path", NodePortType.Text, "/build.prop");
        var text = new NodeInputDeclaration("Text", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Write")
        };
        var encoding = new NodeInputDeclaration("Encoding", NodePortType.Text, "utf8")
        {
            Choices = ["utf8", "utf8-bom", "utf16-le", "utf16-be"],
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Write")
        };
        var expectedHash = new NodeInputDeclaration("Expected Hash", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Write")
        };
        var capacity = new NodeInputDeclaration("Image Capacity", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            HintResourceKey = "Node_Patch_ImageCapacityHint",
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Write")
        };
        var budget = new NodeInputDeclaration("Extraction Budget", NodePortType.Text, "68719476736")
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Write")
        };
        return Set(
            [path, operation, system, imagePath, text, encoding, expectedHash, capacity, budget],
            [TextOutput, EncodingOutput, HashOutput, ChangedOutput]);
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

    private static NodePortSet InitRcEdit()
    {
        return Set(
            [MultilineText("Node_Patch_InitRcEditHint"),
                InitRcEditOperation, InitRcSection, InitRcSelector, InitRcDirective, InitRcReplacement,
                InitRcArgumentIndex, InitRcArgumentValue, InitRcSectionText, InitRcStatement,
                InitRcIdentityArguments, InitRcEditPlacement, InitRcAnchorStatement,
                InitRcExternalServiceNames, InitRcSectionCount, InitRcMatchCount,
                InitRcOldValueCheck, InitRcOldValue,
                InitRcEditCommandMode],
            [InitRcTextOutput, InitRcCountOutput, InitRcChangedOutput, InitRcValueOutput, InitRcValuesOutput,
                InitRcReferencesValidOutput, InitRcServicesOutput, InitRcMissingServicesOutput,
                InitRcDynamicReferencesOutput, InitRcInvalidReferencesOutput, InitRcAmbiguousServicesOutput]);
    }

    private static string TextValue(FlowNodeViewModel node, string title)
        => Convert.ToString(node.Input.First(input => input.Title == title).InitialValue) ?? string.Empty;

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

    private static NodePortSet BinarySearch()
    {
        var operation = new NodeInputDeclaration("Operation", NodePortType.Text, "Find")
        {
            Choices = ["Find", "Replace"]
        };
        var replacement = new NodeInputDeclaration("Replacement", NodePortType.Text, string.Empty)
        {
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Replace")
        };
        var replaceMask = new NodeInputDeclaration("Replace Mask", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Replace")
        };
        return Set(
            [RequiredPath("Node_Patch_BinaryFindHint"), operation,
                new NodeInputDeclaration("Find", NodePortType.Text, string.Empty) { HintResourceKey = "Node_Patch_BinaryFindHint" },
                new NodeInputDeclaration("Find Mask", NodePortType.Text, string.Empty) { Advanced = true },
                replacement, replaceMask,
                new NodeInputDeclaration("Start Offset", NodePortType.Text, "0") { Advanced = true },
                new NodeInputDeclaration("Search Length", NodePortType.Text, "-1") { Advanced = true },
                new NodeInputDeclaration("Alignment", NodePortType.Text, "1") { Advanced = true },
                new NodeInputDeclaration("Skip Matches", NodePortType.Text, "0") { Advanced = true },
                ExpectedMatches],
            [new NodeOutputDeclaration("Offsets", NodePortType.Any), ResultCount]);
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

    /// <summary>
    /// Unified configuration editor: one node addresses every supported configuration format through the
    /// <c>Format</c> selector and performs the requested operation through <c>Operation</c>. Address ports stay hidden
    /// until the matching format is selected, so the panel shows only the fields the chosen format understands.
    /// <para>统一配置编辑器：一个节点通过 <c>Format</c> 选择器覆盖全部受支持的配置格式，并通过 <c>Operation</c>
    /// 执行所需操作。寻址端口在选中匹配格式前保持隐藏，面板只展示所选格式能理解的字段。</para>
    /// </summary>
    private static NodePortSet WorkspaceFile()
    {
        var operation = new NodeInputDeclaration("Operation", NodePortType.Text, "Find")
        {
            Choices = ["Find", "Exists", "Import", "Delete", "Stage"]
        };
        var pattern = new NodeInputDeclaration("Pattern", NodePortType.Text, "vendor/etc/*.xml")
        {
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Find")
        };
        var relativePath = new NodeInputDeclaration("Relative Path", NodePortType.Text, string.Empty) { Required = true };
        var source = new NodeInputDeclaration("Source", NodePortType.Path, string.Empty)
        {
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Import")
        };
        var text = new NodeInputDeclaration("Text", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Stage")
        };
        var encoding = new NodeInputDeclaration("Encoding", NodePortType.Text, "utf8")
        {
            Choices = ["utf8", "utf8-bom", "utf16-le", "utf16-be"],
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Stage")
        };
        var fileMode = new NodeInputDeclaration("File Mode", NodePortType.Text, "Add")
        {
            Choices = PatchFileModes,
            VisibleWhen = node => NodePortVisibility.MayEqualAny(operation, "Import", "Stage")(node)
        };
        var expectedHash = new NodeInputDeclaration("Expected Hash", NodePortType.Text, string.Empty) { Advanced = true };
        var expectedMatches = new NodeInputDeclaration("Expected Matches", NodePortType.Number, 1d)
        {
            Advanced = true,
            VisibleWhen = NodePortVisibility.MayEqualAny(operation, "Find")
        };
        return Set(
            [WorkspaceInput, operation, pattern, relativePath, source, text, encoding, fileMode, expectedHash, expectedMatches],
            [WorkspaceOutput, ExistsOutput, PathOutput, RelativePathOutput, FilesOutput, RelativePathsOutput, ResultCount]);
    }

    private static NodePortSet ConfigEdit()
    {
        var format = new NodeInputDeclaration("Format", NodePortType.Text, "Text")
        {
            Choices = ["Text", "Json", "Ini", "Xml", "Property", "Fstab", "InitRc", "FileContexts", "FsConfig"]
        };
        var operation = new NodeInputDeclaration("Operation", NodePortType.Text, "Query")
        {
            Choices = ["Query", "Set", "Add", "Delete", "InsertBefore", "InsertAfter", "ReplaceHeader",
                "AddStatement", "AddSection", "FindReferences"]
        };
        var text = new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true };

        bool IsFormat(FlowNodeViewModel node, params string[] formats)
            => formats.Any(value => NodePortVisibility.MayEqualAny(format, value)(node));

        static string TextOf(FlowNodeViewModel node, NodeInputDeclaration port)
            => Convert.ToString(node.Input.First(input => input.Title == port.Title).Data
                ?? node.Input.First(input => input.Title == port.Title).InitialValue, CultureInfo.InvariantCulture) ?? string.Empty;

        var target = new NodeInputDeclaration("Target", NodePortType.Text, string.Empty)
        {
            HintResourceKey = "Node_Patch_ConfigTargetHint",
            VisibleWhen = node => IsFormat(node, "Json")
        };
        var section = new NodeInputDeclaration("Section", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "Ini", "InitRc")
        };
        var key = new NodeInputDeclaration("Key", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "Ini", "Property")
        };
        var namespaces = new NodeInputDeclaration("Namespaces", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            Advanced = true,
            VisibleWhen = node => IsFormat(node, "Xml")
        };
        var attributeName = new NodeInputDeclaration("Attribute Name", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "Xml")
        };
        var mountPoint = new NodeInputDeclaration("Mount Point", NodePortType.Text, "/system")
        {
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var blockDevice = new NodeInputDeclaration("Block Device", NodePortType.Text, "system")
        {
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var fileSystem = new NodeInputDeclaration("File System Type", NodePortType.Text, "ext4")
        {
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var mountOptions = new NodeInputDeclaration("Mount Options", NodePortType.Text, "ro")
        {
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var fsMgrFlags = new NodeInputDeclaration("Fs Mgr Flags", NodePortType.Text, "wait,logical")
        {
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var field = new NodeInputDeclaration("Field", NodePortType.Text, "FsMgrFlags")
        {
            Choices = Enum.GetNames<FstabField>(),
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var flagName = new NodeInputDeclaration("Flag Name", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var sectionKind = new NodeInputDeclaration("Section Kind", NodePortType.Text, "Service")
        {
            Choices = Enum.GetNames<InitRcSection>(),
            VisibleWhen = node => IsFormat(node, "InitRc")
        };
        var selector = new NodeInputDeclaration("Selector", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "InitRc")
        };
        var directive = new NodeInputDeclaration("Directive", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "InitRc")
        };
        var context = new NodeInputDeclaration("Context", NodePortType.Text, "u:object_r:vendor_file:s0")
        {
            VisibleWhen = node => IsFormat(node, "FileContexts")
                && NodePortVisibility.MayEqualAny(operation, "Add", "Set")(node)
        };
        var pathExpression = new NodeInputDeclaration("Path Expression", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "FileContexts")
        };
        var fileKind = new NodeInputDeclaration("File Kind", NodePortType.Text, "Any")
        {
            Choices = ["Any", "Regular", "Directory", "Symlink", "Block", "Character", "Pipe", "Socket"],
            VisibleWhen = node => IsFormat(node, "FileContexts")
        };
        var imagePath = new NodeInputDeclaration("Image Path", NodePortType.Text, string.Empty)
        {
            VisibleWhen = node => IsFormat(node, "FsConfig")
        };
        var uid = new NodeInputDeclaration("UID", NodePortType.Text, "0")
        {
            VisibleWhen = node => IsFormat(node, "FsConfig")
        };
        var gid = new NodeInputDeclaration("GID", NodePortType.Text, "0")
        {
            VisibleWhen = node => IsFormat(node, "FsConfig")
        };
        var mode = new NodeInputDeclaration("Mode", NodePortType.Text, "0644")
        {
            VisibleWhen = node => IsFormat(node, "FsConfig")
        };
        var value = new NodeInputDeclaration("Value", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => IsFormat(node, "Json", "Ini", "Xml", "Property", "Fstab", "FsConfig", "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "Set", "Add", "AddStatement", "ReplaceHeader")(node)
        };
        var find = new NodeInputDeclaration("Find", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => IsFormat(node, "Text")
                && NodePortVisibility.MayEqualAny(operation, "Set", "InsertBefore", "InsertAfter", "Delete")(node)
        };
        var replacement = new NodeInputDeclaration("Replacement", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => IsFormat(node, "Text")
                && NodePortVisibility.MayEqualAny(operation, "Set", "InsertBefore", "InsertAfter")(node)
        };
        var sectionText = new NodeInputDeclaration("Section Text", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => IsFormat(node, "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "AddSection")(node)
        };
        var statement = new NodeInputDeclaration("Statement", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => IsFormat(node, "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "AddStatement")(node)
        };
        var identityArguments = new NodeInputDeclaration("Identity Arguments", NodePortType.Number, 0d)
        {
            Advanced = true,
            VisibleWhen = node => IsFormat(node, "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "AddStatement")(node)
        };
        var placement = new NodeInputDeclaration("Placement", NodePortType.Text, "End")
        {
            Choices = Enum.GetNames<InitRcInsertion>(),
            VisibleWhen = node => IsFormat(node, "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "AddStatement")(node)
        };
        var anchor = new NodeInputDeclaration("Anchor Statement", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            VisibleWhen = node => IsFormat(node, "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "AddStatement")(node)
                && !string.Equals(TextOf(node, placement), "End", StringComparison.Ordinal)
        };
        var systemFilter = new NodeInputDeclaration("File System Filter", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = node => IsFormat(node, "Fstab")
        };
        var externalServices = new NodeInputDeclaration("External Services", NodePortType.Text, string.Empty)
        {
            Multiline = true,
            Advanced = true,
            VisibleWhen = node => IsFormat(node, "InitRc")
                && NodePortVisibility.MayEqualAny(operation, "FindReferences")(node)
        };
        var fstabOperation = new NodeInputDeclaration("Fstab Operation", NodePortType.Text, "SetValue")
        {
            Choices = Enum.GetNames<FstabOperation>(),
            VisibleWhen = node => IsFormat(node, "Fstab")
                && NodePortVisibility.MayEqualAny(operation, "Set", "Delete")(node)
        };
        var regex = new NodeInputDeclaration("Regex", NodePortType.Boolean, false)
        {
            VisibleWhen = node => IsFormat(node, "Text")
                && NodePortVisibility.MayEqualAny(operation, "Set", "InsertBefore", "InsertAfter", "Delete")(node)
        };
        var allowCreate = new NodeInputDeclaration("Allow Create", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = node => IsFormat(node, "Json", "Ini", "Property")
                && NodePortVisibility.MayEqualAny(operation, "Set")(node)
        };
        var checkOldValue = new NodeInputDeclaration("Check Old Value", NodePortType.Boolean, false)
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.MayEqualAny(operation, "Set", "Delete")(node)
        };
        var expectedOldValue = new NodeInputDeclaration("Expected Old Value", NodePortType.Text, string.Empty)
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(checkOldValue)(node)
                && NodePortVisibility.MayEqualAny(operation, "Set", "Delete")(node)
        };
        var expectedUid = new NodeInputDeclaration("Expected UID", NodePortType.Text, "0")
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(checkOldValue)(node) && IsFormat(node, "FsConfig")
        };
        var expectedGid = new NodeInputDeclaration("Expected GID", NodePortType.Text, "0")
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(checkOldValue)(node) && IsFormat(node, "FsConfig")
        };
        var expectedMode = new NodeInputDeclaration("Expected Mode", NodePortType.Text, "0644")
        {
            Advanced = true,
            VisibleWhen = node => NodePortVisibility.IsOn(checkOldValue)(node) && IsFormat(node, "FsConfig")
        };
        var expectedMatches = new NodeInputDeclaration("Expected Matches", NodePortType.Number, 1d)
        {
            Advanced = true
        };
        return Set(
            [format, operation, text, target, section, key, namespaces, attributeName, mountPoint, blockDevice,
                fileSystem, mountOptions, fsMgrFlags, field, flagName, sectionKind, selector, directive, context,
                pathExpression, fileKind, imagePath, uid, gid, mode, value, find, replacement, regex, allowCreate,
                checkOldValue, expectedOldValue, expectedUid, expectedGid, expectedMode, expectedMatches, sectionText,
                statement, identityArguments, placement, anchor, systemFilter, externalServices, fstabOperation],
            [TextOutput, ResultCount, ChangedOutput, QueryValueOutput, QueryValuesOutput,
                ExistsOutput, ContextOutput, UidOutput, GidOutput, ModeOutput,
                InitRcReferencesValidOutput, InitRcServicesOutput, InitRcMissingServicesOutput,
                InitRcDynamicReferencesOutput, InitRcInvalidReferencesOutput, InitRcAmbiguousServicesOutput]);
    }
}