namespace UotanStudio.Firmware.AndroidMeta.Dex.Models;

/// <summary>
/// Bit values of the DEX <c>access_flags</c> field and their textual modifiers.
/// <para>DEX <c>access_flags</c> 字段的位定义及其对应的修饰符文本。</para>
/// </summary>
internal static class AccessFlags
{
    /// <summary>Bit <c>0x00001</c>, printed as <c>public</c>. <para>位 <c>0x00001</c>，输出为 <c>public</c>。</para></summary>
    public const uint Public = 0x0001;
    /// <summary>Bit <c>0x00002</c>, printed as <c>private</c>. <para>位 <c>0x00002</c>，输出为 <c>private</c>。</para></summary>
    public const uint Private = 0x0002;
    /// <summary>Bit <c>0x00004</c>, printed as <c>protected</c>. <para>位 <c>0x00004</c>，输出为 <c>protected</c>。</para></summary>
    public const uint Protected = 0x0004;
    /// <summary>Bit <c>0x00008</c>, printed as <c>static</c>. <para>位 <c>0x00008</c>，输出为 <c>static</c>。</para></summary>
    public const uint Static = 0x0008;
    /// <summary>Bit <c>0x00010</c>, printed as <c>final</c>. <para>位 <c>0x00010</c>，输出为 <c>final</c>。</para></summary>
    public const uint Final = 0x0010;
    /// <summary>Bit <c>0x00020</c>, printed as <c>synchronized</c>. <para>位 <c>0x00020</c>，输出为 <c>synchronized</c>。</para></summary>
    public const uint Synchronized = 0x0020;
    /// <summary>Bit <c>0x00040</c>, printed as <c>volatile</c>. <para>位 <c>0x00040</c>，输出为 <c>volatile</c>。</para></summary>
    public const uint Volatile = 0x0040;
    /// <summary>Method bit <c>0x00040</c>, printed as <c>bridge</c>. <para>方法位 <c>0x00040</c>，输出为 <c>bridge</c>。</para></summary>
    public const uint Bridge = 0x0040;
    /// <summary>Field bit <c>0x00080</c>, printed as <c>transient</c>. <para>字段位 <c>0x00080</c>，输出为 <c>transient</c>。</para></summary>
    public const uint Transient = 0x0080;
    /// <summary>Method bit <c>0x00080</c>, printed as <c>varargs</c>. <para>方法位 <c>0x00080</c>，输出为 <c>varargs</c>。</para></summary>
    public const uint Varargs = 0x0080;
    /// <summary>Bit <c>0x00100</c>, printed as <c>native</c>. <para>位 <c>0x00100</c>，输出为 <c>native</c>。</para></summary>
    public const uint Native = 0x0100;
    /// <summary>Bit <c>0x00200</c>, printed as <c>interface</c>. <para>位 <c>0x00200</c>，输出为 <c>interface</c>。</para></summary>
    public const uint Interface = 0x0200;
    /// <summary>Bit <c>0x00400</c>, printed as <c>abstract</c>. <para>位 <c>0x00400</c>，输出为 <c>abstract</c>。</para></summary>
    public const uint Abstract = 0x0400;
    /// <summary>Bit <c>0x00800</c>, printed as <c>strictfp</c>. <para>位 <c>0x00800</c>，输出为 <c>strictfp</c>。</para></summary>
    public const uint Strict = 0x0800;
    /// <summary>Bit <c>0x01000</c>, printed as <c>synthetic</c>. <para>位 <c>0x01000</c>，输出为 <c>synthetic</c>。</para></summary>
    public const uint Synthetic = 0x1000;
    /// <summary>Bit <c>0x02000</c>, printed as <c>annotation</c>. <para>位 <c>0x02000</c>，输出为 <c>annotation</c>。</para></summary>
    public const uint Annotation = 0x2000;
    /// <summary>Bit <c>0x04000</c>, printed as <c>enum</c>. <para>位 <c>0x04000</c>，输出为 <c>enum</c>。</para></summary>
    public const uint Enum = 0x4000;
    /// <summary>Bit <c>0x10000</c>, printed as <c>constructor</c>. <para>位 <c>0x10000</c>，输出为 <c>constructor</c>。</para></summary>
    public const uint Constructor = 0x10000;
    /// <summary>Bit <c>0x20000</c>, printed as <c>declared-synchronized</c>. <para>位 <c>0x20000</c>，输出为 <c>declared-synchronized</c>。</para></summary>
    public const uint DeclaredSynchronized = 0x20000;

