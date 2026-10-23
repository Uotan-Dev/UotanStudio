using UotanStudio.Firmware.Patching;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
    private void ExecuteFstabNode(CancellationToken token)
    {
        var operation = Text("Operation");
        if (operation == "Add")
        {
            SetResult(FstabPatch.Add(Text("Text"), Text("Block Device"), Text("Mount Point"), Text("File System"),
                Text("Mount Options"), Text("Fs Mgr Flags"), token));
            return;
        }
        var field = Enum.Parse<FstabField>(Text("Field"));
        if (operation == "Query")
        {
            var values = FstabPatch.Read(Text("Text"), Text("Mount Point"), Text("File System Filter"), field, Count(), token);
            WriteOutput("Values", values);
            WriteOutput("Value", values.Count == 1 ? values[0] : null);
            WriteOutput("Count", values.Count);
            return;
        }
        SetResult(FstabPatch.Apply(Text("Text"), Text("Mount Point"), Text("File System Filter"), field,
            Operation<FstabOperation>(), Text("Value"), Text("Flag Name"), Boolean("Check Old Value"),
            Text("Expected Old Value"), Count(), token));
    }
}