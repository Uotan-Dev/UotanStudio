using UotanStudio.Firmware.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureFileContextNode(PatchNodeViewModel node)
    {
        node.AddInput("Text", NodePortType.Text, "", multiline: true,
            hint: node.TypeKey is "Config.FileContexts.Add" or "Config.FileContexts.Delete" ? "Node_Patch_FileContextEntryHint" : "Node_Patch_FileContextHint");
        node.AddInput("Path Expression", NodePortType.Text, "");
        node.AddInput("File Kind", NodePortType.Text, "Any", choices: ["Any", "Regular", "Directory", "Symlink", "Block", "Character", "Pipe", "Socket"]);
        if (node.TypeKey is "Config.FileContexts.Edit" or "Config.FileContexts.Add" or "Config.FileContexts.Delete")
        {
            if (node.TypeKey != "Config.FileContexts.Delete")
            {
                node.AddInput("Context", NodePortType.Text, "u:object_r:vendor_file:s0");
            }
            if (node.TypeKey != "Config.FileContexts.Add")
            {
                node.AddOldValueInputs();
            }
            node.AddResultOutputs();
        }
        else
        {
            node.AddOutput("Context", NodePortType.Text);
        }
    }

    private void ExecuteFileContextNode(CancellationToken token)
    {
        var kind = Text("File Kind") switch
        {
            "Any" => "",
            "Regular" => "--",
            "Directory" => "-d",
            "Symlink" => "-l",
            "Block" => "-b",
            "Character" => "-c",
            "Pipe" => "-p",
            "Socket" => "-s",
            _ => throw new InvalidDataException("Unsupported file context kind.")
        };
        if (TypeKey == "Config.FileContexts.Add")
        {
            SetResult(FileContextPatch.Add(Text("Text"), Text("Path Expression"), kind, Text("Context"), token));
            return;
        }
        if (TypeKey == "Config.FileContexts.Delete")
        {
            SetResult(FileContextPatch.Delete(Text("Text"), Text("Path Expression"), kind,
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        if (TypeKey == "Config.FileContexts.Edit")
        {
            SetResult(FileContextPatch.Edit(Text("Text"), Text("Path Expression"), kind, Text("Context"),
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
        }
        else
        {
            WriteOutput("Context", FileContextPatch.Read(Text("Text"), Text("Path Expression"), kind, token));
        }
    }
}