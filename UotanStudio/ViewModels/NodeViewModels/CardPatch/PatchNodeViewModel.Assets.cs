using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureAssetNode(PatchNodeViewModel node)
    {
        if (node.TypeKey == "Patch.Asset")
        {
            node.AddInput("Asset ID", NodePortType.Text, string.Empty, hint: "Node_Patch_AssetHint");
            node.AddOutput("Asset", NodePortType.PatchAsset);
            node.AddOutput("Name", NodePortType.Text);
            node.AddOutput("Length", NodePortType.Number);
            node.AddOutput("Hash", NodePortType.Text);
            return;
        }
        node.AddInput("Asset", NodePortType.PatchAsset, hint: "Node_Patch_AssetHint");
        if (node.TypeKey == "Patch.AssetText")
        {
            node.AddOutput("Text", NodePortType.Text);
            node.AddOutput("Encoding", NodePortType.Text);
            node.AddOutput("Hash", NodePortType.Text);
            return;
        }
        node.AddInput("Workspace", NodePortType.PatchWorkspace);
        node.AddInput("Relative Path", NodePortType.Text, string.Empty);
        node.AddInput("Operation", NodePortType.Text, "Add", choices: Enum.GetNames<PatchFileMode>());
        node.AddOutput("Workspace", NodePortType.PatchWorkspace);
    }

    private async Task ExecuteAssetNodeAsync(INodeExecutionContext context)
    {
        if (TypeKey == "Patch.Asset")
        {
            var package = context.Package
                ?? throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_MissingPackage"));
            var resource = PatchAsset.Resolve(package, Text("Asset ID"));
            WriteOutput("Asset", resource);
            WriteOutput("Name", resource.Name);
            WriteOutput("Length", resource.Length);
            WriteOutput("Hash", resource.Sha256);
            return;
        }
        var asset = ReadInput("Asset") as PatchAsset
            ?? throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_MissingAsset"));
        if (TypeKey == "Patch.AssetText")
        {
            var document = await asset.ReadTextAsync(context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Text", document.Text);
            WriteOutput("Encoding", document.Encoding);
            WriteOutput("Hash", document.Sha256);
            return;
        }
        var run = GetRun(context);
        var workspace = ReadInput("Workspace") as PatchWorkspace
            ?? throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_MissingWorkspace"));
        run.CheckWorkspace(workspace);
        await run.StageAssetAsync(asset, workspace.Resolve(Text("Relative Path")), Operation<PatchFileMode>(),
            context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Workspace", workspace);
    }
}