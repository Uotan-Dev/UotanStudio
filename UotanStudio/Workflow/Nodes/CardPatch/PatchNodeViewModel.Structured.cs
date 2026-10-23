using UotanStudio.Firmware.Patching;

namespace UotanStudio.Workflow.Nodes;

public sealed partial class PatchNodeViewModel
{
    private void ExecuteStructuredNode(CancellationToken token)
    {
        var json = TypeKey is "Config.Json.Edit";
        if (Text("Operation") == "Query")
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