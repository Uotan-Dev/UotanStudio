namespace UotanStudio.Firmware.Dex.Decompile;

public static class DescriptorRenderer
{
    /// <summary>
    /// Determines whether a readable type name is a Java primitive type.
    /// <para>判断可读类型名是否为 Java 原语类型。</para>
    /// </summary>
    public static bool IsPrimitive(string type) => type is "void" or "boolean" or "byte" or "short"
        or "char" or "int" or "long" or "float" or "double";

    /// <summary>
    /// Best-effort conversion of a readable type name into a DEX descriptor (e.g. "com.x.Foo" → "Lcom/x/Foo;", "int[]" → "[I").
    /// <para>把可读类型名尽力转换为 DEX 描述符（如 "com.x.Foo" → "Lcom/x/Foo;"、"int[]" → "[I"）。</para>
    /// </summary>
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
    /// Determines whether from widens implicitly to to per the Java primitive widening rules.
    /// <para>判断 from 是否可按 Java 原语加宽规则隐式转换为 to。</para>
    /// </summary>
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