    /// <summary>
    /// Formats the access flags into modifiers, interpreting shared bits as field modifiers.
    /// <para>把访问标志格式化为修饰符列表，共享位按字段修饰符解释。</para>
    /// </summary>
    /// <param name="flags">
    /// Raw <c>access_flags</c> value; bits outside the defined constants are ignored.
    /// <para>原始 <c>access_flags</c> 值；已定义常量之外的位会被忽略。</para>
    /// </param>
    /// <returns>
    /// Modifier names in constant order, separated by a single space, or <see cref="string.Empty"/>
    /// when no listed bit is set.
    /// <para>按常量顺序以单个空格分隔的修饰符名；没有任何已列出的位被置位时返回 <see cref="string.Empty"/>。</para>
    /// </returns>
    public static string Format(uint flags)
        => FormatCore(flags, method: false);

    /// <summary>Formats class access modifiers. <para>格式化类访问修饰符。</para></summary>
    /// <param name="flags">Raw access flags. <para>原始访问标志。</para></param>
    /// <returns>Space-separated modifiers valid for classes. <para>以空格分隔的类修饰符。</para></returns>
    public static string FormatClass(uint flags)
        => FormatCore(flags & (Public | Private | Protected | Static | Final | Interface | Abstract | Synthetic | Annotation | Enum), method: false);

    /// <summary>Formats field access modifiers. <para>格式化字段访问修饰符。</para></summary>
    /// <param name="flags">Raw access flags. <para>原始访问标志。</para></param>
    /// <returns>Space-separated modifiers with field-specific shared bits. <para>以空格分隔且共享位按字段解释的修饰符。</para></returns>
    public static string FormatField(uint flags)
        => FormatCore(flags & (Public | Private | Protected | Static | Final | Volatile | Transient | Synthetic | Enum), method: false);

    /// <summary>Formats method access modifiers. <para>格式化方法访问修饰符。</para></summary>
    /// <param name="flags">Raw access flags. <para>原始访问标志。</para></param>
    /// <returns>Space-separated modifiers with method-specific shared bits. <para>以空格分隔且共享位按方法解释的修饰符。</para></returns>
    public static string FormatMethod(uint flags)
        => FormatCore(flags & (Public | Private | Protected | Static | Final | Synchronized | Bridge | Varargs | Native
            | Abstract | Strict | Synthetic | Constructor | DeclaredSynchronized), method: true);

    private static string FormatCore(uint flags, bool method)
    {
        var parts = new List<string>(8);
        if ((flags & Public) != 0) parts.Add("public");
        if ((flags & Private) != 0) parts.Add("private");
        if ((flags & Protected) != 0) parts.Add("protected");
        if ((flags & Static) != 0) parts.Add("static");
        if ((flags & Final) != 0) parts.Add("final");
        if ((flags & Synchronized) != 0) parts.Add("synchronized");
        if ((flags & Volatile) != 0) parts.Add(method ? "bridge" : "volatile");
        if ((flags & Transient) != 0) parts.Add(method ? "varargs" : "transient");
        if ((flags & Native) != 0) parts.Add("native");
        if ((flags & Interface) != 0) parts.Add("interface");
        if ((flags & Abstract) != 0) parts.Add("abstract");
        if ((flags & Strict) != 0) parts.Add("strictfp");
        if ((flags & Synthetic) != 0) parts.Add("synthetic");
        if ((flags & Annotation) != 0) parts.Add("annotation");
        if ((flags & Enum) != 0) parts.Add("enum");
        if ((flags & Constructor) != 0) parts.Add("constructor");
        if ((flags & DeclaredSynchronized) != 0) parts.Add("declared-synchronized");
        return parts.Count == 0 ? string.Empty : string.Join(' ', parts);
    }
}