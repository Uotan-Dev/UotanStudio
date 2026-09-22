using UotanStudio.Firmware.BoundedContexts.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureFileContextNode(PatchNodeViewModel node)
    {
        node.AddInput("Text", NodePortType.Text, "", multiline: true,
            hint: node.TypeKey is "Patch.FileContextAdd" or "Patch.FileContextDelete" ? "Node_Patch_FileContextEntryHint" : "Node_Patch_FileContextHint");
        node.AddInput("Path Expression", NodePortType.Text, "");
        node.AddInput("File Kind", NodePortType.Text, "Any", choices: ["Any", "Regular", "Directory", "Symlink", "Block", "Character", "Pipe", "Socket"]);
        if (node.TypeKey is "Patch.FileContextEdit" or "Patch.FileContextAdd" or "Patch.FileContextDelete")
        {
            if (node.TypeKey != "Patch.FileContextDelete")
            {
                node.AddInput("Context", NodePortType.Text, "u:object_r:vendor_file:s0");
            }
            if (node.TypeKey != "Patch.FileContextAdd")
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
        if (TypeKey == "Patch.FileContextAdd")
        {
            SetResult(FileContextPatch.Add(Text("Text"), Text("Path Expression"), kind, Text("Context"), token));
            return;
        }
        if (TypeKey == "Patch.FileContextDelete")
        {
            SetResult(FileContextPatch.Delete(Text("Text"), Text("Path Expression"), kind,
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        if (TypeKey == "Patch.FileContextEdit")
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