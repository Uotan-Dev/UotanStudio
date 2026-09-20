using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

public static class Disassembler
{
    /// <summary>
    /// Disassembles the entire DEX file into text.
    /// <para>反汇编整个 DEX 文件为文本。</para>
    /// </summary>
    public static string DisassembleAll(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var sb = new StringBuilder(4096);
        sb.Append("# DEX v").Append(dex.Header.Version)
          .Append(" | classes: ").Append(dex.Classes.Count)
          .Append(" | strings: ").Append(dex.Strings.Count)
          .Append(" | types: ").Append(dex.Types.Count)
          .Append(" | methods: ").Append(dex.Methods.Count)
          .AppendLine();

        foreach (var cls in dex.Classes)
        {
            sb.AppendLine();
            AppendDisassembleClass(sb, dex, cls);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Disassembles a single class into smali-style text.
    /// <para>反汇编单个类为 smali 风格文本。</para>
    /// </summary>
    public static string DisassembleClass(DexFile dex, ClassDefinition cls)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(cls);
        var sb = new StringBuilder(1024);
        AppendDisassembleClass(sb, dex, cls);
        return sb.ToString();
    }

    private static void AppendDisassembleClass(StringBuilder sb, DexFile dex, ClassDefinition cls)
    {
        var access = AccessFlags.Format(cls.AccessFlags);
        var className = dex.FormatType(cls.TypeIndex);
        sb.Append(".class ").Append(access).Append(access.Length > 0 ? " " : string.Empty).AppendLine(className);

        if (cls.SuperclassIndex >= 0)
        {
            sb.Append(".super ").AppendLine(dex.FormatType(cls.SuperclassIndex));
        }
        foreach (var i in cls.InterfaceTypeIndices)
        {
            sb.Append(".implements ").AppendLine(dex.FormatType(i));
        }
        if (cls.SourceFileIndex >= 0)
        {
            sb.Append(".source \"").Append(dex.GetString(cls.SourceFileIndex)).AppendLine("\"");
        }


        if (cls.Annotations?.ClassAnnotations is { IsEmpty: false } classAnnotations)
        {
            sb.Append(FormatAnnotationSet(dex, classAnnotations, string.Empty));
        }


        for (var i = 0; i < cls.StaticFields.Count; i++)
        {
            var f = cls.StaticFields[i];
            var field = dex.GetField(f.FieldIndex);
            if (field == null) continue;
            var fieldAccess = AccessFlags.Format(f.AccessFlags);
            sb.Append(".field ").Append(fieldAccess).Append(fieldAccess.Length > 0 ? " " : string.Empty)
              .Append(dex.GetString(field.NameIndex)).Append(':').Append(dex.FormatType(field.TypeIndex));
            if (cls.StaticValues is { Count: > 0 } staticValues && i < staticValues.Count)
            {
                sb.Append(" = ").Append(FormatEncodedValue(dex, staticValues[i]));
            }
            sb.AppendLine();
            AppendFieldAnnotations(sb, dex, cls, f.FieldIndex);
        }
        foreach (var f in cls.InstanceFields)
        {
            var field = dex.GetField(f.FieldIndex);
            if (field == null) continue;
            var fieldAccess = AccessFlags.Format(f.AccessFlags);
            sb.Append(".field ").Append(fieldAccess).Append(fieldAccess.Length > 0 ? " " : string.Empty)
              .Append(dex.GetString(field.NameIndex)).Append(':').AppendLine(dex.FormatType(field.TypeIndex));
            AppendFieldAnnotations(sb, dex, cls, f.FieldIndex);
        }

        foreach (var m in cls.DirectMethods.Concat(cls.VirtualMethods))
        {
            sb.AppendLine();
            AppendDisassembleMethod(sb, dex, m, cls);
        }
    }

    private static void AppendFieldAnnotations(StringBuilder sb, DexFile dex, ClassDefinition cls, int fieldIndex)
    {
        var annotations = cls.Annotations?.FieldAnnotations.FirstOrDefault(fa => fa.FieldIndex == fieldIndex)?.Annotations;
        if (annotations is { IsEmpty: false })
        {
            sb.Append(FormatAnnotationSet(dex, annotations, string.Empty));
        }
    }

    /// <summary>
    /// Disassembles a single method into smali-style text (including instructions and branch labels).
    /// <para>反汇编单个方法为 smali 风格文本（含指令与分支标签）。</para>
    /// </summary>
    public static string DisassembleMethod(DexFile dex, EncodedMethod encodedMethod, ClassDefinition? cls = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(encodedMethod);
        var method = dex.GetMethod(encodedMethod.MethodIndex);
        if (method == null) return string.Empty;

        var sb = new StringBuilder(512);
        AppendDisassembleMethod(sb, dex, encodedMethod, cls);
        return sb.ToString();
    }

    private static void AppendDisassembleMethod(StringBuilder sb, DexFile dex, EncodedMethod encodedMethod, ClassDefinition? cls)
    {
        var method = dex.GetMethod(encodedMethod.MethodIndex);
        if (method == null) return;

        var proto = dex.GetProto(method.ProtoIndex);
        var access = AccessFlags.Format(encodedMethod.AccessFlags);

        sb.Append(".method ").Append(access).Append(access.Length > 0 ? " " : string.Empty)
          .Append(dex.GetString(method.NameIndex)).Append('(');
        if (proto != null)
        {
            for (var i = 0; i < proto.ParameterTypeIndices.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(dex.FormatType(proto.ParameterTypeIndices[i]));
            }
        }
        sb.Append(')');
        sb.Append(proto != null ? dex.FormatType(proto.ReturnTypeIndex) : "?").AppendLine();

        var code = encodedMethod.Code;
        if (code == null)
        {
            AppendMethodAnnotations(sb, dex, cls, encodedMethod.MethodIndex, code: null);
            sb.AppendLine(".end method");
            return;
        }


        var instructions = code.Instructions.Count > 0
            ? code.Instructions
            : Instructions.InstructionDecoder.Decode(code.RawInsns, dex);

        sb.Append("    .registers ").AppendLine(code.RegistersSize.ToString());
        sb.Append("    .locals ").AppendLine((code.RegistersSize - code.InsSize).ToString());


        AppendMethodAnnotations(sb, dex, cls, encodedMethod.MethodIndex, code);


        var labels = new HashSet<int>();
        foreach (var insn in instructions)
        {
            foreach (var operand in insn.Operands)
            {
                if (operand is BranchTargetOperand target) labels.Add(target.Offset);
            }
        }


        var tryStarts = new Dictionary<int, List<string>>();
        var tryEnds = new Dictionary<int, (List<string> Labels, List<string> CatchLines)>();
        var catchLabels = new Dictionary<int, string>();
        for (var i = 0; i < code.Tries.Count; i++)
        {
            var t = code.Tries[i];
            var startOff = t.StartAddress * 2;
            var endOff = (t.StartAddress + t.InsnCount) * 2;
            var startLabel = $":try_start_{i}";
            var endLabel = $":try_end_{i}";
            if (!tryStarts.TryGetValue(startOff, out var starts)) tryStarts[startOff] = starts = [];
            starts.Add(startLabel);
            if (!tryEnds.TryGetValue(endOff, out var ends)) tryEnds[endOff] = ends = ([], []);
            ends.Labels.Add(endLabel);
            labels.Add(startOff);
            labels.Add(endOff);

            var handler = t.HandlerIndex >= 0 && t.HandlerIndex < code.Handlers.Count ? code.Handlers[t.HandlerIndex] : null;
            if (handler == null) continue;
            foreach (var entry in handler.Entries)
            {
                var hOff = entry.Address * 2;
                if (!catchLabels.TryGetValue(hOff, out var hLabel))
                {
                    hLabel = $":catch_{hOff:X4}";
                    catchLabels[hOff] = hLabel;
                    labels.Add(hOff);
                }
                ends.CatchLines.Add($".catch {dex.GetType(entry.TypeIndex)} {{{startLabel} .. {endLabel}}} {hLabel}");
            }
            if (handler.HasCatchAll)
            {
                var hOff = handler.CatchAllAddress * 2;
                if (!catchLabels.TryGetValue(hOff, out var hLabel))
                {
                    hLabel = $":catchall_{hOff:X4}";
                    catchLabels[hOff] = hLabel;
                    labels.Add(hOff);
                }
                ends.CatchLines.Add($".catchall {{{startLabel} .. {endLabel}}} {hLabel}");
            }
        }

        foreach (var insn in instructions)
        {
            if (labels.Contains(insn.Offset))
            {
                if (tryStarts.TryGetValue(insn.Offset, out var starts))
                {
                    foreach (var s in starts) sb.Append("  ").AppendLine(s);
                }
                if (catchLabels.TryGetValue(insn.Offset, out var catchLabel))
                {
                    sb.Append("  ").AppendLine(catchLabel);
                }
                sb.Append("  :label_").Append(insn.Offset.ToString("X4")).AppendLine();
                if (tryEnds.TryGetValue(insn.Offset, out var ends))
                {
                    foreach (var e in ends.Labels) sb.Append("  ").AppendLine(e);
                    foreach (var c in ends.CatchLines) sb.Append("    ").AppendLine(c);
                }
            }
            sb.Append("    ").Append(insn.Mnemonic).Append(' ');
            AppendOperands(sb, insn);
            sb.AppendLine();
        }

        sb.AppendLine(".end method");
    }

    /// <summary>
    /// Emits method-level and parameter annotations (.annotation / .param).
    /// <para>输出方法级注解与参数注解（.annotation / .param）。</para>
    /// </summary>
    private static void AppendMethodAnnotations(StringBuilder sb, DexFile dex, ClassDefinition? cls, int methodIndex, CodeItem? code)
    {
        if (cls?.Annotations == null) return;

        var methodAnns = cls.Annotations.MethodAnnotations.FirstOrDefault(ma => ma.MethodIndex == methodIndex)?.Annotations;
        if (methodAnns is { IsEmpty: false })
        {
            sb.Append(FormatAnnotationSet(dex, methodAnns, "    "));
        }

        var paramAnns = cls.Annotations.ParameterAnnotations.FirstOrDefault(pa => pa.MethodIndex == methodIndex)?.Parameters;
        if (paramAnns == null || code == null) return;


        var paramBase = code.RegistersSize - code.InsSize;
        for (var i = 0; i < paramAnns.Sets.Count; i++)
        {
            var set = paramAnns.Sets[i];
            if (set is not { IsEmpty: false }) continue;
            foreach (var item in set.Annotations)
            {
                sb.Append("    .param p").Append(paramBase + i).Append(", .annotation ")
                  .Append(VisibilityName(item.Visibility)).Append(' ').Append(dex.GetType(item.TypeIndex)).AppendLine();
                foreach (var element in item.Elements)
                {
                    sb.Append("        ").Append(dex.GetString(element.NameIndex)).Append(" = ")
                      .AppendLine(FormatEncodedValue(dex, element.Value));
                }
                sb.AppendLine("    .end annotation");
                sb.AppendLine("    .end param");
            }
        }
    }

    /// <summary>
    /// Formats an annotation set as smali-style .annotation blocks (with visibility).
    /// <para>把注解集合格式化为 smali 风格 .annotation 块（含可见性）。</para>
    /// </summary>
    private static string FormatAnnotationSet(DexFile dex, AnnotationSet set, string indent)
    {
        var sb = new StringBuilder();
        foreach (var item in set.Annotations)
        {
            sb.Append(indent).Append(".annotation ").Append(VisibilityName(item.Visibility))
              .Append(' ').Append(dex.GetType(item.TypeIndex)).AppendLine();
            foreach (var element in item.Elements)
            {
                sb.Append(indent).Append("    ").Append(dex.GetString(element.NameIndex)).Append(" = ")
                  .AppendLine(FormatEncodedValue(dex, element.Value));
            }
            sb.Append(indent).AppendLine(".end annotation");
        }
        return sb.ToString();
    }

    private static string VisibilityName(int visibility) => visibility switch
    {
        AnnotationVisibility.Runtime => "runtime",
        AnnotationVisibility.System => "system",
        _ => "build"
    };

    /// <summary>
    /// Formats an encoded value as a smali-style literal (annotation element values / field initializers).
    /// <para>把编码值格式化为 smali 风格字面量（注解元素值 / 字段初始值）。</para>
    /// </summary>
    private static string FormatEncodedValue(DexFile dex, EncodedValue value)
    {
        switch (value.Type)
        {
            case EncodedValueType.Byte:
                return value.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture) + "t";
            case EncodedValueType.Short:
                return value.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
            case EncodedValueType.Char:
                {
                    var c = (char)value.AsInt();
                    return "'" + EscapeChar(c) + "'";
                }
            case EncodedValueType.Int:
                return value.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture);
            case EncodedValueType.Long:
                return value.AsLong().ToString(System.Globalization.CultureInfo.InvariantCulture) + "L";
            case EncodedValueType.Float:
                return Convert.ToSingle(value.Value, System.Globalization.CultureInfo.InvariantCulture)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
            case EncodedValueType.Double:
                return Convert.ToDouble(value.Value, System.Globalization.CultureInfo.InvariantCulture)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case EncodedValueType.Boolean:
                return value.AsBoolean() ? "true" : "false";
            case EncodedValueType.Null:
                return "null";
            case EncodedValueType.String:
                return "\"" + EscapeString(dex.GetString(value.AsIndex())) + "\"";
            case EncodedValueType.Type:
                return dex.GetType(value.AsIndex());
            case EncodedValueType.Field:
            case EncodedValueType.Enum:
                {
                    var field = dex.GetField(value.AsIndex());
                    return field == null
                        ? "?"
                        : $"{dex.GetType(field.ClassIndex)}->{dex.GetString(field.NameIndex)}:{dex.GetType(field.TypeIndex)}";
                }
            case EncodedValueType.Method:
                {
                    var method = dex.GetMethod(value.AsIndex());
                    if (method == null) return "?";
                    var mproto = dex.GetProto(method.ProtoIndex);
                    var parameters = mproto == null ? string.Empty
                        : string.Concat(mproto.ParameterTypeIndices.Select(dex.GetType));
                    var returnType = mproto == null ? "?" : dex.GetType(mproto.ReturnTypeIndex);
                    return $"{dex.GetType(method.ClassIndex)}->{dex.GetString(method.NameIndex)}({parameters}){returnType}";
                }
            case EncodedValueType.MethodType:
                {
                    var proto = dex.GetProto(value.AsIndex());
                    if (proto == null) return "?";
                    var parameters = string.Concat(proto.ParameterTypeIndices.Select(dex.GetType));
                    return $"({parameters}){dex.GetType(proto.ReturnTypeIndex)}";
                }
            case EncodedValueType.Array:
                {
                    var items = value.AsArray() ?? [];
                    return "{" + string.Join(", ", items.Select(v => FormatEncodedValue(dex, v))) + "}";
                }
            case EncodedValueType.Annotation:
                {
                    var item = value.Value as AnnotationItem;
                    if (item == null) return "?";
                    var sb = new StringBuilder();
                    sb.Append('@').Append(dex.GetType(item.TypeIndex)).Append('{');
                    for (var i = 0; i < item.Elements.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(dex.GetString(item.Elements[i].NameIndex)).Append('=')
                          .Append(FormatEncodedValue(dex, item.Elements[i].Value));
                    }
                    sb.Append('}');
                    return sb.ToString();
                }
            case EncodedValueType.MethodHandle:
                return "method-handle@" + value.AsIndex();
            default:
                return "?";
        }
    }

    private static string EscapeChar(char c) => c switch
    {
        '\\' => "\\\\",
        '\'' => "\\'",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ when c < 0x20 => "\\u" + ((int)c).ToString("X4"),
        _ => c.ToString()
    };

    private static void AppendOperands(StringBuilder sb, Instruction insn)
    {
        if (insn.Operands.Count == 0) return;


        if (insn.Operands[0] is IReadOnlyList<int> regs)
        {
            sb.Append('{');
            for (var i = 0; i < regs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('v').Append(regs[i]);
            }
            sb.Append('}');
            for (var i = 1; i < insn.Operands.Count; i++)
            {
                sb.Append(", ");
                AppendOperand(sb, insn.Operands[i]);
            }
            return;
        }

        for (var i = 0; i < insn.Operands.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            AppendOperand(sb, insn.Operands[i]);
        }
    }

    private static void AppendOperand(StringBuilder sb, object? operand)
    {
        switch (operand)
        {
            case null:
                sb.Append("<null>");
                break;
            case BranchTargetOperand target:
                sb.Append(":label_").Append(target.Offset.ToString("X4"));
                break;
            case LiteralOperand literal:
                AppendNumber(sb, literal.Value);
                break;
            case TypeRefOperand type:
                sb.Append(type.Descriptor);
                break;
            case FieldRefOperand field:
                sb.Append(field.OwnerClass).Append("->").Append(field.Name).Append(':').Append(field.Type);
                break;
            case MethodRefOperand method:
                sb.Append(method.OwnerClass).Append("->").Append(method.Name).Append('(');
                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(method.Parameters[i]);
                }
                sb.Append(')').Append(method.ReturnType);
                break;
            case PayloadOperand payload:
                AppendPayload(sb, payload);
                break;
            case int reg:
                sb.Append('v').Append(reg);
                break;
            case string value:
                sb.Append('"').Append(EscapeString(value)).Append('"');
                break;
            default:
                sb.Append(Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture) ?? "?");
                break;
        }
    }

    private static void AppendNumber(StringBuilder sb, long value)
    {
        sb.Append("#0x").Append(value is >= 0 and <= 0xFFFF ? value.ToString("X") : value.ToString("X8"));
    }

    private static void AppendPayload(StringBuilder sb, PayloadOperand payload)
    {
        sb.Append(payload.Kind);
        if (payload.Kind == "packed-switch")
        {
            sb.Append(" first_key=").Append(payload.FirstKey).Append(" targets=[");
            AppendValueList(sb, payload.Values);
            sb.Append(']');
        }
        else if (payload.Kind == "sparse-switch")
        {
            sb.Append(" entries=").Append(payload.Values.Length);
        }
        else if (payload.Kind == "fill-array-data")
        {
            sb.Append(" width=").Append(payload.FirstKey).Append(" units=").Append(payload.Values.Length);
        }
        else
        {
            sb.Append('[');
            AppendValueList(sb, payload.Values);
            sb.Append(']');
        }
    }

    private static void AppendValueList(StringBuilder sb, int[] values)
    {
        var count = Math.Min(values.Length, 8);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(values[i]);
        }
    }

    private static string EscapeString(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}