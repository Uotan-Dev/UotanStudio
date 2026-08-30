using System;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>描述符渲染工具：可读类型名与 DEX 描述符之间的双向转换，以及原语类型分类/加宽判定，
/// 供重载解析与代码生成使用。</para>
/// Descriptor rendering utilities: conversions between readable type names and DEX descriptors,
/// plus primitive classification/widening rules, used by overload resolution and code generation.
/// </summary>
public static class DescriptorRenderer
{
    /// <summary>
    /// <para>判断可读类型名是否为 Java 原语类型。</para>
    /// Determines whether a readable type name is a Java primitive type.
    /// </summary>
    /// <param name="type">可读类型名（如 "int"、"java.lang.String"）。<para>Readable type name (e.g. "int", "java.lang.String").</para></param>
    /// <returns>是原语返回 <c>true</c>。<para><c>true</c> when primitive.</para></returns>
    public static bool IsPrimitive(string type) => type is "void" or "boolean" or "byte" or "short"
        or "char" or "int" or "long" or "float" or "double";

    /// <summary>
    /// <para>把可读类型名尽力转换为 DEX 描述符（如 "com.x.Foo" → "Lcom/x/Foo;"、"int[]" → "[I"）。
    /// 已是描述符形态的输入原样返回。</para>
    /// Best-effort conversion of a readable type name into a DEX descriptor (e.g. "com.x.Foo" →
    /// "Lcom/x/Foo;", "int[]" → "[I"). Descriptor-shaped input is returned unchanged.
    /// </summary>
    /// <param name="formatted">可读类型名。<para>Readable type name.</para></param>
    /// <returns>描述符。<para>The descriptor.</para></returns>
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
    /// <para>获取原语类型的装箱类名（如 "int" → "java.lang.Integer"）。</para>
    /// Gets the boxed class name of a primitive type (e.g. "int" → "java.lang.Integer").
    /// </summary>
    /// <param name="primitive">原语类型名。<para>Primitive type name.</para></param>
    /// <returns>装箱类名；非原语输入原样返回。<para>Boxed class name; non-primitive input is returned unchanged.</para></returns>
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

    /// <summary>
    /// <para>判断 <paramref name="from"/> 是否可按 Java 原语加宽规则隐式转换为 <paramref name="to"/>。</para>
    /// Determines whether <paramref name="from"/> widens implicitly to <paramref name="to"/>
    /// per the Java primitive widening rules.
    /// </summary>
    /// <param name="from">源原语类型。<para>Source primitive type.</para></param>
    /// <param name="to">目标原语类型。<para>Target primitive type.</para></param>
    /// <returns>可加宽返回 <c>true</c>。<para><c>true</c> when widening is allowed.</para></returns>
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
