using UotanStudio.Services.Patching;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
    private async Task ExecuteImageBatchAsync(INodeExecutionContext context)
    {
        var input = ReadInput("Image Edits");
        var edits = input as PatchImageEdits;
        if (input is not null && edits is null || edits is null && TypeKey == "Image.Edit.Apply")
        {
            throw new InvalidDataException(Services.LanguageService.Instance.GetString("Node_Patch_ImageEditsRequired"));
        }
        if (TypeKey == "Image.Edit.CollectText")
        {
            var next = (edits ?? PatchImageEdits.Empty).Append(new(Text("Image Path"), Text("Text"), Text("Encoding"), Text("Expected Hash")));
            WriteOutput("Image Edits", next);
            WriteOutput("Count", next.Items.Count);
        }
        else
        {
            WriteOutput("Changed", await GetRun(context).StageImageTextsAsync(Text("Path"), Text("File System"), edits!.Items,
                PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), context.CancellationToken).ConfigureAwait(false));
        }
    }
}