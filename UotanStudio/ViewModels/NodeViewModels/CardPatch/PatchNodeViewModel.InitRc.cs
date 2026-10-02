using UotanStudio.Firmware.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private void ExecuteInitRcNode(CancellationToken token)
    {
        if (TypeKey == "Config.InitRc.FindReferences")
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
        if (TypeKey == "Config.InitRc.AddSection")
        {
            SetResult(InitRcPatch.AddSection(Text("Text"), Text("Section Text"), token));
            return;
        }
        var kind = Enum.Parse<InitRcSection>(Text("Section Kind"));
        if (TypeKey == "Config.InitRc.EditHeader")
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
        if (TypeKey == "Config.InitRc.EditArgument")
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
        if (TypeKey == "Config.InitRc.AddCommand")
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
        if (TypeKey == "Config.InitRc.Query")
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