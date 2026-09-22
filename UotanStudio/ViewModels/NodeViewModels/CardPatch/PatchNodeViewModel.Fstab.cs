using UotanStudio.Firmware.BoundedContexts.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureFstabNode(PatchNodeViewModel node)
    {
        node.AddInput("Text", NodePortType.Text, "", multiline: true,
            hint: node.TypeKey == "Config.Fstab.Add" ? "Node_Patch_FstabAddHint" : "Node_Patch_FstabHint");
        node.AddInput("Mount Point", NodePortType.Text, "/system");
        if (node.TypeKey == "Config.Fstab.Add")
        {
            node.AddInput("Block Device", NodePortType.Text, "system");
            node.AddInput("File System", NodePortType.Text, "ext4");
            node.AddInput("Mount Options", NodePortType.Text, "ro");
            node.AddInput("Fs Mgr Flags", NodePortType.Text, "wait,logical");
            node.AddResultOutputs();
            return;
        }
        node.AddInput("File System Filter", NodePortType.Text, "");
        node.AddInput("Field", NodePortType.Text, "FsMgrFlags", choices: Enum.GetNames<FstabField>());
        node.AddInput("Expected Matches", NodePortType.Number, 1d);
        if (node.TypeKey == "Config.Fstab.Edit")
        {
            node.AddInput("Operation", NodePortType.Text, "SetValue", choices: Enum.GetNames<FstabOperation>());
            node.AddInput("Value", NodePortType.Text, "");
            node.AddInput("Flag Name", NodePortType.Text, "");
            node.AddOldValueInputs();
            node.AddResultOutputs();
        }
        else
        {
            node.AddOutput("Value", NodePortType.Text);
            node.AddOutput("Values", NodePortType.Any);
            node.AddOutput("Count", NodePortType.Number);
        }
    }

    private void ExecuteFstabNode(CancellationToken token)
    {
        if (TypeKey == "Config.Fstab.Add")
        {
            SetResult(FstabPatch.Add(Text("Text"), Text("Block Device"), Text("Mount Point"), Text("File System"),
                Text("Mount Options"), Text("Fs Mgr Flags"), token));
            return;
        }
        var field = Enum.Parse<FstabField>(Text("Field"));
        if (TypeKey == "Config.Fstab.Query")
        {
            var values = FstabPatch.Read(Text("Text"), Text("Mount Point"), Text("File System Filter"), field, Count(), token);
            WriteOutput("Values", values);
            WriteOutput("Value", values.Count == 1 ? values[0] : null);
            WriteOutput("Count", values.Count);
        }
        else
        {
            SetResult(FstabPatch.Apply(Text("Text"), Text("Mount Point"), Text("File System Filter"), field,
                Operation<FstabOperation>(), Text("Value"), Text("Flag Name"), Boolean("Check Old Value"),
                Text("Expected Old Value"), Count(), token));
        }
    }
}