using UotanStudio.Firmware.Patching;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    /// <summary>
    /// Dispatches the unified configuration editor by its Format and Operation inputs. The format selects the
    /// addressing model and the underlying patch API, so a single node edits every supported configuration file
    /// without a per-file node type.
    /// <para>按 Format 与 Operation 输入分派统一配置编辑器。格式决定寻址模型与底层补丁 API，
    /// 因此单个节点即可编辑全部受支持的配置文件，无需为每种文件单独设节点类型。</para>
    /// </summary>
    private void ExecuteConfigEdit(CancellationToken token)
    {
        var format = Text("Format").Trim();
        switch (format)
        {
            case "Json":
                ExecuteJsonConfig(token);
                return;
            case "Ini":
                ExecuteIniConfig(token);
                return;
            case "Xml":
                ExecuteXmlConfig(token);
                return;
            case "Property":
                ExecutePropertyConfig(token);
                return;
            case "Fstab":
                ExecuteFstabUnified(token);
                return;
            case "InitRc":
                ExecuteInitRcUnified(token);
                return;
            case "FileContexts":
                ExecuteFileContextUnified(token);
                return;
            case "FsConfig":
                ExecuteFsConfigUnified(token);
                return;
            case "Text":
                ExecuteTextConfig(token);
                return;
            default:
                throw new InvalidDataException($"Unsupported configuration format '{format}'.");
        }
    }

    private void ExecuteJsonConfig(CancellationToken token)
    {
        var text = Text("Text");
        var pointer = Text("Target");
        if (Text("Operation") == "Query")
        {
            var value = JsonPatch.Read(text, pointer, token);
            WriteOutput("Value", value ?? string.Empty);
            WriteOutput("Exists", value is not null);
            return;
        }
        SetResult(JsonPatch.Apply(text, pointer, Text("Value"), Text("Operation") == "Delete",
            Boolean("Check Old Value"), Text("Expected Old Value"), Boolean("Allow Create"), token));
    }

    private void ExecuteIniConfig(CancellationToken token)
    {
        var text = Text("Text");
        var section = Text("Section");
        var key = Text("Key");
        if (Text("Operation") == "Query")
        {
            var value = IniPatch.Read(text, section, key, token);
            WriteOutput("Value", value ?? string.Empty);
            WriteOutput("Exists", value is not null);
            return;
        }
        SetResult(IniPatch.Apply(text, section, key, Text("Value"), Text("Operation") == "Delete",
            Boolean("Check Old Value"), Text("Expected Old Value"), Boolean("Allow Create"), token));
    }

    private void ExecuteXmlConfig(CancellationToken token)
    {
        var text = Text("Text");
        if (Text("Operation") == "Query")
        {
            var values = XmlPatch.Query(text, Text("Target"), Text("Namespaces"), Count());
            WriteOutput("Values", values);
            WriteOutput("Value", values.Count == 1 ? values[0] : null);
            WriteOutput("Count", values.Count);
            return;
        }
        SetResult(XmlPatch.Apply(text, Text("Target"), Text("Namespaces"),
            Operation<XmlPatchOperation>(), Text("Value"), Text("Attribute Name"), Count(),
            Boolean("Check Old Value"), Text("Expected Old Value")));
    }

    private void ExecutePropertyConfig(CancellationToken token)
    {
        var text = Text("Text");
        var key = Text("Key");
        if (Text("Operation") == "Query")
        {
            var value = AndroidPropertyPatch.Read(text, key);
            WriteOutput("Value", value ?? string.Empty);
            WriteOutput("Exists", value is not null);
            return;
        }
        SetResult(AndroidPropertyPatch.Apply(text, key, Text("Value"), Text("Operation") == "Delete",
            Boolean("Check Old Value"), Text("Expected Old Value"), Boolean("Allow Create")));
    }

    private void ExecuteTextConfig(CancellationToken token)
    {
        var operation = Text("Operation").Trim();
        var textOperation = operation switch
        {
            "Set" or "Replace" => TextPatchOperation.Replace,
            "Delete" => TextPatchOperation.Delete,
            "InsertBefore" => TextPatchOperation.InsertBefore,
            "InsertAfter" => TextPatchOperation.InsertAfter,
            _ => throw new InvalidDataException($"Unsupported text configuration operation '{operation}'.")
        };
        SetResult(TextPatch.Apply(Text("Text"), Text("Find"), Text("Replacement"),
            textOperation, Boolean("Regex"), Count(), token));
    }

    private void ExecuteFstabUnified(CancellationToken token)
    {
        var text = Text("Text");
        var mountPoint = Text("Mount Point");
        var fileSystem = Text("File System Type");
        var operation = Text("Operation");
        if (operation == "Query")
        {
            var values = FstabPatch.Read(text, mountPoint, fileSystem, FstabFieldOf(), Count(), token);
            WriteOutput("Values", values);
            WriteOutput("Value", values.Count == 1 ? values[0] : null);
            WriteOutput("Count", values.Count);
            return;
        }
        if (operation == "Add")
        {
            SetResult(FstabPatch.Add(text, Text("Block Device"), mountPoint, fileSystem,
                Text("Mount Options"), Text("Fs Mgr Flags"), token));
            return;
        }
        SetResult(FstabPatch.Apply(text, mountPoint, fileSystem, FstabFieldOf(),
            FstabOperationOf(), Text("Value"), Text("Flag Name"),
            Boolean("Check Old Value"), Text("Expected Old Value"), Count(), token));
    }

    private FstabField FstabFieldOf()
        => Enum.TryParse<FstabField>(Text("Field"), out var value) && Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException("Unsupported fstab field.");

    private FstabOperation FstabOperationOf()
        => Enum.TryParse<FstabOperation>(Text("Fstab Operation"), out var value) && Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException("Unsupported fstab operation.");

    private void ExecuteInitRcUnified(CancellationToken token)
    {
        var operation = Text("Operation");
        if (operation == "FindReferences")
        {
            ExecuteInitRcFindReferences(token);
            return;
        }
        var text = Text("Text");
        var kind = InitRcKindOf();
        var selector = Text("Selector");
        var sections = ExpectedSectionCount();
        if (operation == "Query")
        {
            var values = InitRcPatch.Read(text, kind, selector, Text("Directive"), sections, Count(), token);
            WriteOutput("Values", values);
            WriteOutput("Value", values.Count == 1 ? values[0] : null);
            WriteOutput("Count", values.Count);
            return;
        }
        if (operation == "AddSection")
        {
            SetResult(InitRcPatch.AddSection(text, Text("Section Text"), token));
            return;
        }
        if (operation == "AddStatement")
        {
            SetResult(InitRcPatch.Add(text, kind, selector, Text("Statement"), IdentityArgumentCount(), sections, token,
                InitRcPlacementOf(), Text("Anchor Statement")));
            return;
        }
        if (operation == "ReplaceHeader")
        {
            SetResult(InitRcPatch.ReplaceHeader(text, kind, selector, Text("Value"),
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        if (operation == "Add")
        {
            SetResult(InitRcPatch.Add(text, kind, selector, Text("Value"), 1, sections, token,
                InitRcInsertion.End, string.Empty));
            return;
        }
        SetResult(InitRcPatch.Apply(text, kind, selector, Text("Directive"),
            operation == "Delete" ? InitRcOperation.Delete : InitRcOperation.Replace,
            Text("Value"), Boolean("Check Old Value"), Text("Expected Old Value"), sections, Count(), token));
    }

    private int IdentityArgumentCount()
    {
        var value = Convert.ToDouble(ReadInput("Identity Arguments"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 0 or > 1023)
        {
            throw new InvalidDataException("Identity argument count must be an integer between zero and 1023.");
        }
        return (int)value;
    }

    private InitRcInsertion InitRcPlacementOf()
        => Enum.TryParse<InitRcInsertion>(Text("Placement"), out var value) && Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException("Unsupported init.rc insertion placement.");

    private InitRcSection InitRcKindOf()
        => Enum.TryParse<InitRcSection>(Text("Section Kind"), out var value) && Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException("Unsupported init.rc section kind.");

    private int ExpectedSectionCount()
    {
        var value = Convert.ToDouble(ReadInput("Expected Matches"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 0 or > 100000)
        {
            throw new InvalidDataException("Expected section count must be an integer between zero and 100000.");
        }
        return (int)value;
    }

    private void ExecuteFileContextUnified(CancellationToken token)
    {
        var text = Text("Text");
        var expression = Text("Path Expression");
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
        if (operation == "Query")
        {
            WriteOutput("Context", FileContextPatch.Read(text, expression, kind, token));
            return;
        }
        if (operation == "Add")
        {
            SetResult(FileContextPatch.Add(text, expression, kind, Text("Context"), token));
            return;
        }
        if (operation == "Delete")
        {
            SetResult(FileContextPatch.Delete(text, expression, kind,
                Boolean("Check Old Value"), Text("Expected Old Value"), token));
            return;
        }
        SetResult(FileContextPatch.Edit(text, expression, kind, Text("Context"),
            Boolean("Check Old Value"), Text("Expected Old Value"), token));
    }

    private void ExecuteFsConfigUnified(CancellationToken token)
    {
        var text = Text("Text");
        var path = Text("Image Path");
        var operation = Text("Operation");
        if (operation == "Query")
        {
            var entry = FsConfigPatch.Read(text, path, token);
            WriteOutput("UID", entry.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteOutput("GID", entry.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteOutput("Mode", "0" + Convert.ToString(entry.Mode, 8).PadLeft(3, '0'));
            WriteOutput("Text", text);
            return;
        }
        if (operation == "Add")
        {
            SetResult(FsConfigPatch.Add(text, path, Text("UID"), Text("GID"), Text("Mode"), token));
            return;
        }
        if (operation == "Delete")
        {
            SetResult(FsConfigPatch.Delete(text, path, Boolean("Check Old Value"),
                Text("Expected UID"), Text("Expected GID"), Text("Expected Mode"), token));
            return;
        }
        SetResult(FsConfigPatch.Edit(text, path, Text("UID"), Text("GID"), Text("Mode"),
            Boolean("Check Old Value"), Text("Expected UID"), Text("Expected GID"),
            Text("Expected Mode"), token));
    }
}