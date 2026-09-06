namespace UotanStudio.Firmware.Dex.Model;

public static class AccessFlags
{
    public const uint Public = 0x0001;
    public const uint Private = 0x0002;
    public const uint Protected = 0x0004;
    public const uint Static = 0x0008;
    public const uint Final = 0x0010;
    public const uint Synchronized = 0x0020;
    public const uint Volatile = 0x0040;
    public const uint Bridge = 0x0080;
    public const uint Transient = 0x0100;
    public const uint Varargs = 0x0200;
    public const uint Native = 0x0400;
    public const uint Interface = 0x0800;
    public const uint Abstract = 0x1000;
    public const uint Strict = 0x2000;
    public const uint Synthetic = 0x4000;
    public const uint Annotation = 0x8000;
    public const uint Enum = 0x10000;
    public const uint Constructor = 0x20000;
    public const uint DeclaredSynchronized = 0x40000;

    /// <summary>
    /// Formats the access flags into a space-separated list of modifiers.
    /// <para>把访问标志格式化为以空格分隔的修饰符列表。</para>
    /// </summary>
    public static string Format(uint flags)
    {
        var parts = new List<string>(8);
        if ((flags & Public) != 0) parts.Add("public");
        if ((flags & Private) != 0) parts.Add("private");
        if ((flags & Protected) != 0) parts.Add("protected");
        if ((flags & Static) != 0) parts.Add("static");
        if ((flags & Final) != 0) parts.Add("final");
        if ((flags & Synchronized) != 0) parts.Add("synchronized");
        if ((flags & Volatile) != 0) parts.Add("volatile");
        if ((flags & Bridge) != 0) parts.Add("bridge");
        if ((flags & Transient) != 0) parts.Add("transient");
        if ((flags & Varargs) != 0) parts.Add("varargs");
        if ((flags & Native) != 0) parts.Add("native");
        if ((flags & Interface) != 0) parts.Add("interface");
        if ((flags & Abstract) != 0) parts.Add("abstract");
        if ((flags & Strict) != 0) parts.Add("strictfp");
        if ((flags & Synthetic) != 0) parts.Add("synthetic");
        if ((flags & Annotation) != 0) parts.Add("annotation");
        if ((flags & Enum) != 0) parts.Add("enum");
        if ((flags & Constructor) != 0) parts.Add("constructor");
        return parts.Count == 0 ? string.Empty : string.Join(' ', parts);
    }
}
