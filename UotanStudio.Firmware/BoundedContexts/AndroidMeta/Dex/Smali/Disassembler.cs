using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

/// <summary>
/// Renders parsed DEX structures as smali-style text: class and member headers, annotations, encoded values,
/// labels and decoded instructions.
/// <para>把已解析的 DEX 结构输出为 smali 风格文本：类与成员声明、注解、编码值、标签以及解码后的指令。</para>
/// </summary>
public static partial class Disassembler
{
    /// <remarks>
    /// Unreferenced switch tables use project-specific .packed-switch-raw or .sparse-switch-raw blocks,
    /// retaining signed code-unit offsets without inventing a case-label origin.
    /// <para>无引用 switch 表使用项目专用的 .packed-switch-raw 或 .sparse-switch-raw 块，保留有符号代码单元偏移而不虚构标签起点。</para>
    /// </remarks>
    /// <summary>
    /// Disassembles the entire DEX file into text.
    /// <para>反汇编整个 DEX 文件为文本。</para>
    /// </summary>
    /// <param name="dex">
    /// Parsed image whose header and class, string, type, field and method tables supply every printed value.
    /// <para>已解析的镜像，其头部与类、字符串、字段、类型、方法表提供全部输出内容。</para>
    /// </param>
    /// <returns>
    /// A leading <c># DEX v</c> comment holding the header version plus the class, string, type and method counts,
    /// then one blank-line separated block per entry of <see cref="DexFile.Classes"/>. The whole text is built in
    /// memory, so its size grows with the input. Out-of-range table indices print as <c>"?"</c> or as an empty
    /// string, so malformed input still yields text instead of an error.
    /// <para>以 <c># DEX v</c> 注释开头，包含头部版本号以及类、字符串、类型与方法计数；随后按 <see cref="DexFile.Classes"/>
    /// 顺序为每个条目输出一个以空行分隔的块。全部文本在内存中构造，因此大小随输入增长。越界的表索引打印为 <c>"?"</c>
    /// 或空字符串，畸形输入依然产出文本而不会报错。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    /// <example>
    /// <code>
    /// var dex = DexFile.Load("classes.dex");
    /// string text = Disassembler.DisassembleAll(dex);
    /// </code>
    /// </example>
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
    /// <param name="dex">Tables used to resolve the type, string, method and proto references of the class and its
    /// members. <para>用于解析该类及其成员所指向的类型、字符串、方法与 proto 引用的各张表。</para></param>
    /// <param name="cls">Class model to render; it is printed from its own data and does not have to be a member of
    /// <paramref name="dex"/>. <para>要渲染的类模型；按其自身数据输出，不要求属于 <paramref name="dex"/>。</para></param>
    /// <returns>
    /// The <c>.class</c> line with formatted access flags, a <c>.super</c> line only when the superclass index is
    /// non-negative, one <c>.implements</c> line per interface, a <c>.source</c> line only when the source index is
    /// non-negative, the class annotation blocks, then the static fields (each followed by its static value when
    /// the static-value array has a slot for it) and the instance fields, and finally every direct and virtual
    /// method. A field whose index resolves to no <c>field_ids</c> entry is skipped without notice, and the whole
    /// block is built in memory. <para>输出带格式化访问标志的 <c>.class</c> 行；仅当父类索引非负时输出 <c>.super</c> 行；
    /// 每个接口输出一行 <c>.implements</c>；仅当源文件索引非负时输出 <c>.source</c> 行；随后是类级注解块、静态字段
    /// （静态值数组存在对应槽位时附带其初始值）与实例字段，最后是全部直接方法与虚方法。索引找不到 <c>field_ids</c>
    /// 条目的字段会被静默跳过，整块文本在内存中构造。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="cls"/> is
    /// <see langword="null"/>. <para><paramref name="dex"/> 或 <paramref name="cls"/> 为 <see langword="null"/>。</para></exception>
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
        var access = AccessFlags.FormatClass(cls.AccessFlags);
        var className = dex.GetType(cls.TypeIndex);
        sb.Append(".class ").Append(access).Append(access.Length > 0 ? " " : string.Empty).AppendLine(className);

