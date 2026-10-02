using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private async Task ExecuteImageXattrEditAsync(INodeExecutionContext context)
    {
        var check = Boolean("Check Old Value");
        var edit = new FileSystemXattrEdit(Text("Image Path"), Text("Attribute Name"),
            TypeKey == "Image.Xattr.Set" ? XattrHex("Hex Value") : null,
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