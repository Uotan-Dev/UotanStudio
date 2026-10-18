namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>
/// Maps between readable Java type names and DEX type descriptors for decompiled output.
/// <para>在反编译输出中，于可读的 Java 类型名与 DEX 类型描述符之间做映射。</para>
/// </summary>
internal static class DescriptorRenderer
{
    /// <summary>
    /// Determines whether a readable type name is a Java primitive type.
    /// <para>判断可读类型名是否为 Java 原语类型。</para>
    /// </summary>
    /// <param name="type">Readable type name. <para>可读类型名。</para></param>
    /// <returns><see langword="true"/> for <c>void</c>, <c>boolean</c>, <c>byte</c>, <c>short</c>,
    /// <c>char</c>, <c>int</c>, <c>long</c>, <c>float</c> or <c>double</c>.
    /// <para>取值为 <c>void</c>、<c>boolean</c>、<c>byte</c>、<c>short</c>、<c>char</c>、<c>int</c>、
    /// <c>long</c>、<c>float</c> 或 <c>double</c> 时为 <see langword="true"/>。</para></returns>
    public static bool IsPrimitive(string type) => type is "void" or "boolean" or "byte" or "short"
        or "char" or "int" or "long" or "float" or "double";

    /// <summary>
    /// Best-effort conversion of a readable type name into a DEX descriptor (e.g. "com.x.Foo" → "Lcom/x/Foo;", "int[]" → "[I").
    /// <para>把可读类型名尽力转换为 DEX 描述符（如 "com.x.Foo" → "Lcom/x/Foo;"、"int[]" → "[I"）。</para>
    /// </summary>
    /// <param name="formatted">Readable type name. <para>可读类型名。</para></param>
    /// <returns>The descriptor for the name. Input that is empty, already a descriptor (leading
    /// <c>[</c> or <c>L</c>) or a single-letter primitive code is returned unchanged; other names
    /// become the corresponding primitive code or an <c>L…;</c> class descriptor.
    /// <para>返回该名称对应的描述符。空输入、已是描述符（以 <c>[</c> 或 <c>L</c> 开头）或单字母原语码
    /// 会原样返回；其余名称转换为对应原语码或 <c>L…;</c> 类描述符。</para></returns>
    public static string ToDescriptor(string formatted)
    {
        if (string.IsNullOrEmpty(formatted)) return formatted;
        if (formatted.Length > 0 && (formatted[0] == '[' || formatted[0] == 'L')) return formatted;
        if (formatted.Length == 1 && "VZBSCIJFD".IndexOf(formatted[0], StringComparison.Ordinal) >= 0) return formatted;

        if (formatted.EndsWith("[]", StringComparison.Ordinal))
        {
            return "[" + ToDescriptor(formatted[..^2]);
        }

        return formatted switch
        {
            "void" => "V",
            "boolean" => "Z",
            "byte" => "B",
            "short" => "S",
            "char" => "C",
            "int" => "I",
            "long" => "J",
            "float" => "F",
            "double" => "D",
            _ => "L" + formatted.Replace('.', '/') + ";"
        };
    }

    /// <summary>
    /// Gets the boxed class name of a primitive type (e.g. "int" → "java.lang.Integer").
    /// <para>获取原语类型的装箱类名（如 "int" → "java.lang.Integer"）。</para>
    /// </summary>
    /// <param name="primitive">Primitive type name. <para>原语类型名。</para></param>
    /// <returns>The fully qualified boxed type; <c>void</c> maps to <c>java.lang.Void</c> and any
    /// other input is returned unchanged. <para>限定装箱类型名；<c>void</c> 映射为
    /// <c>java.lang.Void</c>，其余输入原样返回。</para></returns>
    public static string Boxed(string primitive) => primitive switch
    {
        "boolean" => "java.lang.Boolean",
        "byte" => "java.lang.Byte",
        "short" => "java.lang.Short",
        "char" => "java.lang.Character",
        "int" => "java.lang.Integer",
        "long" => "java.lang.Long",
        "float" => "java.lang.Float",
        "double" => "java.lang.Double",
        "void" => "java.lang.Void",
        _ => primitive
    };

    /// <summary>Determines whether from widens implicitly to to per the Java primitive widening rules.
    /// <para>判断 from 是否可按 Java 原语加宽规则隐式转换为 to。</para></summary>
    /// <param name="from">Source primitive type name.<para>源原语类型名。</para></param>
    /// <param name="to">Target primitive type name.<para>目标原语类型名。</para></param>
    /// <returns><see langword="true"/> for the widening pairs <c>byte</c> to <c>short</c>, <c>int</c>, <c>long</c>, <c>float</c> or <c>double</c>; <c>short</c> or <c>char</c> to <c>int</c>, <c>long</c>, <c>float</c> or <c>double</c>; <c>int</c> to <c>long</c>, <c>float</c> or <c>double</c>; <c>long</c> to <c>float</c> or <c>double</c>; and <c>float</c> to <c>double</c>. Any other pair, including identical types, is <see langword="false"/>.
    /// <para>以下加宽对返回 <see langword="true"/>：<c>byte</c> 到 <c>short</c>、<c>int</c>、 <c>long</c>、<c>float</c> 或 <c>double</c>；<c>short</c> 或 <c>char</c> 到 <c>int</c>、 <c>long</c>、<c>float</c> 或 <c>double</c>；<c>int</c> 到 <c>long</c>、<c>float</c> 或 <c>double</c>；<c>long</c> 到 <c>float</c> 或 <c>double</c>；以及 <c>float</c> 到 <c>double</c>。其余组合（包括同一类型）均为 <see langword="false"/>。</para></returns>
    public static bool IsWideningPrimitive(string from, string to) => from switch
    {
        "byte" => to is "short" or "int" or "long" or "float" or "double",
        "short" or "char" => to is "int" or "long" or "float" or "double",
        "int" => to is "long" or "float" or "double",
        "long" => to is "float" or "double",
        "float" => to == "double",
        _ => false
    };
}