using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private async Task ExecuteAssetNodeAsync(INodeExecutionContext context)
    {
        if (TypeKey == "Resource.Asset.Read" && Text("Operation") == "Select")
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
        if (TypeKey == "Resource.Asset.ReadText" || TypeKey == "Resource.Asset.Read")
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