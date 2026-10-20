using System.Globalization;
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
        node.ApplyAssetIdChoices();
        return node;
    }

    /// <summary>
    /// Offers the asset identifier of the resource read node as a selection over the current project resources, so a
    /// mistyped identifier is caught while editing instead of at execution time. The port type and its declaration
    /// stay unchanged: an identifier outside the offered list remains typable and is validated when the node runs.
    /// <para>把资源读取节点的资源标识提供为当前项目资源的选择项，使拼错的标识在编辑时就能发现，而不是等到执行期。端口类型
    /// 与其声明保持不变：提供列表之外的标识仍可填写，并在节点运行时校验。</para>
    /// </summary>
    private void ApplyAssetIdChoices()
    {
        if (TypeKey is not "Resource.Asset.Read")
        {
            return;
        }
        var assetId = Input.FirstOrDefault(port => string.Equals(port.Title, "Asset ID", StringComparison.Ordinal));
        if (assetId is not null)
        {
            NodeResourceIdChoices.Apply(assetId);
        }
    }

    /// <inheritdoc />
    public override NodeFlowKind FlowKind => TypeKey switch
    {
        "Flow.Applicability.Gate" => NodeFlowKind.Branch,
        _ => NodeFlowKind.None
    };

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
            case "Image.Xattr.Edit":
                await ExecuteImageXattrAsync(context).ConfigureAwait(false);
                break;
            case "Image.Xattr.Read":
                var xattr = await GetRun(context).ReadImageXattrAsync(Text("Path"), Text("File System"), Text("Image Path"),
                    Text("Attribute Name"), token).ConfigureAwait(false);
                WriteOutput("Data", xattr ?? []);
                WriteOutput("Exists", xattr is not null);
                break;
            case "Image.File.ReadAttributes":
            case "Image.File.SetAttributes":
            case "Image.File.Attributes":
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
            case "Image.Text.Edit":
                if (Text("Operation") == "Read")
                {
                    var readText = await GetRun(context).ReadImageTextAsync(Text("Path"), Text("File System"), Text("Image Path"), token).ConfigureAwait(false);
                    WriteOutput("Text", readText.Text);
                    WriteOutput("Encoding", readText.Encoding);
                    WriteOutput("Hash", readText.Sha256);
                    break;
                }
                WriteOutput("Changed", await GetRun(context).StageImageTextAsync(Text("Path"), Text("File System"),
                    Text("Image Path"), Text("Text"), Text("Encoding"), Text("Expected Hash"),
                    PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), token).ConfigureAwait(false));
                break;
            case "Config.Edit":
                ExecuteConfigEdit(token);
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
            case "Config.InitRc.Edit":
                ExecuteInitRcNode(token);
                break;
            case "Config.Fstab.Edit":
                ExecuteFstabNode(token);
                break;
            case "Flow.Applicability.Gate":
                ExecuteApplicabilityNode(context);
                break;
            case "Resource.Asset.ReadText":
            case "Resource.Asset.Read":
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
            case "Binary.Search":
                var binaryIsReplace = Text("Operation").Trim() == "Replace";
                var binaryOffsets = binaryIsReplace
                    ? await GetRun(context).StageBinaryAsync(Text("Path"), Text("Find"), Text("Find Mask"), Text("Replacement"), Text("Replace Mask"),
                        long.Parse(Text("Start Offset"), System.Globalization.CultureInfo.InvariantCulture),
                        long.Parse(Text("Search Length"), System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Text("Alignment"), System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Text("Skip Matches"), System.Globalization.CultureInfo.InvariantCulture), Count(), token).ConfigureAwait(false)
                    : await GetRun(context).FindBinaryAsync(Text("Path"), Text("Find"), Text("Find Mask"),
                        long.Parse(Text("Start Offset"), System.Globalization.CultureInfo.InvariantCulture),
                        long.Parse(Text("Search Length"), System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Text("Alignment"), System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Text("Skip Matches"), System.Globalization.CultureInfo.InvariantCulture), Count(), token).ConfigureAwait(false);
                WriteOutput("Offsets", binaryOffsets);
                WriteOutput("Count", binaryOffsets.Count);
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
    /// Applicability evaluation behind Flow.Applicability.Gate: evaluates, records, and turns Conflict into an
    /// exception. The branch is carried by the Apply/Skip Flow outputs instead of a Completed flow edge.
    /// <para>Flow.Applicability.Gate 使用的适用性判定：求值、记录并把 Conflict 转为异常。分支由 Apply/Skip Flow 输出承载，
    /// 不再依赖 Completed 流程边。</para>
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

    private string Text(string title) => Convert.ToString(ReadInput(title), CultureInfo.InvariantCulture) ?? string.Empty;

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

    private T Operation<T>(string title = "Operation") where T : struct, Enum
    {
        if (!Enum.TryParse<T>(Text(title), out var result) || !Enum.IsDefined(result))
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
        var port = Input[^1];
        port.IsAdvanced = declaration.Advanced;
        port.IsConfiguration = declaration.IsConfiguration;
        port.IsVisibleWhen = declaration.VisibleWhen
            ?? (declaration.Rule is { } rule ? rule.IsSatisfiedBy : null);
    }

    private void AddOutput(NodeOutputDeclaration declaration)
        => AddOutput(declaration.Title, declaration.PortType, declaration.ValueResourcePrefix, declaration.FlowRole);

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

    private void AddOutput(string title, NodePortType type, string? valueResourcePrefix = null,
        NodeFlowRole flowRole = NodeFlowRole.None)
    {
        Output.Add(new NodeConnectorViewModel
        {
            Title = title,
            PortId = NodeConnectorViewModel.CreateStableId(title),
            PortType = type,
            ValueResourcePrefix = valueResourcePrefix,
            FlowRole = flowRole
        });
    }
}