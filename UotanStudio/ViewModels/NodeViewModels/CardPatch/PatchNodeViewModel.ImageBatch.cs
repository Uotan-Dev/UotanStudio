using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureImageBatch(PatchNodeViewModel node)
    {
        node.AddInput("Image Edits", NodePortType.ImageEdits, hint: "Node_Patch_ImageBatchHint");
        if (node.TypeKey == "Image.Edit.CollectText")
        {
            node.AddInput("Image Path", NodePortType.Text, "/build.prop");
            node.AddInput("Text", NodePortType.Text, "", multiline: true);
            node.AddInput("Encoding", NodePortType.Text, "utf8", choices: ["utf8", "utf8-bom", "utf16-le", "utf16-be"]);
            node.AddInput("Expected Hash", NodePortType.Text, "");
            node.AddOutput("Image Edits", NodePortType.ImageEdits);
            node.AddOutput("Count", NodePortType.Number);
        }
        else
        {
            node.AddInput("Path", NodePortType.Path);
            node.AddInput("File System", NodePortType.Text, "EXT4", choices: ["EXT4", "EROFS"]);
            node.AddInput("Image Capacity", NodePortType.Text, "", hint: "Node_Patch_ImageCapacityHint");
            node.AddInput("Extraction Budget", NodePortType.Text, "68719476736");
            node.AddOutput("Changed", NodePortType.Boolean);
        }
    }

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