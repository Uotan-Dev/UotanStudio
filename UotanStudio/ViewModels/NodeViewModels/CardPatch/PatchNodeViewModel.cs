using System.Globalization;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.Patching;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Offline configuration patch nodes. Mutations produce text until explicitly published.</summary>
public sealed partial class PatchNodeViewModel : FlowNodeViewModel
{
    private PatchNodeViewModel(string typeKey, bool completesFlow = true) : base(typeKey, NodeCategory.Data)
    {
        TypeKey = typeKey;
        AddInput("Flow", NodePortType.Flow);
        if (completesFlow)
        {
            AddOutput("Completed", NodePortType.Flow);
        }
    }

    /// <summary>Creates a node with stable ports shared by the canvas, persistence and composites.</summary>
    public static PatchNodeViewModel Create(string typeKey)
    {
        if (!PatchNodePorts.TryGetSet(typeKey, out var ports))
        {
            throw new ArgumentException("Unknown patch node type.", nameof(typeKey));
        }
        var node = new PatchNodeViewModel(typeKey, ports.CompletesFlow);
        foreach (var declaration in ports.Inputs)
        {
            node.AddInput(declaration);
        }
        foreach (var declaration in ports.Outputs)
        {
            node.AddOutput(declaration);
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
            case "Image.Xattr.Set":
            case "Image.Xattr.Delete":
                await ExecuteImageXattrEditAsync(context).ConfigureAwait(false);
                break;
            case "Image.Xattr.Read":
                var xattr = await GetRun(context).ReadImageXattrAsync(Text("Path"), Text("File System"), Text("Image Path"),
                    Text("Attribute Name"), token).ConfigureAwait(false);
                WriteOutput("Data", xattr ?? []);
                WriteOutput("Exists", xattr is not null);
                break;
            case "Image.File.ReadAttributes":
            case "Image.File.SetAttributes":
                await ExecuteImageAttributesAsync(context).ConfigureAwait(false);
                break;
            case "Image.Edit.CollectText":
            case "Image.Edit.Apply":
                await ExecuteImageBatchAsync(context).ConfigureAwait(false);
                break;
            case "Image.Text.Stage":
                WriteOutput("Changed", await GetRun(context).StageImageTextAsync(Text("Path"), Text("File System"),
                    Text("Image Path"), Text("Text"), Text("Encoding"), Text("Expected Hash"),
                    PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), token).ConfigureAwait(false));
                break;
            case "Image.Text.Read":
                var imageText = await GetRun(context).ReadImageTextAsync(Text("Path"), Text("File System"), Text("Image Path"), token).ConfigureAwait(false);
                WriteOutput("Text", imageText.Text);
                WriteOutput("Encoding", imageText.Encoding);
                WriteOutput("Hash", imageText.Sha256);
                break;
            case "Config.FileContexts.Edit":
                ExecuteFileContextNode(token);
                break;
            case "Config.FsConfig.Edit":
                ExecuteFsConfigNode(token);
                break;
            case "Config.InitRc.Query":
            case "Config.InitRc.FindReferences":
            case "Config.InitRc.EditArgument":
            case "Config.InitRc.EditHeader":
            case "Config.InitRc.AddSection":
            case "Config.InitRc.AddCommand":
            case "Config.InitRc.EditCommand":
                ExecuteInitRcNode(token);
                break;
            case "Config.Fstab.Edit":
                ExecuteFstabNode(token);
                break;
            case "Validation.Applicability.Check":
            case "Flow.Applicability.Gate":
                ExecuteApplicabilityNode(context);
                break;
            case "Resource.Asset.Open":
            case "Resource.Asset.ReadText":
            case "Workspace.Asset.Stage":
                await ExecuteAssetNodeAsync(context).ConfigureAwait(false);
                break;
            case "Config.Json.Edit":
            case "Config.Ini.Edit":
                ExecuteStructuredNode(token);
                break;
            case "File.Text.Read":
                var document = await GetRun(context).ReadTextAsync(Text("Path"), token).ConfigureAwait(false);
                WriteOutput("Text", document.Text);
                WriteOutput("Encoding", document.Encoding);
                WriteOutput("Hash", document.Sha256);
                break;
            case "File.Text.Publish":
                await GetRun(context).StageTextAsync(Text("Output Path"), Text("Text"), Text("Encoding"), PatchFileMode.Add, token).ConfigureAwait(false);
                WriteOutput("Path", Path.GetFullPath(Text("Output Path")));
                break;
            case "Validation.Condition.Evaluate":
                WriteOutput("Match", await EvaluateConditionAsync(context).ConfigureAwait(false));
                break;
            case "Validation.Boolean.Assert":
                if (!Boolean("Condition"))
                {
                    throw new InvalidDataException(string.IsNullOrWhiteSpace(Text("Reason"))
                        ? Services.LanguageService.Instance.GetString("Node_Patch_AssertionFailed") : Text("Reason"));
                }
                WriteOutput("Valid", true);
                break;
            case "Config.Text.Edit":
                SetResult(TextPatch.Apply(Text("Text"), Text("Find"), Text("Replacement"),
                    Operation<TextPatchOperation>(), Boolean("Regex"), Count(), token));
                break;
            case "Config.AndroidProperty.Edit":
                var propertyOperation = Text("Operation");
                if (propertyOperation == "Query")
                {
                    var queryValue = AndroidPropertyPatch.Read(Text("Text"), Text("Key"));
                    WriteOutput("Value", queryValue ?? string.Empty);
                    WriteOutput("Exists", queryValue is not null);
                    break;
                }
                if (propertyOperation is not "Set" and not "Delete")
                {
                    throw new InvalidDataException("Invalid property operation.");
                }
                SetResult(AndroidPropertyPatch.Apply(Text("Text"), Text("Key"), Text("Value"), propertyOperation == "Delete",
                    Boolean("Check Old Value"), Text("Expected Old Value"), Boolean("Allow Create")));
                break;
            case "Config.Xml.Edit":
                if (Text("Operation") == "Query")
                {
                    var values = XmlPatch.Query(Text("Text"), Text("XPath"), Text("Namespaces"), Count());
                    WriteOutput("Values", values);
                    WriteOutput("Value", values.Count == 1 ? values[0] : null);
                    WriteOutput("Count", values.Count);
                    break;
                }
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

    /// <summary>
    /// Shared applicability evaluation for Validation.Applicability.Check and Flow.Applicability.Gate: evaluates,
    /// records, and turns Conflict into an exception. The two TypeKeys differ only in port layout
    /// (Gate carries the branch via Apply/Skip Flow outputs).
    /// <para>Validation.Applicability.Check 与 Flow.Applicability.Gate 共用的适用性判定：求值、记录并把 Conflict 转为异常。
    /// 两个 TypeKey 的差异只在端口布局（Gate 以 Apply/Skip Flow 输出承载分支）。</para>
    /// </summary>
    private void ExecuteApplicabilityNode(INodeExecutionContext context)
    {
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
    }

    private async Task<bool> EvaluateConditionAsync(INodeExecutionContext context)
    {
        var value = Text("Value");
        var pathInput = Input.First(port => port.Title == "Path");
        var path = pathInput.HasRuntimeSource || !string.IsNullOrWhiteSpace(Text("Path")) ? Text("Path") : value;
        var expected = Text("Expected Value");
        if (value.Length > TextPatch.MaxCharacters || path.Length > TextPatch.MaxCharacters || expected.Length > 16_384)
        {
            throw new InvalidDataException("Condition input exceeds the size limit.");
        }
        return Text("Operation") switch
        {
            "Equals" => string.Equals(value, expected, StringComparison.Ordinal),
            "Contains" => value.Contains(expected, StringComparison.Ordinal),
            "Regex" => Regex.IsMatch(value, expected, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "VersionRange" => ParseVersion(value) >= ParseVersion(expected) && ParseVersion(value) <= ParseVersion(Text("Upper Version")),
            "FileExists" => await GetRun(context).ExistsAsync(path, context.CancellationToken).ConfigureAwait(false),
            "DirectoryExists" => await GetRun(context).DirectoryExistsAsync(path, context.CancellationToken).ConfigureAwait(false),
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

    private void AddInput(NodeInputDeclaration declaration)
    {
        AddInput(declaration.Title, declaration.PortType, declaration.InitialValue, declaration.Multiline,
            declaration.Directory, declaration.Choices?.ToArray(), declaration.HintResourceKey);
    }

    private void AddOutput(NodeOutputDeclaration declaration)
        => AddOutput(declaration.Title, declaration.PortType, declaration.ValueResourcePrefix);

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
            PortId = NodeConnectorViewModel.CreateStableId(title),
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
            PortId = NodeConnectorViewModel.CreateStableId(title),
            PortType = type,
            ValueResourcePrefix = valueResourcePrefix
        });
    }
}