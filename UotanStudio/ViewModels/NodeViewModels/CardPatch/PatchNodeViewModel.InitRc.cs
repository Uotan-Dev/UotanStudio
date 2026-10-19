using UotanStudio.Firmware.Patching;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    private const string InitRcEditTypeKey = "Config.InitRc.Edit";

    /// <summary>
    /// Routes both the legacy per-operation TypeKeys and the unified <c>Config.InitRc.Edit</c> node onto one shared body
    /// per operation, so the two registrations cannot drift in parsing, validation or output writing.
    /// <para>把旧的按操作拆分的 TypeKey 与统一的 <c>Config.InitRc.Edit</c> 节点分派到每种操作唯一的共享实现，两个注册入口在解析、校验与输出写入上不会各自漂移。</para>
    /// </summary>
    private void ExecuteInitRcNode(CancellationToken token)
    {
        switch (TypeKey)
        {
            case InitRcEditTypeKey:
                ExecuteInitRcEdit(token);
                break;
            case "Config.InitRc.Query":
                ExecuteInitRcQuery(token);
                break;
            case "Config.InitRc.FindReferences":
                ExecuteInitRcFindReferences(token);
                break;
            case "Config.InitRc.EditCommand":
                ExecuteInitRcEditCommand(token);
                break;
            case "Config.InitRc.EditArgument":
                ExecuteInitRcEditArgument(token);
                break;
            case "Config.InitRc.EditHeader":
                ExecuteInitRcEditHeader(token);
                break;
            case "Config.InitRc.AddSection":
                ExecuteInitRcAddSection(token);
                break;
            case "Config.InitRc.AddCommand":
                ExecuteInitRcAddCommand(token);
                break;
            default:
                throw new InvalidDataException("Unsupported init.rc node type.");
        }
    }

    /// <summary>
    /// Routes the unified node by its <c>Operation</c> input. Every branch reads exactly the ports of its legacy node,
    /// so validation bounds, accepted inputs and written outputs stay identical to the seven separate registrations.
    /// <para>按 <c>Operation</c> 输入分派统一节点。每个分支只读取其旧节点对应的端口，因此校验边界、接受的输入与写入的输出与七个独立注册完全一致。</para>
    /// </summary>
    private void ExecuteInitRcEdit(CancellationToken token)
    {
        switch (Text("Operation"))
        {
            case "Query":
                ExecuteInitRcQuery(token);
                break;
            case "FindReferences":
                ExecuteInitRcFindReferences(token);
                break;
            case "EditCommand":
                ExecuteInitRcEditCommand(token);
                break;
            case "EditArgument":
                ExecuteInitRcEditArgument(token);
                break;
            case "EditHeader":
                ExecuteInitRcEditHeader(token);
                break;
            case "AddSection":
                ExecuteInitRcAddSection(token);
                break;
            case "AddCommand":
                ExecuteInitRcAddCommand(token);
                break;
            default:
                throw new InvalidDataException("Unsupported init.rc operation.");
        }
    }

    private void ExecuteInitRcFindReferences(CancellationToken token)
    {
        var report = InitRcPatch.CheckServiceReferences(Text("Text"), Text("External Services"), token);
        WriteOutput("References Valid", report.ReferencesValid);
        WriteOutput("Services", report.Services);
        WriteOutput("Missing Services", report.MissingServices);
        WriteOutput("Dynamic References", report.DynamicReferences);
        WriteOutput("Invalid References", report.InvalidReferences);
        WriteOutput("Ambiguous Services", report.AmbiguousServices);
        WriteOutput("Count", report.References);
    }

    private void ExecuteInitRcAddSection(CancellationToken token)
        => SetResult(InitRcPatch.AddSection(Text("Text"), Text("Section Text"), token));

    private void ExecuteInitRcEditHeader(CancellationToken token)
    {
        SetResult(InitRcPatch.ReplaceHeader(Text("Text"), InitRcKind(), Text("Selector"), Text("Replacement"),
            Boolean("Check Old Value"), Text("Expected Old Value"), token));
    }

    private void ExecuteInitRcEditArgument(CancellationToken token)
    {
        var sections = InitRcExpectedSections();
        var index = InitRcArgumentIndex();
        SetResult(InitRcPatch.EditArgument(Text("Text"), InitRcKind(), Text("Selector"), Text("Directive"), index,
            Text("Value"), Boolean("Check Old Value"), Text("Expected Old Value"), sections, Count(), token));
    }

    private void ExecuteInitRcAddCommand(CancellationToken token)
    {
        var sections = InitRcExpectedSections();
        var identity = Convert.ToDouble(ReadInput("Identity Arguments"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(identity) || identity != Math.Truncate(identity) || identity is < 0 or > 1023)
        {
            throw new InvalidDataException("Identity argument count must be an integer between zero and 1023.");
        }
        SetResult(InitRcPatch.Add(Text("Text"), InitRcKind(), Text("Selector"), Text("Statement"), (int)identity, sections, token,
            Enum.Parse<InitRcInsertion>(Text("Placement")), Text("Anchor Statement")));
    }

    private void ExecuteInitRcQuery(CancellationToken token)
    {
        var sections = InitRcExpectedSections();
        var values = InitRcPatch.Read(Text("Text"), InitRcKind(), Text("Selector"), Text("Directive"), sections, Count(), token);
        WriteOutput("Values", values);
        WriteOutput("Value", values.Count == 1 ? values[0] : null);
        WriteOutput("Count", values.Count);
    }

    private void ExecuteInitRcEditCommand(CancellationToken token)
    {
        var sections = InitRcExpectedSections();
        SetResult(InitRcPatch.Apply(Text("Text"), InitRcKind(), Text("Selector"), Text("Directive"),
            InitRcEditCommandMode(), Text("Replacement"), Boolean("Check Old Value"),
            Text("Expected Old Value"), sections, Count(), token));
    }

    /// <summary>
    /// Reads the replace/delete mode of a body-statement edit. The unified node carries it on <c>Edit Operation</c> so the
    /// <c>Operation</c> input stays the dispatch selector; the legacy <c>Config.InitRc.EditCommand</c> node keeps <c>Operation</c>.
    /// <para>读取正文语句编辑的替换/删除模式。统一节点把它放在 <c>Edit Operation</c> 上，使 <c>Operation</c> 输入保持为分派选择器；
    /// 旧 <c>Config.InitRc.EditCommand</c> 节点仍使用 <c>Operation</c>。</para>
    /// </summary>
    private InitRcOperation InitRcEditCommandMode()
    {
        var mode = Text(TypeKey == InitRcEditTypeKey ? "Edit Operation" : "Operation");
        if (!Enum.TryParse<InitRcOperation>(mode, out var result) || !Enum.IsDefined(result))
        {
            throw new InvalidDataException("Invalid patch operation.");
        }
        return result;
    }

    private InitRcSection InitRcKind() => Enum.Parse<InitRcSection>(Text("Section Kind"));

    private int InitRcExpectedSections()
    {
        var sectionsValue = Convert.ToDouble(ReadInput("Expected Sections"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(sectionsValue) || sectionsValue != Math.Truncate(sectionsValue) || sectionsValue is < 0 or > 100000)
        {
            throw new InvalidDataException("Expected section count must be an integer between zero and 100000.");
        }
        return (int)sectionsValue;
    }

    private int InitRcArgumentIndex()
    {
        var index = Convert.ToDouble(ReadInput("Argument Index"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(index) || index != Math.Truncate(index) || index is < 0 or > 1022)
        {
            throw new InvalidDataException("Argument index must be an integer between zero and 1022.");
        }
        return (int)index;
    }
}