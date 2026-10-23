using UotanStudio.Services.Patching;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
    /// <summary>
    /// Dispatches the unified workspace file node by its Operation input, covering lookup, existence check,
    /// external import, deletion and text staging inside one confined workspace.
    /// <para>按 Operation 输入分派统一工作区文件节点，在同一个受限工作区内覆盖查找、存在性检查、外部导入、删除与文本暂存。</para>
    /// </summary>
    private async Task ExecuteWorkspaceFileNode(INodeExecutionContext context)
    {
        var token = context.CancellationToken;
        var run = GetRun(context);
        var workspace = ReadInput("Workspace") as PatchWorkspace
            ?? throw new InvalidDataException(TextLocalized("Node_Patch_MissingWorkspace"));
        run.CheckWorkspace(workspace);
        var operation = Text("Operation").Trim();
        var relativePath = Text("Relative Path");
        switch (operation)
        {
            case "Find":
                var files = await run.FindAsync(workspace.Root, Text("Pattern"), Count(), token).ConfigureAwait(false);
                var relative = files.Select(path => Path.GetRelativePath(workspace.Root, path).Replace('\\', '/')).ToArray();
                WriteOutput("Files", files);
                WriteOutput("Path", files.Count == 1 ? files[0] : null);
                WriteOutput("Relative Paths", relative);
                WriteOutput("Relative Path", relative.Length == 1 ? relative[0] : null);
                WriteOutput("Count", files.Count);
                break;
            case "Exists":
                WriteOutput("Exists", await run.ExistsAsync(workspace.Resolve(relativePath), token).ConfigureAwait(false));
                break;
            case "Import":
                await run.StageCopyAsync(Text("Source"), workspace.Resolve(relativePath),
                    Operation<PatchFileMode>("File Mode"), Text("Expected Hash"), token).ConfigureAwait(false);
                break;
            case "Delete":
                await run.StageDeleteAsync(workspace.Resolve(relativePath), Text("Expected Hash"), token).ConfigureAwait(false);
                break;
            case "Stage":
                await run.StageTextAsync(workspace.Resolve(relativePath), Text("Text"), Text("Encoding"),
                    Operation<PatchFileMode>("File Mode"), token).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException($"Unsupported workspace file operation '{operation}'.");
        }
        WriteOutput("Workspace", workspace);
    }

    private static string TextLocalized(string key) => Services.LanguageService.Instance.GetString(key);
}