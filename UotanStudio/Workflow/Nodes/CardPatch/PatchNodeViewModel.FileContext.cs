using UotanStudio.Firmware.Patching;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
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
        var operation = Text("Operation");
        if (operation == "Add")
        {
            SetResult(FileContextPatch.Add(Text("Text"), Text("Path Expression"), kind, Text("Context"), token));
            return;
        }
        if (operation == "Delete")
        {
            SetResult(FileContextPatch.Delete(Text("Text"), Text("Path Expression"), kind,
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        if (operation == "Edit")
        {
            SetResult(FileContextPatch.Edit(Text("Text"), Text("Path Expression"), kind, Text("Context"),
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        if (operation == "Query")
        {
            WriteOutput("Context", FileContextPatch.Read(Text("Text"), Text("Path Expression"), kind, token));
            return;
        }
        throw new InvalidDataException("Unsupported file context operation.");
    }
}