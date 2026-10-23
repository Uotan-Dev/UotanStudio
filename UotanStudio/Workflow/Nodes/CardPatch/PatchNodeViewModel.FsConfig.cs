using System.Globalization;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
    private void ExecuteFsConfigNode(CancellationToken token)
    {
        var operation = Text("Operation");
        if (operation == "Add")
        {
            SetResult(FsConfigPatch.Add(Text("Text"), Text("Image Path"), Text("UID"), Text("GID"), Text("Mode"), token));
            return;
        }
        if (operation == "Delete")
        {
            SetResult(FsConfigPatch.Delete(Text("Text"), Text("Image Path"), Boolean("Check Old Value"),
                Text("Expected UID"), Text("Expected GID"), Text("Expected Mode"), token));
            return;
        }
        if (operation == "Edit")
        {
            SetResult(FsConfigPatch.Edit(Text("Text"), Text("Image Path"), Text("UID"), Text("GID"), Text("Mode"),
                Boolean("Check Old Value"), Text("Expected UID"), Text("Expected GID"), Text("Expected Mode"), token));
            return;
        }
        if (operation == "Query")
        {
            var entry = FsConfigPatch.Read(Text("Text"), Text("Image Path"), token);
            WriteOutput("UID", entry.Uid.ToString(CultureInfo.InvariantCulture));
            WriteOutput("GID", entry.Gid.ToString(CultureInfo.InvariantCulture));
            WriteOutput("Mode", "0" + Convert.ToString(entry.Mode, 8).PadLeft(3, '0'));
            return;
        }
        throw new InvalidDataException("Unsupported fs_config operation.");
    }
}