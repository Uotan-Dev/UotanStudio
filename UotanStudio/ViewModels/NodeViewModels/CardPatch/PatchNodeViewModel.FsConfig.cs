using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureFsConfigNode(PatchNodeViewModel node)
    {
        node.AddInput("Text", NodePortType.Text, "", multiline: true,
            hint: node.TypeKey is "Config.FsConfig.Add" or "Config.FsConfig.Delete" ? "Node_Patch_FsConfigEntryHint" : "Node_Patch_FsConfigHint");
        node.AddInput("Image Path", NodePortType.Text, "");
        if (node.TypeKey is "Config.FsConfig.Edit" or "Config.FsConfig.Add" or "Config.FsConfig.Delete")
        {
            if (node.TypeKey != "Config.FsConfig.Delete")
            {
                node.AddInput("UID", NodePortType.Text, "0");
                node.AddInput("GID", NodePortType.Text, "0");
                node.AddInput("Mode", NodePortType.Text, "0644");
            }
            if (node.TypeKey != "Config.FsConfig.Add")
            {
                node.AddInput("Check Old Value", NodePortType.Boolean, false);
                node.AddInput("Expected UID", NodePortType.Text, "0");
                node.AddInput("Expected GID", NodePortType.Text, "0");
                node.AddInput("Expected Mode", NodePortType.Text, "0644");
            }
            node.AddResultOutputs();
        }
        else
        {
            node.AddOutput("UID", NodePortType.Text);
            node.AddOutput("GID", NodePortType.Text);
            node.AddOutput("Mode", NodePortType.Text);
        }
    }

    private void ExecuteFsConfigNode(CancellationToken token)
    {
        if (TypeKey == "Config.FsConfig.Add")
        {
            SetResult(FsConfigPatch.Add(Text("Text"), Text("Image Path"), Text("UID"), Text("GID"), Text("Mode"), token));
            return;
        }
        if (TypeKey == "Config.FsConfig.Delete")
        {
            SetResult(FsConfigPatch.Delete(Text("Text"), Text("Image Path"), Boolean("Check Old Value"),
                Text("Expected UID"), Text("Expected GID"), Text("Expected Mode"), token));
            return;
        }
        if (TypeKey == "Config.FsConfig.Edit")
        {
            SetResult(FsConfigPatch.Edit(Text("Text"), Text("Image Path"), Text("UID"), Text("GID"), Text("Mode"),
                Boolean("Check Old Value"), Text("Expected UID"), Text("Expected GID"), Text("Expected Mode"), token));
            return;
        }
        var entry = FsConfigPatch.Read(Text("Text"), Text("Image Path"), token);
        WriteOutput("UID", entry.Uid.ToString(CultureInfo.InvariantCulture));
        WriteOutput("GID", entry.Gid.ToString(CultureInfo.InvariantCulture));
        WriteOutput("Mode", "0" + Convert.ToString(entry.Mode, 8).PadLeft(3, '0'));
    }
}