using UotanStudio.Firmware.BoundedContexts.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private static void ConfigureInitRcNode(PatchNodeViewModel node)
    {
        if (node.TypeKey == "Patch.InitRcReferences")
        {
            node.AddInput("Text", NodePortType.Text, "", multiline: true, hint: "Node_Patch_InitRcReferencesHint");
            node.AddInput("External Services", NodePortType.Text, "", multiline: true);
            node.AddOutput("References Valid", NodePortType.Boolean);
            node.AddOutput("Services", NodePortType.Any);
            node.AddOutput("Missing Services", NodePortType.Any);
            node.AddOutput("Dynamic References", NodePortType.Any);
            node.AddOutput("Invalid References", NodePortType.Any);
            node.AddOutput("Ambiguous Services", NodePortType.Any);
            node.AddOutput("Count", NodePortType.Number);
            return;
        }
        if (node.TypeKey == "Patch.InitRcAddSection")
        {
            node.AddInput("Text", NodePortType.Text, "", multiline: true, hint: "Node_Patch_InitRcAddSectionHint");
            node.AddInput("Section Text", NodePortType.Text, "", multiline: true);
            node.AddResultOutputs();
            return;
        }
        node.AddInput("Text", NodePortType.Text, "", multiline: true,
            hint: node.TypeKey switch
            {
                "Patch.InitRcAdd" => "Node_Patch_InitRcAddHint",
                "Patch.InitRcHeaderEdit" => "Node_Patch_InitRcHeaderHint",
                "Patch.InitRcArgumentEdit" => "Node_Patch_InitRcArgumentHint",
                _ => "Node_Patch_InitRcHint"
            });
        node.AddInput("Section Kind", NodePortType.Text, "Service",
            choices: node.TypeKey == "Patch.InitRcAdd" ? ["Service", "Action"] : Enum.GetNames<InitRcSection>());
        node.AddInput("Selector", NodePortType.Text, "");
        if (node.TypeKey == "Patch.InitRcHeaderEdit")
        {
            node.AddInput("Replacement", NodePortType.Text, "", multiline: true);
            node.AddOldValueInputs();
            node.AddResultOutputs();
            return;
        }
        if (node.TypeKey == "Patch.InitRcAdd")
        {
            node.AddInput("Statement", NodePortType.Text, "", multiline: true);
            node.AddInput("Identity Arguments", NodePortType.Number, 0d);
            node.AddInput("Expected Sections", NodePortType.Number, 1d);
            node.AddInput("Placement", NodePortType.Text, "End", choices: Enum.GetNames<InitRcInsertion>(), hint: "Node_Patch_InitRcPlacementHint");
            node.AddInput("Anchor Statement", NodePortType.Text, "", multiline: true);
            node.AddResultOutputs();
            return;
        }
        node.AddInput("Directive", NodePortType.Text, "");
        node.AddInput("Expected Sections", NodePortType.Number, 1d);
        node.AddInput("Expected Matches", NodePortType.Number, 1d);
        if (node.TypeKey == "Patch.InitRcArgumentEdit")
        {
            node.AddInput("Argument Index", NodePortType.Number, 0d);
            node.AddInput("Value", NodePortType.Text, "", multiline: true);
            node.AddOldValueInputs();
            node.AddResultOutputs();
            return;
        }
        if (node.TypeKey == "Patch.InitRcEdit")
        {
            node.AddInput("Operation", NodePortType.Text, "Replace", choices: Enum.GetNames<InitRcOperation>());
            node.AddInput("Replacement", NodePortType.Text, "", multiline: true);
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

    private void ExecuteInitRcNode(CancellationToken token)
    {
        if (TypeKey == "Patch.InitRcReferences")
        {
            var report = InitRcPatch.CheckServiceReferences(Text("Text"), Text("External Services"), token);
            WriteOutput("References Valid", report.ReferencesValid);
            WriteOutput("Services", report.Services);
            WriteOutput("Missing Services", report.MissingServices);
            WriteOutput("Dynamic References", report.DynamicReferences);
            WriteOutput("Invalid References", report.InvalidReferences);
            WriteOutput("Ambiguous Services", report.AmbiguousServices);
            WriteOutput("Count", report.References);
            return;
        }
        if (TypeKey == "Patch.InitRcAddSection")
        {
            SetResult(InitRcPatch.AddSection(Text("Text"), Text("Section Text"), token));
            return;
        }
        var kind = Enum.Parse<InitRcSection>(Text("Section Kind"));
        if (TypeKey == "Patch.InitRcHeaderEdit")
        {
            SetResult(InitRcPatch.ReplaceHeader(Text("Text"), kind, Text("Selector"), Text("Replacement"),
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        var sectionsValue = Convert.ToDouble(ReadInput("Expected Sections"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(sectionsValue) || sectionsValue != Math.Truncate(sectionsValue) || sectionsValue is < 0 or > 100000)
        {
            throw new InvalidDataException("Expected section count must be an integer between zero and 100000.");
        }
        var sections = (int)sectionsValue;
        if (TypeKey == "Patch.InitRcArgumentEdit")
        {
            var index = Convert.ToDouble(ReadInput("Argument Index"), System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(index) || index != Math.Truncate(index) || index is < 0 or > 1022)
            {
                throw new InvalidDataException("Argument index must be an integer between zero and 1022.");
            }
            SetResult(InitRcPatch.EditArgument(Text("Text"), kind, Text("Selector"), Text("Directive"), (int)index,
                Text("Value"), Boolean("Check Old Value"), Text("Expected Old Value"), sections, Count(), token));
            return;
        }
        if (TypeKey == "Patch.InitRcAdd")
        {
            var identity = Convert.ToDouble(ReadInput("Identity Arguments"), System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(identity) || identity != Math.Truncate(identity) || identity is < 0 or > 1023)
            {
                throw new InvalidDataException("Identity argument count must be an integer between zero and 1023.");
            }
            SetResult(InitRcPatch.Add(Text("Text"), kind, Text("Selector"), Text("Statement"), (int)identity, sections, token,
                Enum.Parse<InitRcInsertion>(Text("Placement")), Text("Anchor Statement")));
            return;
        }
        if (TypeKey == "Patch.InitRcQuery")
        {
            var values = InitRcPatch.Read(Text("Text"), kind, Text("Selector"), Text("Directive"), sections, Count(), token);
            WriteOutput("Values", values);
            WriteOutput("Value", values.Count == 1 ? values[0] : null);
            WriteOutput("Count", values.Count);
        }
        else
        {
            SetResult(InitRcPatch.Apply(Text("Text"), kind, Text("Selector"), Text("Directive"),
                Operation<InitRcOperation>(), Text("Replacement"), Boolean("Check Old Value"),
                Text("Expected Old Value"), sections, Count(), token));
        }
    }
}