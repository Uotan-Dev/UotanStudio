using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>
/// Emits Java-like source text for decompiled DEX classes and methods.
/// <para>为反编译的 DEX 类与方法输出 Java 风格的源码文本。</para>
/// </summary>
internal static class JavaCodegen
{
    /// <summary>
    /// Renders a whole class (header + fields + methods).
    /// <para>渲染整个类（头 + 字段 + 方法）。</para>
    /// </summary>
    /// <param name="dex">DEX model supplying type, string and field lookups.
    /// <para>提供类型、字符串与字段查找的 DEX 模型。</para></param>
    /// <param name="cls">Class definition to render. <para>要渲染的类定义。</para></param>
    /// <param name="resolver">Optional overload resolver used when rendering method bodies;
    /// <see langword="null"/> renders calls with raw argument expressions.
    /// <para>渲染方法体时可选的重载解析器；为 <see langword="null"/> 时调用按原始实参表达式渲染。</para></param>
    /// <param name="options">Output settings; <see langword="null"/> uses
    /// <see cref="DecompileOptions.Default"/>. <para>输出设置；为 <see langword="null"/> 时使用
    /// <see cref="DecompileOptions.Default"/>。</para></param>
    /// <returns>The class source text: declaration header, then static and instance field
    /// declarations, then direct and virtual methods.
    /// <para>类源码文本：声明头部，随后是静态与实例字段声明，最后是直接方法与虚方法。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="cls"/> is
    /// <see langword="null"/>. <para><paramref name="dex"/> 或 <paramref name="cls"/> 为
    /// <see langword="null"/>。</para></exception>
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
    /// <param name="dex">DEX model supplying type and string lookups.
    /// <para>提供类型与字符串查找的 DEX 模型。</para></param>
    /// <param name="method">Encoded method to render. <para>要渲染的编码方法。</para></param>
    /// <param name="cls">Declaring class, or <see langword="null"/> when the enclosing type is
    /// unknown; a <see langword="null"/> value renders a constructor without its short class name.
    /// <para>声明类；未知所属类型时为 <see langword="null"/>，此时构造函数渲染时不带其短类名。</para></param>
    /// <param name="resolver">Optional overload resolver; <see langword="null"/> renders calls with
    /// raw argument expressions. <para>可选的重载解析器；为 <see langword="null"/> 时调用按原始实参表达式渲染。</para></param>
    /// <param name="options">Output settings; <see langword="null"/> uses
    /// <see cref="DecompileOptions.Default"/>. <para>输出设置；为 <see langword="null"/> 时使用
    /// <see cref="DecompileOptions.Default"/>。</para></param>
    /// <returns>The method text, indented one level. A method without a code item ends as a
    /// declaration terminated by <c>;</c>; otherwise the signature is followed by a reconstructed body.
    /// <para>缩进一级的方法文本。没有代码项的方法以 <c>;</c> 结束的声明收尾；否则签名后跟还原出的方法体。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="method"/> is
    /// <see langword="null"/>. <para><paramref name="dex"/> 或 <paramref name="method"/> 为
    /// <see langword="null"/>。</para></exception>
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