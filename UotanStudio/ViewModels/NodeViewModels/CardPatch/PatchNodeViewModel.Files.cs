using UotanStudio.Firmware.Patching;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
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
                var expectedFormat = PatchFormatKind.Unknown;
                var required = PatchFormatEvidence.Structure;
                if (!string.IsNullOrWhiteSpace(Text("Expected Format")))
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
                if (expectedFormat != PatchFormatKind.Unknown)
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
            case "Workspace.Text.Stage":
                await run.StageTextAsync(workspace.Resolve(Text("Relative Path")), Text("Text"), Text("Encoding"),
                    Operation<PatchFileMode>(), token).ConfigureAwait(false);
                break;
            case "Workspace.File.Import":
                await run.StageCopyAsync(Text("Source"), workspace.Resolve(Text("Relative Path")), Operation<PatchFileMode>(),
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