using System.Globalization;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BoundedContexts.Patching;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Offline configuration patch nodes. Mutations produce text until explicitly published.</summary>
public sealed partial class PatchNodeViewModel : FlowNodeViewModel
{
    private PatchNodeViewModel(string typeKey) : base(typeKey, NodeCategory.Data)
    {
        TypeKey = typeKey;
        AddInput("Flow", NodePortType.Flow);
        AddOutput("Completed", NodePortType.Flow);
    }

    /// <summary>Creates a node with stable ports shared by the canvas, persistence and composites.</summary>
    public static PatchNodeViewModel Create(string typeKey)
    {
        var node = new PatchNodeViewModel(typeKey);
        switch (typeKey)
        {
            case "Patch.ImageSetXattr":
            case "Patch.ImageDeleteXattr":
                ConfigureImageXattrEdit(node);
                break;
            case "Patch.ImageReadXattr":
                node.AddInput("Path", NodePortType.Path, hint: "Node_Patch_ImageReadXattrHint");
                node.AddInput("File System", NodePortType.Text, "EXT4", choices: ["EXT4", "EROFS"]);
                node.AddInput("Image Path", NodePortType.Text, "/build.prop");
                node.AddInput("Attribute Name", NodePortType.Text, "security.selinux");
                node.AddOutput("Data", NodePortType.Data);
                node.AddOutput("Exists", NodePortType.Boolean);
                break;
            case "Patch.ImageReadAttributes":
            case "Patch.ImageSetAttributes":
                ConfigureImageAttributes(node);
                break;
            case "Patch.ImageCollectText":
            case "Patch.ImageApplyTexts":
                ConfigureImageBatch(node);
                break;
            case "Patch.ImageWriteText":
                node.AddInput("Path", NodePortType.Path, hint: "Node_Patch_ImageWriteTextHint");
                node.AddInput("File System", NodePortType.Text, "EXT4", choices: ["EXT4", "EROFS"]);
                node.AddInput("Image Path", NodePortType.Text, "/build.prop");
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Encoding", NodePortType.Text, "utf8", choices: ["utf8", "utf8-bom", "utf16-le", "utf16-be"]);
                node.AddInput("Expected Hash", NodePortType.Text, string.Empty);
                node.AddInput("Image Capacity", NodePortType.Text, "", hint: "Node_Patch_ImageCapacityHint");
                node.AddInput("Extraction Budget", NodePortType.Text, "68719476736");
                node.AddOutput("Changed", NodePortType.Boolean);
                break;
            case "Patch.ImageReadText":
                node.AddInput("Path", NodePortType.Path, hint: "Node_Patch_ImageReadTextHint");
                node.AddInput("File System", NodePortType.Text, "EXT4", choices: ["EXT4", "EROFS"]);
                node.AddInput("Image Path", NodePortType.Text, "/build.prop");
                node.AddOutput("Text", NodePortType.Text);
                node.AddOutput("Encoding", NodePortType.Text);
                node.AddOutput("Hash", NodePortType.Text);
                break;
            case "Patch.FileContextQuery":
            case "Patch.FileContextAdd":
            case "Patch.FileContextDelete":
            case "Patch.FileContextEdit":
                ConfigureFileContextNode(node);
                break;
            case "Patch.FsConfigQuery":
            case "Patch.FsConfigAdd":
            case "Patch.FsConfigDelete":
            case "Patch.FsConfigEdit":
                ConfigureFsConfigNode(node);
                break;
            case "Patch.InitRcQuery":
            case "Patch.InitRcReferences":
            case "Patch.InitRcArgumentEdit":
            case "Patch.InitRcHeaderEdit":
            case "Patch.InitRcAddSection":
            case "Patch.InitRcAdd":
            case "Patch.InitRcEdit":
                ConfigureInitRcNode(node);
                break;
            case "Patch.FstabQuery":
            case "Patch.FstabAdd":
            case "Patch.FstabEdit":
                ConfigureFstabNode(node);
                break;
            case "Patch.Applicability":
            case "Patch.ApplyGate":
                node.AddInput("Compatible", NodePortType.Boolean, false,
                    hint: typeKey == "Patch.ApplyGate" ? "Node_Patch_ApplyGateHint" : "Node_Patch_ApplicabilityHint");
                node.AddInput("Original State Matches", NodePortType.Boolean, false);
                node.AddInput("Desired State Matches", NodePortType.Boolean, false);
                node.AddOutput("Applicability", NodePortType.Text, "Node_Applicability_");
                node.AddOutput("Should Apply", NodePortType.Boolean);
                if (typeKey == "Patch.ApplyGate")
                {
                    node.Output.Remove(node.Output.Single(port => port.Title == "Completed"));
                    node.AddOutput("Apply", NodePortType.Flow);
                    node.AddOutput("Skip", NodePortType.Flow);
                }
                break;
            case "Patch.Asset":
            case "Patch.AssetText":
            case "Patch.ImportAsset":
                ConfigureAssetNode(node);
                break;
            case "Patch.JsonQuery":
            case "Patch.JsonEdit":
            case "Patch.IniQuery":
            case "Patch.IniEdit":
                ConfigureStructuredNode(node);
                break;
            case "Patch.FindFiles":
                node.AddInput("Root", NodePortType.Path, directory: true);
                node.AddInput("Pattern", NodePortType.Text, "vendor/etc/*.xml");
                node.AddInput("Expected Matches", NodePortType.Number, 1d);
                node.AddOutput("Path", NodePortType.Path);
                node.AddOutput("Files", NodePortType.Any);
                node.AddOutput("Count", NodePortType.Number);
                break;
            case "Patch.ReadText":
                node.AddInput("Path", NodePortType.Path);
                node.AddOutput("Text", NodePortType.Text);
                node.AddOutput("Encoding", NodePortType.Text);
                node.AddOutput("Hash", NodePortType.Text);
                break;
            case "Patch.WriteText":
                node.AddInput("Output Path", NodePortType.Path);
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Encoding", NodePortType.Text, "utf8", choices: ["utf8", "utf8-bom", "utf16-le", "utf16-be"]);
                node.AddOutput("Path", NodePortType.Path);
                break;
            case "Patch.Condition":
                node.AddInput("Value", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Expected Value", NodePortType.Text, string.Empty);
                node.AddInput("Upper Version", NodePortType.Text, string.Empty);
                node.AddInput("Operation", NodePortType.Text, "Equals", choices:
                    ["Equals", "Contains", "Regex", "VersionRange", "FileExists", "DirectoryExists"]);
                node.AddOutput("Match", NodePortType.Boolean);
                break;
            case "Patch.Assert":
                node.AddInput("Condition", NodePortType.Boolean, false);
                node.AddInput("Reason", NodePortType.Text, string.Empty);
                node.AddOutput("Valid", NodePortType.Boolean);
                break;
            case "Patch.Text":
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Find", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Replacement", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Operation", NodePortType.Text, "Replace", choices: Enum.GetNames<TextPatchOperation>());
                node.AddInput("Regex", NodePortType.Boolean, false);
                node.AddInput("Expected Matches", NodePortType.Number, 1d);
                node.AddResultOutputs();
                break;
            case "Patch.PropertyRead":
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Key", NodePortType.Text, string.Empty);
                node.AddOutput("Value", NodePortType.Text);
                node.AddOutput("Exists", NodePortType.Boolean);
                break;
            case "Patch.PropertyEdit":
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Key", NodePortType.Text, string.Empty);
                node.AddInput("Value", NodePortType.Text, string.Empty);
                node.AddInput("Operation", NodePortType.Text, "Set", choices: ["Set", "Delete"]);
                node.AddInput("Allow Create", NodePortType.Boolean, false);
                node.AddOldValueInputs();
                node.AddResultOutputs();
                break;
            case "Patch.XmlQuery":
            case "Patch.XmlEdit":
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("XPath", NodePortType.Text, string.Empty);
                node.AddInput("Namespaces", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Expected Matches", NodePortType.Number, 1d);
                if (typeKey == "Patch.XmlEdit")
                {
                    node.AddInput("Operation", NodePortType.Text, "SetValue", choices: Enum.GetNames<XmlPatchOperation>());
                    node.AddInput("Value", NodePortType.Text, string.Empty, multiline: true);
                    node.AddInput("Attribute Name", NodePortType.Text, string.Empty);
                    node.AddOldValueInputs();
                    node.AddResultOutputs();
                }
                else
                {
                    node.AddOutput("Value", NodePortType.Text);
                    node.AddOutput("Values", NodePortType.Any);
                    node.AddOutput("Count", NodePortType.Number);
                }
                break;
            default:
                ConfigureFileNode(node);
                break;
        }
        return node;
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        var token = context.CancellationToken;
        token.ThrowIfCancellationRequested();
        switch (TypeKey)
        {
            case "Patch.ImageSetXattr":
            case "Patch.ImageDeleteXattr":
                await ExecuteImageXattrEditAsync(context).ConfigureAwait(false);
                break;
            case "Patch.ImageReadXattr":
                var xattr = await GetRun(context).ReadImageXattrAsync(Text("Path"), Text("File System"), Text("Image Path"),
                    Text("Attribute Name"), token).ConfigureAwait(false);
                WriteOutput("Data", xattr ?? []);
                WriteOutput("Exists", xattr is not null);
                break;
            case "Patch.ImageReadAttributes":
            case "Patch.ImageSetAttributes":
                await ExecuteImageAttributesAsync(context).ConfigureAwait(false);
                break;
            case "Patch.ImageCollectText":
            case "Patch.ImageApplyTexts":
                await ExecuteImageBatchAsync(context).ConfigureAwait(false);
                break;
            case "Patch.ImageWriteText":
                WriteOutput("Changed", await GetRun(context).StageImageTextAsync(Text("Path"), Text("File System"),
                    Text("Image Path"), Text("Text"), Text("Encoding"), Text("Expected Hash"),
                    PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), token).ConfigureAwait(false));
                break;
            case "Patch.ImageReadText":
                var imageText = await GetRun(context).ReadImageTextAsync(Text("Path"), Text("File System"), Text("Image Path"), token).ConfigureAwait(false);
                WriteOutput("Text", imageText.Text);
                WriteOutput("Encoding", imageText.Encoding);
                WriteOutput("Hash", imageText.Sha256);
                break;
            case "Patch.FileContextQuery":
            case "Patch.FileContextAdd":
            case "Patch.FileContextDelete":
            case "Patch.FileContextEdit":
                ExecuteFileContextNode(token);
                break;
            case "Patch.FsConfigQuery":
            case "Patch.FsConfigAdd":
            case "Patch.FsConfigDelete":
            case "Patch.FsConfigEdit":
                ExecuteFsConfigNode(token);
                break;
            case "Patch.InitRcQuery":
            case "Patch.InitRcReferences":
            case "Patch.InitRcArgumentEdit":
            case "Patch.InitRcHeaderEdit":
            case "Patch.InitRcAddSection":
            case "Patch.InitRcAdd":
            case "Patch.InitRcEdit":
                ExecuteInitRcNode(token);
                break;
            case "Patch.FstabQuery":
            case "Patch.FstabAdd":
            case "Patch.FstabEdit":
                ExecuteFstabNode(token);
                break;
            case "Patch.Applicability":
            case "Patch.ApplyGate":
                var applicability = PatchApplicabilityCheck.Evaluate(Boolean("Compatible"),
                    Boolean("Original State Matches"), Boolean("Desired State Matches"));
                WriteOutput("Applicability", applicability.ToString());
                WriteOutput("Should Apply", applicability == PatchApplicability.Applicable);
                WriteOutput("Apply", applicability == PatchApplicability.Applicable);
                WriteOutput("Skip", applicability != PatchApplicability.Applicable);
                GetRun(context).RecordApplicability(applicability);
                if (applicability == PatchApplicability.Conflict)
                {
                    throw new PatchApplicabilityConflictException(Services.LanguageService.Instance.GetString("Node_Patch_ApplicabilityConflict"));
                }
                break;
            case "Patch.Asset":
            case "Patch.AssetText":
            case "Patch.ImportAsset":
                await ExecuteAssetNodeAsync(context).ConfigureAwait(false);
                break;
            case "Patch.JsonQuery":
            case "Patch.JsonEdit":
            case "Patch.IniQuery":
            case "Patch.IniEdit":
                ExecuteStructuredNode(token);
                break;
            case "Patch.FindFiles":
                var files = await GetRun(context).FindAsync(Text("Root"), Text("Pattern"), Count(), token).ConfigureAwait(false);
                WriteOutput("Files", files);
                WriteOutput("Path", files.Count == 1 ? files[0] : null);
                WriteOutput("Count", files.Count);
                break;
            case "Patch.ReadText":
                var document = await GetRun(context).ReadTextAsync(Text("Path"), token).ConfigureAwait(false);
                WriteOutput("Text", document.Text);
                WriteOutput("Encoding", document.Encoding);
                WriteOutput("Hash", document.Sha256);
                break;
            case "Patch.WriteText":
                await GetRun(context).StageTextAsync(Text("Output Path"), Text("Text"), Text("Encoding"), PatchFileMode.Add, token).ConfigureAwait(false);
                WriteOutput("Path", Path.GetFullPath(Text("Output Path")));
                break;
            case "Patch.Condition":
                WriteOutput("Match", await EvaluateConditionAsync(context).ConfigureAwait(false));
                break;
            case "Patch.Assert":
                if (!Boolean("Condition"))
                {
                    throw new InvalidDataException(string.IsNullOrWhiteSpace(Text("Reason"))
                        ? Services.LanguageService.Instance.GetString("Node_Patch_AssertionFailed") : Text("Reason"));
                }
                WriteOutput("Valid", true);
                break;
            case "Patch.Text":
                SetResult(TextPatch.Apply(Text("Text"), Text("Find"), Text("Replacement"),
                    Operation<TextPatchOperation>(), Boolean("Regex"), Count(), token));
                break;
            case "Patch.PropertyRead":
                var value = AndroidPropertyPatch.Read(Text("Text"), Text("Key"));
                WriteOutput("Value", value ?? string.Empty);
                WriteOutput("Exists", value is not null);
                break;
            case "Patch.PropertyEdit":
                var operation = Text("Operation");
                if (operation is not "Set" and not "Delete")
                {
                    throw new InvalidDataException("Invalid property operation.");
                }
                SetResult(AndroidPropertyPatch.Apply(Text("Text"), Text("Key"), Text("Value"), operation == "Delete",
                    Boolean("Check Old Value"), Text("Expected Old Value"), Boolean("Allow Create")));
                break;
            case "Patch.XmlQuery":
                var values = XmlPatch.Query(Text("Text"), Text("XPath"), Text("Namespaces"), Count());
                WriteOutput("Values", values);
                WriteOutput("Value", values.Count == 1 ? values[0] : null);
                WriteOutput("Count", values.Count);
                break;
            case "Patch.XmlEdit":
                SetResult(XmlPatch.Apply(Text("Text"), Text("XPath"), Text("Namespaces"), Operation<XmlPatchOperation>(),
                    Text("Value"), Text("Attribute Name"), Count(), Boolean("Check Old Value"), Text("Expected Old Value")));
                break;
            default:
                await ExecuteFileNodeAsync(context).ConfigureAwait(false);
                break;
        }
        token.ThrowIfCancellationRequested();
        WriteOutput("Completed", true);
    }

    private async Task<bool> EvaluateConditionAsync(INodeExecutionContext context)
    {
        var value = Text("Value");
        var expected = Text("Expected Value");
        if (value.Length > TextPatch.MaxCharacters || expected.Length > 16_384)
        {
            throw new InvalidDataException("Condition input exceeds the size limit.");
        }
        return Text("Operation") switch
        {
            "Equals" => string.Equals(value, expected, StringComparison.Ordinal),
            "Contains" => value.Contains(expected, StringComparison.Ordinal),
            "Regex" => Regex.IsMatch(value, expected, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "VersionRange" => ParseVersion(value) >= ParseVersion(expected) && ParseVersion(value) <= ParseVersion(Text("Upper Version")),
            "FileExists" => await GetRun(context).ExistsAsync(value, context.CancellationToken).ConfigureAwait(false),
            "DirectoryExists" => await GetRun(context).DirectoryExistsAsync(value, context.CancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("Invalid condition operation.")
        };
    }

    private string Text(string title) => Convert.ToString(ReadInput(title), CultureInfo.InvariantCulture) ?? string.Empty;

    private static Version ParseVersion(string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return new Version(major, 0, 0, 0);
        }
        var version = Version.Parse(text);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }
    private bool Boolean(string title) => ReadInput(title) is bool value ? value : bool.Parse(Text(title));

    private int Count()
    {
        var value = Convert.ToDouble(ReadInput("Expected Matches"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 0 or > 100_000)
        {
            throw new InvalidDataException("Expected matches must be an integer between 0 and 100000.");
        }
        return (int)value;
    }

    private T Operation<T>() where T : struct, Enum
    {
        if (!Enum.TryParse<T>(Text("Operation"), out var result) || !Enum.IsDefined(result))
        {
            throw new InvalidDataException("Invalid patch operation.");
        }
        return result;
    }

    private void SetResult(TextPatchResult result)
    {
        WriteOutput("Text", result.Text);
        WriteOutput("Count", result.Matches);
        WriteOutput("Changed", result.Changed);
    }

    private void AddOldValueInputs()
    {
        AddInput("Check Old Value", NodePortType.Boolean, false);
        AddInput("Expected Old Value", NodePortType.Text, string.Empty);
    }

    private void AddResultOutputs()
    {
        AddOutput("Text", NodePortType.Text);
        AddOutput("Count", NodePortType.Number);
        AddOutput("Changed", NodePortType.Boolean);
    }

    private long PositiveImageBytes(string title)
    {
        if (!long.TryParse(Text(title), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
        {
            throw new InvalidDataException(Services.LanguageService.Instance.GetString("Node_Patch_InvalidImageBytes"));
        }
        return bytes;
    }

    private void AddInput(string title, NodePortType type, object? initial = null, bool multiline = false,
        bool directory = false, string[]? choices = null, string? hint = null)
    {
        Input.Add(new NodeConnectorViewModel
        {
            Title = title,
            PortId = NodeConnectorViewModel.CreateCompatibilityId(title),
            PortType = type,
            IsInput = true,
            MaxConnections = 1,
            InitialValue = initial,
            IsMultiline = multiline,
            IsDirectoryPath = directory,
            HintResourceKey = hint,
            OutputExtension = "txt",
            SuggestedOutputName = "patched.txt",
            Choices = choices?.Select(value => new NodePortChoice(value)).ToArray() ?? []
        });
    }

    private void AddOutput(string title, NodePortType type, string? valueResourcePrefix = null)
    {
        Output.Add(new NodeConnectorViewModel
        {
            Title = title,
            PortId = NodeConnectorViewModel.CreateCompatibilityId(title),
            PortType = type,
            ValueResourcePrefix = valueResourcePrefix
        });
    }
}
