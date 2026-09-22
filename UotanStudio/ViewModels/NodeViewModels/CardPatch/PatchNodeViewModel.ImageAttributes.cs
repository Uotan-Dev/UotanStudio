using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureImageAttributes(PatchNodeViewModel node)
    {
        node.AddInput("Path", NodePortType.Path, hint: "Node_Patch_ImageAttributesHint");
        node.AddInput("File System", NodePortType.Text, "EXT4", choices: ["EXT4", "EROFS"]);
        node.AddInput("Image Path", NodePortType.Text, "/build.prop");
        if (node.TypeKey == "Patch.ImageReadAttributes")
        {
            node.AddOutput("UID", NodePortType.Text);
            node.AddOutput("GID", NodePortType.Text);
            node.AddOutput("Mode", NodePortType.Text);
            return;
        }
        node.AddInput("UID", NodePortType.Text, "0");
        node.AddInput("GID", NodePortType.Text, "0");
        node.AddInput("Mode", NodePortType.Text, "0644");
        node.AddInput("Check Old Value", NodePortType.Boolean, false);
        node.AddInput("Expected UID", NodePortType.Text, "0");
        node.AddInput("Expected GID", NodePortType.Text, "0");
        node.AddInput("Expected Mode", NodePortType.Text, "0644");
        node.AddInput("Image Capacity", NodePortType.Text, "", hint: "Node_Patch_ImageCapacityHint");
        node.AddInput("Extraction Budget", NodePortType.Text, "68719476736");
        node.AddOutput("Changed", NodePortType.Boolean);
    }

    private async Task ExecuteImageAttributesAsync(INodeExecutionContext context)
    {
        var run = GetRun(context);
        if (TypeKey == "Patch.ImageReadAttributes")
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