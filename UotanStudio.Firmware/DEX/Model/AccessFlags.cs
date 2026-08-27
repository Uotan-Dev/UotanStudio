namespace UotanStudio.Firmware.Dex.Model;

/// <summary>
/// <para>DEX access_flags 常量（类 / 字段 / 方法共用，按 Google dex-format 定义）。</para>
/// DEX access_flags constants (shared by classes, fields and methods, per Google dex-format).
/// </summary>
public static class AccessFlags
{
    /// <summary>public：对包外可见。</summary>
    public const uint Public = 0x0001;
    /// <summary>private：仅对本类可见。</summary>
    public const uint Private = 0x0002;
    /// <summary>protected：对子类可见。</summary>
    public const uint Protected = 0x0004;
    /// <summary>static：静态成员。</summary>
    public const uint Static = 0x0008;
    /// <summary>final：不可重写 / 不可继承。</summary>
    public const uint Final = 0x0010;
    /// <summary>synchronized：同步方法。</summary>
    public const uint Synchronized = 0x0020;
    /// <summary>volatile：易变字段。</summary>
    public const uint Volatile = 0x0040;
    /// <summary>bridge：桥接方法。</summary>
    public const uint Bridge = 0x0080;
    /// <summary>transient：瞬时字段。</summary>
    public const uint Transient = 0x0100;
    /// <summary>varargs：可变参数。</summary>
    public const uint Varargs = 0x0200;
    /// <summary>native：本地方法。</summary>
    public const uint Native = 0x0400;
    /// <summary>interface：接口。</summary>
    public const uint Interface = 0x0800;
    /// <summary>abstract：抽象成员。</summary>
    public const uint Abstract = 0x1000;
    /// <summary>strict：strictfp。</summary>
    public const uint Strict = 0x2000;
    /// <summary>synthetic：编译器合成。</summary>
    public const uint Synthetic = 0x4000;
    /// <summary>annotation：注解类型。</summary>
    public const uint Annotation = 0x8000;
    /// <summary>enum：枚举类型。</summary>
    public const uint Enum = 0x10000;
    /// <summary>constructor：构造方法。</summary>
    public const uint Constructor = 0x20000;
    /// <summary>declared-synchronized：声明的同步。</summary>
    public const uint DeclaredSynchronized = 0x40000;

    /// <summary>
    /// <para>把访问标志格式化为以空格分隔的修饰符列表。</para>
    /// Formats the access flags into a space-separated list of modifiers.
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