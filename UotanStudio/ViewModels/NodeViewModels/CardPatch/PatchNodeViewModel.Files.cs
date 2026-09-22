using UotanStudio.Firmware.BoundedContexts.Patching;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureFileNode(PatchNodeViewModel node)
    {
        switch (node.TypeKey)
        {
            case "Binary.Pattern.Find":
            case "Binary.Pattern.Replace":
                node.AddInput("Path", NodePortType.Path);
                node.AddInput("Find", NodePortType.Text, "", hint: node.TypeKey == "Binary.Pattern.Find" ? "Node_Patch_BinaryFindHint" : "Node_Patch_BinaryReplaceHint");
                node.AddInput("Find Mask", NodePortType.Text, "");
                if (node.TypeKey == "Binary.Pattern.Replace")
                {
                    node.AddInput("Replacement", NodePortType.Text, "");
                    node.AddInput("Replace Mask", NodePortType.Text, "");
                }
                node.AddInput("Start Offset", NodePortType.Text, "0");
                node.AddInput("Search Length", NodePortType.Text, "-1");
                node.AddInput("Alignment", NodePortType.Text, "1");
                node.AddInput("Skip Matches", NodePortType.Text, "0");
                node.AddInput("Expected Matches", NodePortType.Number, 1d);
                node.AddOutput("Offsets", NodePortType.Any);
                node.AddOutput("Count", NodePortType.Number);
                return;
            case "Validation.Format.Detect":
            case "Validation.Format.Check":
                node.AddInput("Path", NodePortType.Path, hint: "Node_Patch_FormatHint");
                if (node.TypeKey == "Validation.Format.Check")
                {
                    node.AddInput("Expected Format", NodePortType.Text, "Json",
                        choices: Enum.GetNames<PatchFormatKind>().Where(name => name != "Unknown").ToArray());
                    node.AddInput("Required Evidence", NodePortType.Text, "Structure", choices: ["Signature", "Structure"]);
                    node.AddOutput("Match", NodePortType.Boolean);
                }
                node.AddOutput("Format", NodePortType.Text, "Node_Choice_");
                node.AddOutput("Evidence", NodePortType.Text, "Node_Choice_");
                node.AddOutput("Detail", NodePortType.Text, "Node_PatchFormat_");
                node.AddOutput("Structure Valid", NodePortType.Boolean);
                return;
            case "Workspace.Directory.Open":
                node.AddInput("Root", NodePortType.Path, directory: true);
                node.AddOutput("Workspace", NodePortType.PatchWorkspace);
                return;
            case "Validation.File.Hash":
            case "Validation.Hash.Verify":
                node.AddInput("Path", NodePortType.Path);
                if (node.TypeKey == "Validation.Hash.Verify")
                {
                    node.AddInput("Expected Hash", NodePortType.Text, string.Empty);
                    node.AddOutput("Valid", NodePortType.Boolean);
                }
                node.AddOutput("Hash", NodePortType.Text);
                return;
            case "Validation.Count.Assert":
                node.AddInput("Count", NodePortType.Number, 0d);
                node.AddInput("Expected Matches", NodePortType.Number, 1d);
                node.AddOutput("Valid", NodePortType.Boolean);
                return;
        }
        node.AddInput("Workspace", NodePortType.PatchWorkspace);
        node.AddOutput("Workspace", NodePortType.PatchWorkspace);
        switch (node.TypeKey)
        {
            case "Workspace.Partition.Resolve":
                node.AddInput("Partition", NodePortType.Text, "vendor", choices:
                    ["system", "vendor", "product", "system_ext", "odm", "vendor_dlkm", "odm_dlkm", "system_dlkm"]);
                node.AddInput("Layout", NodePortType.Text, "PartitionDirectories",
                    choices: ["PartitionDirectories", "PartitionRoot", "SystemAsRoot"], hint: "Node_Patch_PartitionPathHint");
                node.AddInput("Partition Path", NodePortType.Text, "/etc/build.prop");
                node.AddOutput("Path", NodePortType.Path);
                node.AddOutput("Relative Path", NodePortType.Text);
                node.AddOutput("Exists", NodePortType.Boolean);
                break;
            case "Workspace.File.Find":
                node.AddInput("Pattern", NodePortType.Text, "vendor/etc/*.xml");
                node.AddInput("Expected Matches", NodePortType.Number, 1d);
                node.AddOutput("Path", NodePortType.Path);
                node.AddOutput("Files", NodePortType.Any);
                node.AddOutput("Relative Path", NodePortType.Text);
                node.AddOutput("Relative Paths", NodePortType.Any);
                node.AddOutput("Count", NodePortType.Number);
                break;
            case "Workspace.Text.Read":
                node.AddInput("Relative Path", NodePortType.Text, string.Empty);
                node.AddOutput("Text", NodePortType.Text);
                node.AddOutput("Encoding", NodePortType.Text);
                node.AddOutput("Hash", NodePortType.Text);
                break;
            case "Workspace.Text.Stage":
                node.AddInput("Relative Path", NodePortType.Text, string.Empty);
                node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true);
                node.AddInput("Encoding", NodePortType.Text, "utf8", choices: ["utf8", "utf8-bom", "utf16-le", "utf16-be"]);
                node.AddInput("Operation", NodePortType.Text, "Replace", choices: Enum.GetNames<PatchFileMode>());
                break;
            case "Workspace.File.Import":
            case "Workspace.File.Copy":
                node.AddInput("Source", node.TypeKey == "Workspace.File.Import" ? NodePortType.Path : NodePortType.Text, string.Empty);
                node.AddInput("Relative Path", NodePortType.Text, string.Empty);
                node.AddInput("Operation", NodePortType.Text, "Add", choices: Enum.GetNames<PatchFileMode>());
                node.AddInput("Expected Hash", NodePortType.Text, string.Empty);
                break;
            case "Workspace.File.Delete":
                node.AddInput("Relative Path", NodePortType.Text, string.Empty);
                node.AddInput("Expected Hash", NodePortType.Text, string.Empty);
                break;
            case "Workspace.File.Exists":
                node.AddInput("Relative Path", NodePortType.Text, string.Empty);
                node.AddOutput("Exists", NodePortType.Boolean);
                break;
            case "Validation.Changes.Verify":
                node.AddInput("Expected Paths", NodePortType.Text, string.Empty, multiline: true);
                node.AddOutput("Valid", NodePortType.Boolean);
                break;
            default:
                throw new ArgumentException("Unknown patch node type.", nameof(node));
        }
    }

    private async Task ExecuteFileNodeAsync(INodeExecutionContext context)
    {
        var run = GetRun(context);
        var token = context.CancellationToken;
        switch (TypeKey)
        {
            case "Binary.Pattern.Find":
            case "Binary.Pattern.Replace":
                var offsets = TypeKey == "Binary.Pattern.Replace"
                    ? await run.StageBinaryAsync(Text("Path"), Text("Find"), Text("Find Mask"), Text("Replacement"), Text("Replace Mask"),
                        long.Parse(Text("Start Offset"), System.Globalization.CultureInfo.InvariantCulture),
                        long.Parse(Text("Search Length"), System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Text("Alignment"), System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Text("Skip Matches"), System.Globalization.CultureInfo.InvariantCulture), Count(), token).ConfigureAwait(false)
                    : await run.FindBinaryAsync(Text("Path"), Text("Find"), Text("Find Mask"),
                    long.Parse(Text("Start Offset"), System.Globalization.CultureInfo.InvariantCulture),
                    long.Parse(Text("Search Length"), System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(Text("Alignment"), System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(Text("Skip Matches"), System.Globalization.CultureInfo.InvariantCulture), Count(), token).ConfigureAwait(false);
                WriteOutput("Offsets", offsets);
                WriteOutput("Count", offsets.Count);
                return;
            case "Validation.Format.Detect":
            case "Validation.Format.Check":
                var expectedFormat = PatchFormatKind.Unknown;
                var required = PatchFormatEvidence.Structure;
                if (TypeKey == "Validation.Format.Check")
                {
                    if (!Enum.TryParse(Text("Expected Format"), out expectedFormat) || !Enum.IsDefined(expectedFormat)
                        || expectedFormat == PatchFormatKind.Unknown
                        || !Enum.TryParse(Text("Required Evidence"), out required) || required is not PatchFormatEvidence.Signature and not PatchFormatEvidence.Structure)
                    {
                        throw new InvalidDataException("Invalid format precondition.");
                    }
                }
                var probe = await run.ProbeFormatAsync(Text("Path"), expectedFormat, token).ConfigureAwait(false);
                WriteOutput("Format", probe.Format.ToString());
                WriteOutput("Evidence", probe.Evidence.ToString());
                WriteOutput("Detail", probe.Detail);
                WriteOutput("Structure Valid", !probe.Rejected && probe.Evidence == PatchFormatEvidence.Structure);
                if (TypeKey == "Validation.Format.Check")
                {
                    WriteOutput("Match", probe.Matches(expectedFormat, required));
                }
                return;
            case "Workspace.Directory.Open":
                WriteOutput("Workspace", run.OpenWorkspace(Text("Root")));
                return;
            case "Validation.File.Hash":
            case "Validation.Hash.Verify":
                var hash = await run.HashAsync(Text("Path"), token).ConfigureAwait(false);
                if (TypeKey == "Validation.Hash.Verify")
                {
                    var expected = Text("Expected Hash");
                    if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))
                    {
                        throw new InvalidDataException("Expected hash must contain exactly 64 hexadecimal characters.");
                    }
                    if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PatchConflictException(Text("Path"));
                    }
                    WriteOutput("Valid", true);
                }
                WriteOutput("Hash", hash);
                return;
            case "Validation.Count.Assert":
                var actual = Convert.ToDouble(ReadInput("Count"), System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsFinite(actual) || actual != Count())
                {
                    throw new InvalidDataException("Patch result count does not match the expected count.");
                }
                WriteOutput("Valid", true);
                return;
        }
        var workspace = ReadInput("Workspace") as PatchWorkspace
            ?? throw new InvalidDataException("Connect a patch workspace before operating on its files.");
        run.CheckWorkspace(workspace);
        switch (TypeKey)
        {
            case "Workspace.Partition.Resolve":
                var mapped = PatchPartitionPath.Resolve(workspace, Text("Partition"), Text("Layout"), Text("Partition Path"));
                var resolved = workspace.Resolve(mapped);
                WriteOutput("Relative Path", mapped);
                WriteOutput("Path", resolved);
                WriteOutput("Exists", await run.ExistsAsync(resolved, token).ConfigureAwait(false));
                break;
            case "Workspace.File.Find":
                var files = await run.FindAsync(workspace.Root, Text("Pattern"), Count(), token).ConfigureAwait(false);
                var relative = files.Select(path => Path.GetRelativePath(workspace.Root, path).Replace('\\', '/')).ToArray();
                WriteOutput("Files", files);
                WriteOutput("Path", files.Count == 1 ? files[0] : null);
                WriteOutput("Relative Paths", relative);
                WriteOutput("Relative Path", relative.Length == 1 ? relative[0] : null);
                WriteOutput("Count", files.Count);
                break;
            case "Workspace.Text.Read":
                var document = await run.ReadTextAsync(workspace.Resolve(Text("Relative Path")), token).ConfigureAwait(false);
                WriteOutput("Text", document.Text);
                WriteOutput("Encoding", document.Encoding);
                WriteOutput("Hash", document.Sha256);
                break;
            case "Workspace.Text.Stage":
                await run.StageTextAsync(workspace.Resolve(Text("Relative Path")), Text("Text"), Text("Encoding"),
                    Operation<PatchFileMode>(), token).ConfigureAwait(false);
                break;
            case "Workspace.File.Import":
            case "Workspace.File.Copy":
                var source = TypeKey == "Workspace.File.Copy" ? workspace.Resolve(Text("Source")) : Text("Source");
                await run.StageCopyAsync(source, workspace.Resolve(Text("Relative Path")), Operation<PatchFileMode>(),
                    Text("Expected Hash"), token).ConfigureAwait(false);
                break;
            case "Workspace.File.Delete":
                await run.StageDeleteAsync(workspace.Resolve(Text("Relative Path")), Text("Expected Hash"), token).ConfigureAwait(false);
                break;
            case "Workspace.File.Exists":
                WriteOutput("Exists", await run.ExistsAsync(workspace.Resolve(Text("Relative Path")), token).ConfigureAwait(false));
                break;
            case "Validation.Changes.Verify":
                await run.VerifyChangesAsync(workspace,
                    Text("Expected Paths").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), token).ConfigureAwait(false);
                WriteOutput("Valid", true);
                break;
        }
        WriteOutput("Workspace", workspace);
    }

    private static PatchRun GetRun(INodeExecutionContext context) => context.Patches
        ?? throw new InvalidOperationException("Patch file nodes require a workflow execution scope.");
}