using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureImageXattrEdit(PatchNodeViewModel node)
    {
        node.AddInput("Path", NodePortType.Path, hint: "Node_Patch_ImageXattrEditHint");
        node.AddInput("File System", NodePortType.Text, "EXT4", choices: ["EXT4", "EROFS"]);
        node.AddInput("Image Path", NodePortType.Text, "/build.prop");
        node.AddInput("Attribute Name", NodePortType.Text, "user.patch");
        if (node.TypeKey == "Patch.ImageSetXattr") { node.AddInput("Hex Value", NodePortType.Text, "", multiline: true); }
        node.AddInput("Check Old Value", NodePortType.Boolean, false);
        node.AddInput("Expected Exists", NodePortType.Boolean, true);
        node.AddInput("Expected Hex Value", NodePortType.Text, "", multiline: true);
        node.AddInput("Image Capacity", NodePortType.Text, "", hint: "Node_Patch_ImageCapacityHint");
        node.AddInput("Extraction Budget", NodePortType.Text, "68719476736");
        node.AddOutput("Changed", NodePortType.Boolean);
    }

    private async Task ExecuteImageXattrEditAsync(INodeExecutionContext context)
    {
        var check = Boolean("Check Old Value");
        var edit = new FileSystemXattrEdit(Text("Image Path"), Text("Attribute Name"),
            TypeKey == "Patch.ImageSetXattr" ? XattrHex("Hex Value") : null,
            check, check && Boolean("Expected Exists") ? XattrHex("Expected Hex Value") : null);
        WriteOutput("Changed", await GetRun(context).StageImageXattrAsync(Text("Path"), Text("File System"), edit,
            PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), context.CancellationToken).ConfigureAwait(false));
    }

    private byte[] XattrHex(string port)
    {
        var text = Text(port);
        if (text.Length > 131072 || (text.Length & 1) != 0 || text.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(Services.LanguageService.Instance.GetString("Node_Patch_InvalidXattrHex"));
        }
        return Convert.FromHexString(text);
    }
}