        if (cls.SuperclassIndex >= 0)
        {
            sb.Append(".super ").AppendLine(dex.GetType(cls.SuperclassIndex));
        }
        foreach (var i in cls.InterfaceTypeIndices)
        {
            sb.Append(".implements ").AppendLine(dex.GetType(i));
        }
        if (cls.SourceFileIndex >= 0)
        {
            sb.Append(".source \"").Append(EscapeString(dex.GetString(cls.SourceFileIndex))).AppendLine("\"");
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
            var fieldAccess = AccessFlags.FormatField(f.AccessFlags);
            sb.Append(".field ").Append(fieldAccess).Append(fieldAccess.Length > 0 ? " " : string.Empty)
              .Append(dex.GetString(field.NameIndex)).Append(':').Append(dex.GetType(field.TypeIndex));
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
            var fieldAccess = AccessFlags.FormatField(f.AccessFlags);
            sb.Append(".field ").Append(fieldAccess).Append(fieldAccess.Length > 0 ? " " : string.Empty)
              .Append(dex.GetString(field.NameIndex)).Append(':').AppendLine(dex.GetType(field.TypeIndex));
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
            sb.Append(FormatAnnotationSet(dex, annotations, "    "));
            sb.AppendLine(".end field");
        }
    }

    /// <summary>
    /// Disassembles a single method into smali-style text (including instructions and branch labels).
    /// <para>反汇编单个方法为 smali 风格文本（含指令与分支标签）。</para>
    /// </summary>
    /// <param name="dex">Tables used to resolve the method, proto, string and type references of the body.
    /// <para>用于解析方法体中方法、proto、字符串与类型引用的各张表。</para></param>
    /// <param name="encodedMethod">Method entry supplying the <c>method_idx</c>, access flags and code item; its
    /// decoded <see cref="Instruction"/> list is printed as supplied, and the raw code units are decoded on demand
    /// only when that list is empty. <para>提供 <c>method_idx</c>、访问标志与 code item 的方法条目；已解码的
    /// <see cref="Instruction"/> 列表非空时按其原样输出，为空时才按需解码原始代码单元。</para></param>
    /// <param name="cls">Owning class used to look up method-level and parameter annotations; <see langword="null"/>
    /// omits every annotation block while the declaration and bytecode still print.
    /// <para>用于查找方法级注解与参数注解的所属类；为 <see langword="null"/> 时不输出任何注解块，但声明行与字节码照常输出。</para></param>
    /// <returns>
    /// The <c>.method</c> line with formatted flags and parameter and return descriptors; for a method with a
    /// code item the <c>.registers</c> line, annotation blocks, then one instruction per line
    /// with <c>:label_XXXX</c>, <c>:try_start_</c>, <c>:try_end_</c> and <c>:catch</c> labels, closed by
    /// <c>.end method</c>. A body without a code item prints as the declaration followed by <c>.end method</c>.
    /// Label and catch addresses are derived from try and handler offsets that are not validated
    /// against the instruction stream, and the whole text is built in memory. Empty when the
    /// <c>method_idx</c> resolves to no <c>method_ids</c> entry.
    /// <para>输出带格式化标志与参数、返回类型描述符的 <c>.method</c> 行；方法含 code item 时接着输出 <c>.registers</c>
    /// 行、注解块，然后每行一条指令，并插入 <c>:label_XXXX</c>、<c>:try_start_</c>、
    /// <c>:try_end_</c> 与 <c>:catch</c> 标签，以 <c>.end method</c> 结束。没有 code item 的方法体只输出声明行与
    /// <c>.end method</c>。
    /// 标签与 catch 地址取自不与指令流比对的 try 和处理程序偏移，全部文本在内存中构造。<c>method_idx</c>
    /// 找不到 <c>method_ids</c> 条目时返回空字符串。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="encodedMethod"/> is
    /// <see langword="null"/>. <para><paramref name="dex"/> 或 <paramref name="encodedMethod"/> 为 <see langword="null"/>。</para></exception>
    /// <example>
    /// <code>
    /// foreach (var method in cls.DirectMethods)
    /// {
    ///     Console.Write(Disassembler.DisassembleMethod(dex, method, cls));
    /// }
    /// </code>
    /// </example>
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
        var access = AccessFlags.FormatMethod(encodedMethod.AccessFlags);

        sb.Append(".method ").Append(access).Append(access.Length > 0 ? " " : string.Empty)
          .Append(dex.GetString(method.NameIndex)).Append('(');
        if (proto != null)
        {
            for (var i = 0; i < proto.ParameterTypeIndices.Length; i++)
            {
                sb.Append(dex.GetType(proto.ParameterTypeIndices[i]));
            }
        }
        sb.Append(')');
        sb.Append(proto != null ? dex.GetType(proto.ReturnTypeIndex) : "?").AppendLine();

        var code = encodedMethod.Code;
        if (code == null)
        {
            AppendMethodAnnotations(sb, dex, cls, encodedMethod.MethodIndex, encodedMethod.AccessFlags, code: null);
            sb.AppendLine(".end method");
            return;
        }


        var instructions = code.Instructions.Count > 0
            ? code.Instructions
            : Instructions.InstructionDecoder.DecodeForSmali(code.RawInsns, dex);

        sb.Append("    .registers ").AppendLine(code.RegistersSize.ToString());


        AppendMethodAnnotations(sb, dex, cls, encodedMethod.MethodIndex, encodedMethod.AccessFlags, code);
        var debugDirectives = ReadDebugDirectives(dex, code, instructions);


        var labels = new HashSet<int>();
        foreach (var insn in instructions)
        {
            foreach (var operand in insn.Operands)
            {
                if (operand is BranchTargetOperand target) labels.Add(target.Offset);
            }
        }


        var payloadOwners = CollectPayloadLabels(instructions, labels);
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
            if (debugDirectives.TryGetValue(insn.Offset, out var directives))
            {
                foreach (var directive in directives)
                {
                    sb.Append("    ").AppendLine(directive);
                }
            }
            if (insn.Operands.FirstOrDefault() is PayloadOperand)
            {
                AppendPayloadBlock(sb, insn, payloadOwners.Owners.TryGetValue(insn.Offset, out var owner) ? owner : null);
                continue;
            }
            sb.Append("    ").Append(insn.Mnemonic).Append(' ');
            if (payloadOwners.TargetLabels.TryGetValue(insn.Offset, out var targetLabel))
            {
                AppendOperand(sb, insn.Operands[0], dex);
                sb.Append(", ").Append(targetLabel);
            }
            else
            {
                AppendOperands(sb, insn, dex);
            }
            sb.AppendLine();
        }

        if (debugDirectives.TryGetValue(code.RawInsns.Length * 2, out var terminalDirectives))
        {
            foreach (var directive in terminalDirectives)
            {
                sb.Append("    ").AppendLine(directive);
            }
        }
        if (tryEnds.TryGetValue(code.RawInsns.Length * 2, out var terminalEnds))
        {
            foreach (var label in terminalEnds.Labels) sb.Append("  ").AppendLine(label);
            foreach (var line in terminalEnds.CatchLines) sb.Append("    ").AppendLine(line);
        }
        foreach (var copy in payloadOwners.Copies)
        {
            sb.Append("  ").AppendLine(copy.Label);
            AppendPayloadBlock(sb, copy.Payload, copy.Owner);
        }

        sb.AppendLine(".end method");
    }

    /// <summary>
    /// Emits method-level and parameter annotations (.annotation / .param).
    /// <para>输出方法级注解与参数注解（.annotation / .param）。</para>
    /// </summary>
    private static void AppendMethodAnnotations(StringBuilder sb, DexFile dex, ClassDefinition? cls, int methodIndex, uint flags, CodeItem? code)
    {
        var methodAnns = cls?.Annotations?.MethodAnnotations.FirstOrDefault(ma => ma.MethodIndex == methodIndex)?.Annotations;
        if (methodAnns is { IsEmpty: false })
        {
            sb.Append(FormatAnnotationSet(dex, methodAnns, "    "));
        }

        var paramAnns = cls?.Annotations?.ParameterAnnotations.FirstOrDefault(pa => pa.MethodIndex == methodIndex)?.Parameters;
        var method = dex.GetMethod(methodIndex)!;
        var proto = dex.GetProto(method.ProtoIndex);
        if (proto is null)
        {
            return;
        }
        var names = ReadParameterNames(dex, code, proto.ParameterTypeIndices.Length);
        if (paramAnns is not null && paramAnns.Sets.Count > proto.ParameterTypeIndices.Length)
        {
            throw new InvalidDataException("The parameter annotation list exceeds the method signature.");
        }
        var word = (flags & AccessFlags.Static) == 0 ? 1 : 0;
        for (var i = 0; i < proto.ParameterTypeIndices.Length; i++)
        {
            var set = paramAnns is not null && i < paramAnns.Sets.Count ? paramAnns.Sets[i] : null;
            if (names[i] is not null || set is { IsEmpty: false })
            {
                sb.Append("    .param p").Append(word);
                if (names[i] is { } name)
                {
                    sb.Append(", \"").Append(EscapeString(name)).Append('"');
                }
                sb.AppendLine();
                if (set is { IsEmpty: false })
                {
                    sb.Append(FormatAnnotationSet(dex, set, "        "));
                    sb.AppendLine("    .end param");
                }
            }
            word += dex.GetType(proto.ParameterTypeIndices[i]) is "J" or "D" ? 2 : 1;
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
                  .AppendLine(FormatEncodedValue(dex, element.Value, indent + "    "));
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
    private static string FormatEncodedValue(DexFile dex, EncodedValue value, string indent = "")
    {
        switch (value.Type)
        {
            case EncodedValueType.Byte:
                return unchecked((sbyte)value.AsInt()).ToString(System.Globalization.CultureInfo.InvariantCulture) + "t";
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
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace("E+", "E", StringComparison.Ordinal) + "f";
            case EncodedValueType.Double:
                var number = Convert.ToDouble(value.Value, System.Globalization.CultureInfo.InvariantCulture);
                var formatted = number.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace("E+", "E", StringComparison.Ordinal);
                return double.IsFinite(number) && !formatted.Contains('.') && !formatted.Contains('E')
                    ? formatted + ".0" : formatted;
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
                        : (value.Type == EncodedValueType.Enum ? ".enum " : string.Empty)
                          + $"{dex.GetType(field.ClassIndex)}->{dex.GetString(field.NameIndex)}:{dex.GetType(field.TypeIndex)}";
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
                    return items.Length == 0 ? "{}" : "{\n" + string.Join(",\n",
                        items.Select(v => indent + "    " + FormatEncodedValue(dex, v, indent + "    "))) + "\n" + indent + "}";
                }
            case EncodedValueType.Annotation:
                {
                    var item = value.Value as AnnotationItem;
                    if (item == null) return "?";
                    var sb = new StringBuilder();
                    sb.Append(".subannotation ").Append(dex.GetType(item.TypeIndex)).AppendLine();
                    foreach (var element in item.Elements)
                    {
                        sb.Append(indent).Append("    ").Append(dex.GetString(element.NameIndex)).Append(" = ")
                          .AppendLine(FormatEncodedValue(dex, element.Value, indent + "    "));
                    }
                    sb.Append(indent).Append(".end subannotation");
                    return sb.ToString();
                }
            case EncodedValueType.MethodHandle:
                var index = value.AsIndex();
                if (index < 0 || index >= dex.MethodHandles.Count)
                {
                    throw new InvalidDataException("A method-handle reference is outside its table.");
                }
                var handle = dex.MethodHandles[index];
                return SmaliMethodHandles.Name(handle.Kind) + "@" + FormatEncodedValue(dex,
                    new EncodedValue(handle.Kind <= 3 ? EncodedValueType.Field : EncodedValueType.Method, (int)handle.MemberIndex));
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

    private static void AppendOperands(StringBuilder sb, Instruction insn, DexFile dex)
    {
        if (insn.Operands.Count == 0) return;


        if (insn.Operands[0] is IReadOnlyList<int> regs)
        {
            sb.Append('{');
            if (insn.Mnemonic.EndsWith("/range", StringComparison.Ordinal) && regs.Count > 0)
            {
                sb.Append('v').Append(regs[0]).Append(" .. v").Append(regs[^1]);
            }
            else
            {
                for (var i = 0; i < regs.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('v').Append(regs[i]);
                }
            }
            sb.Append('}');
            for (var i = 1; i < insn.Operands.Count; i++)
            {
                sb.Append(", ");
                AppendOperand(sb, insn.Operands[i], dex);
            }
            return;
        }

        for (var i = 0; i < insn.Operands.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            AppendOperand(sb, insn.Operands[i], dex);
        }
    }

    private static void AppendOperand(StringBuilder sb, object? operand, DexFile dex)
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
                sb.Append(SmaliTypes.Single(field.OwnerClass, allowVoid: false)).Append("->").Append(field.Name)
                    .Append(':').Append(SmaliTypes.Single(field.Type, allowVoid: false));
                break;
            case MethodRefOperand method:
                sb.Append(SmaliTypes.Single(method.OwnerClass, allowVoid: false)).Append("->").Append(method.Name).Append('(');
                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    sb.Append(SmaliTypes.Single(method.Parameters[i], allowVoid: false));
                }
                sb.Append(')').Append(SmaliTypes.Single(method.ReturnType, allowVoid: true));
                break;
            case ProtoRefOperand prototype:
                sb.Append('(');
                foreach (var parameter in prototype.Parameters)
                {
                    sb.Append(parameter);
                }
                sb.Append(')').Append(prototype.ReturnType);
                break;
            case MethodHandleRefOperand handle:
                sb.Append(FormatEncodedValue(dex, new EncodedValue(EncodedValueType.MethodHandle, handle.Index)));
                break;
            case CallSiteRefOperand site:
                AppendCallSite(sb, dex, site.Index);
                break;
            case PayloadOperand:
                throw new InvalidDataException("A payload must be rendered as a complete directive block.");
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
        sb.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (value is < int.MinValue or > int.MaxValue)
        {
            sb.Append('L');
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