using System.Globalization;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
    private async Task ExecuteImageAttributesAsync(INodeExecutionContext context)
    {
        var run = GetRun(context);
        if (TypeKey == "Image.File.ReadAttributes"
            || TypeKey == "Image.File.Attributes" && Text("Operation") == "Read")
        {
            var attributes = await run.ReadImageAttributesAsync(Text("Path"), Text("File System"), Text("Image Path"), context.CancellationToken).ConfigureAwait(false);
            WriteOutput("UID", attributes.Uid.ToString(CultureInfo.InvariantCulture));
            WriteOutput("GID", attributes.Gid.ToString(CultureInfo.InvariantCulture));
            WriteOutput("Mode", Convert.ToString(attributes.Mode & 0xFFF, 8).PadLeft(4, '0'));
            return;
        }
        var guard = Boolean("Check Old Value");
        var edit = new FileSystemAttributeEdit(Text("Image Path"), ImageOwner("UID"), ImageOwner("GID"), ImagePermissions("Mode"),
            guard ? ImageOwner("Expected UID") : null, guard ? ImageOwner("Expected GID") : null,
            guard ? ImagePermissions("Expected Mode") : null);
        WriteOutput("Changed", await run.StageImageAttributesAsync(Text("Path"), Text("File System"), edit,
            PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), context.CancellationToken).ConfigureAwait(false));
    }

    private uint ImageOwner(string port)
    {
        if (!uint.TryParse(Text(port), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException(Services.LanguageService.Instance.GetString("Node_Patch_InvalidImageAttributes"));
        }
        return value;
    }

    private ushort ImagePermissions(string port)
    {
        var value = Text(port);
        if (value.Length is < 3 or > 4 || value.Any(character => character is < '0' or > '7'))
        {
            throw new InvalidDataException(Services.LanguageService.Instance.GetString("Node_Patch_InvalidImageAttributes"));
        }
        return Convert.ToUInt16(value, 8);
    }
}