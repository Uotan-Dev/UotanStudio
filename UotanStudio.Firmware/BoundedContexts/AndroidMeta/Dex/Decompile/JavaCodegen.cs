using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public static class JavaCodegen
{
    /// <summary>
    /// Renders a whole class (header + fields + methods).
    /// <para>渲染整个类（头 + 字段 + 方法）。</para>
    /// </summary>
    public static string RenderClass(DexFile dex, ClassDefinition cls, MethodResolver? resolver, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(cls);
        options ??= DecompileOptions.Default;

        var sb = new StringBuilder(1024);
        var isInterface = (cls.AccessFlags & AccessFlags.Interface) != 0;


        var mods = new List<string>(4);
        if ((cls.AccessFlags & AccessFlags.Public) != 0) mods.Add("public");
        if ((cls.AccessFlags & AccessFlags.Final) != 0) mods.Add("final");
        if ((cls.AccessFlags & AccessFlags.Abstract) != 0 && !isInterface) mods.Add("abstract");

        var keyword = isInterface ? "interface" : "class";
        var className = ShortName(dex.FormatType(cls.TypeIndex));
        sb.Append(string.Join(' ', mods));
        if (mods.Count > 0) sb.Append(' ');
        sb.Append(keyword).Append(' ').Append(className);

        if (cls.SuperclassIndex >= 0 && cls.SuperclassIndex < dex.Types.Count)
        {
            var super = dex.FormatType(cls.SuperclassIndex);
            if (!isInterface && super != "java.lang.Object")
            {
                sb.Append(" extends ").Append(super);
            }
        }

        if (cls.InterfaceTypeIndices.Length > 0)
        {
            sb.Append(isInterface ? " extends " : " implements ");
            for (var i = 0; i < cls.InterfaceTypeIndices.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(dex.FormatType(cls.InterfaceTypeIndices[i]));
            }
        }

        sb.AppendLine(" {");


        foreach (var field in cls.StaticFields)
        {
            AppendField(sb, dex, field);
        }

        foreach (var field in cls.InstanceFields)
        {
            AppendField(sb, dex, field);
        }


        foreach (var method in cls.DirectMethods)
        {
            sb.AppendLine();
            sb.Append(RenderMethod(dex, method, cls, resolver, options));
        }

        foreach (var method in cls.VirtualMethods)
        {
            sb.AppendLine();
            sb.Append(RenderMethod(dex, method, cls, resolver, options));
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Renders a single method (signature + structured body).
    /// <para>渲染单个方法（签名 + 结构化方法体）。</para>
    /// </summary>
    public static string RenderMethod(DexFile dex, EncodedMethod method, ClassDefinition? cls, MethodResolver? resolver, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(method);
        options ??= DecompileOptions.Default;

        var ctx = MethodContext.Create(dex, method, cls);
        var sb = new StringBuilder(512);
        var indent = 1;
        WriteIndent(sb, indent);

        if (ctx.MethodName == "<clinit>")
        {


            sb.Append("static");
        }
        else
        {
            var mods = MethodModifiers(method.AccessFlags);
            if (mods.Length > 0) sb.Append(mods).Append(' ');

            if (ctx.IsConstructor)
            {


                var shortName = ctx.ClassName is null ? "<init>" : ShortName(ctx.ClassName);
                sb.Append(shortName);
            }
            else
            {
                sb.Append(ctx.ReturnType).Append(' ').Append(ctx.MethodName);
            }

            sb.Append('(');
            for (var i = 0; i < ctx.ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ctx.ParameterTypes[i]).Append(' ').Append(ctx.IsStatic ? $"p{i}" : $"p{i + 1}");
            }

            sb.Append(')');
        }

        var code = method.Code;
        if (code is null)
        {


            sb.Append(';');
            return sb.ToString();
        }

        sb.AppendLine(" {");
        var instructions = code.Instructions.Count > 0
            ? code.Instructions
            : InstructionDecoder.Decode(code.RawInsns, dex);
        var analysis = RegisterAnalyzer.Analyze(ctx, instructions, resolver, options);
        var cfg = CfgBuilder.Build(code, dex);
        var stmts = ControlFlowStructuring.Structure(analysis, cfg, options);
        foreach (var stmt in stmts)
        {
            AppendStatement(sb, stmt, indent + 1, options);
        }

        WriteIndent(sb, indent);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, DexFile dex, EncodedField field)
    {
        var fieldId = dex.GetField(field.FieldIndex);
        if (fieldId is null) return;

        WriteIndent(sb, 1);
        var mods = FieldModifiers(field.AccessFlags);
        if (mods.Length > 0) sb.Append(mods).Append(' ');
        sb.Append(dex.FormatType(fieldId.TypeIndex)).Append(' ');
        sb.Append(dex.GetString(fieldId.NameIndex)).Append(';');
        sb.AppendLine();
    }

    private static string MethodModifiers(uint flags)
    {
        var parts = new List<string>(6);
        if ((flags & AccessFlags.Public) != 0) parts.Add("public");
        else if ((flags & AccessFlags.Protected) != 0) parts.Add("protected");
        else if ((flags & AccessFlags.Private) != 0) parts.Add("private");
        if ((flags & AccessFlags.Static) != 0) parts.Add("static");
        if ((flags & AccessFlags.Final) != 0) parts.Add("final");
        if ((flags & AccessFlags.Synchronized) != 0 || (flags & AccessFlags.DeclaredSynchronized) != 0) parts.Add("synchronized");
        if ((flags & AccessFlags.Native) != 0) parts.Add("native");
        if ((flags & AccessFlags.Abstract) != 0) parts.Add("abstract");
        return string.Join(' ', parts);
    }

    private static string FieldModifiers(uint flags)
    {
        var parts = new List<string>(5);
        if ((flags & AccessFlags.Public) != 0) parts.Add("public");
        else if ((flags & AccessFlags.Protected) != 0) parts.Add("protected");
        else if ((flags & AccessFlags.Private) != 0) parts.Add("private");
        if ((flags & AccessFlags.Static) != 0) parts.Add("static");
        if ((flags & AccessFlags.Final) != 0) parts.Add("final");
        if ((flags & AccessFlags.Volatile) != 0) parts.Add("volatile");
        if ((flags & AccessFlags.Transient) != 0) parts.Add("transient");
        return string.Join(' ', parts);
    }

    private static void AppendStatement(StringBuilder sb, JavaStmt stmt, int indent, DecompileOptions options)
    {
        if (options.Mode == DecompileOutputMode.BytecodeGuide && stmt.Offset >= 0)
        {
            WriteIndent(sb, indent);
            sb.Append("// 0x").Append(stmt.Offset.ToString("X4")).AppendLine();
        }

        stmt.WriteTo(sb, indent, options);
        sb.AppendLine();
    }

    private static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++) sb.Append("    ");
    }

    private static string ShortName(string formattedType)
    {
        var dot = formattedType.LastIndexOf('.');
        return dot >= 0 ? formattedType[(dot + 1)..] : formattedType;
    }
}