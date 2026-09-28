using UotanStudio.Firmware.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureStructuredNode(PatchNodeViewModel node)
    {
        var json = node.TypeKey is "Config.Json.Query" or "Config.Json.Edit";
        var edit = node.TypeKey is "Config.Json.Edit" or "Config.Ini.Edit";
        node.AddInput("Text", NodePortType.Text, string.Empty, multiline: true,
            hint: json ? "Node_Patch_JsonHint" : "Node_Patch_IniHint");
        if (json)
        {
            node.AddInput("JSON Pointer", NodePortType.Text, string.Empty);
        }
        else
        {
            node.AddInput("Section", NodePortType.Text, string.Empty);
            node.AddInput("Key", NodePortType.Text, string.Empty);
        }
        if (edit)
        {
            node.AddInput("Operation", NodePortType.Text, "Set", choices: ["Set", "Delete"]);
            node.AddInput(json ? "JSON Value" : "Value", NodePortType.Text, json ? "null" : string.Empty, multiline: json);
            node.AddInput("Allow Create", NodePortType.Boolean, false);
            node.AddInput("Check Old Value", NodePortType.Boolean, false);
            node.AddInput(json ? "Expected Old JSON" : "Expected Old Value", NodePortType.Text, json ? "null" : string.Empty, multiline: json);
            node.AddResultOutputs();
        }
        else
        {
            node.AddOutput(json ? "JSON Value" : "Value", NodePortType.Text);
            node.AddOutput("Exists", NodePortType.Boolean);
        }
    }

    private void ExecuteStructuredNode(CancellationToken token)
    {
        var json = TypeKey is "Config.Json.Query" or "Config.Json.Edit";
        if (TypeKey is "Config.Json.Query" or "Config.Ini.Query")
        {
            var value = json ? JsonPatch.Read(Text("Text"), Text("JSON Pointer"), token)
                : IniPatch.Read(Text("Text"), Text("Section"), Text("Key"), token);
            WriteOutput(json ? "JSON Value" : "Value", value ?? string.Empty);
            WriteOutput("Exists", value is not null);
            return;
        }
        var operation = Text("Operation");
        if (operation is not "Set" and not "Delete")
        {
            throw new InvalidDataException("Invalid structured configuration operation.");
        }
        SetResult(json
            ? JsonPatch.Apply(Text("Text"), Text("JSON Pointer"), Text("JSON Value"), operation == "Delete",
                Boolean("Check Old Value"), Text("Expected Old JSON"), Boolean("Allow Create"), token)
            : IniPatch.Apply(Text("Text"), Text("Section"), Text("Key"), Text("Value"), operation == "Delete",
                Boolean("Check Old Value"), Text("Expected Old Value"), Boolean("Allow Create"), token));
    }
}