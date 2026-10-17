using System.Globalization;
using System.Xml.Linq;
using UotanStudio.Firmware.AndroidMeta.Resources;

namespace UotanStudio.Firmware.AndroidMeta.AXml;

/// <summary>
/// Data-type tag stored in an attribute value, mirroring Android's <c>Res_value</c> type constants. The byte value is the raw <c>dataType</c> read from the AXML stream.
/// <para>属性值中保存的数据类型标签，对应 Android 的 <c>Res_value</c> 类型常量。字节值即从 AXML 流中读到的原始 <c>dataType</c>。</para>
/// </summary>
public enum AXmlValueType : byte
{
    /// <summary>No data; the value is null. <para>无数据，值为空。</para></summary>
    Null = 0x00,
    /// <summary>A resource entry reference resolved against the resource table. <para>针对资源表解析的资源项引用。</para></summary>
    Reference = 0x01,
    /// <summary>An attribute resource reference resolved against the current theme at run time. <para>运行时针对当前主题解析的属性资源引用。</para></summary>
    Attribute = 0x02,
    /// <summary>An index into the string pool. <para>指向字符串池的索引。</para></summary>
    String = 0x03,
    /// <summary>An IEEE-754 single-precision float whose bits are held in <c>ValueData</c>. <para>以 <c>ValueData</c> 各位保存的 IEEE-754 单精度浮点数。</para></summary>
    Float = 0x04,
    /// <summary>A complex dimension value with the unit encoded in the low bits. <para>低位编码单位的复数维度值。</para></summary>
    Dimension = 0x05,
    /// <summary>A complex fraction value with the unit encoded in the low bits. <para>低位编码单位的复数百分比值。</para></summary>
    Fraction = 0x06,
    /// <summary>A dynamic (runtime-added) resource reference. <para>动态（运行时添加）的资源引用。</para></summary>
    DynamicReference = 0x07,
    /// <summary>A dynamic (runtime-added) attribute reference. <para>动态（运行时添加）的属性引用。</para></summary>
    DynamicAttribute = 0x08,
    /// <summary>A signed integer rendered in decimal. <para>以十进制显示的有符号整数。</para></summary>
    IntDec = 0x10,
    /// <summary>An integer rendered in hexadecimal. <para>以十六进制显示的整数。</para></summary>
    IntHex = 0x11,
    /// <summary>A boolean where <c>0</c> is false and any other value is true. <para>布尔值，<c>0</c> 为假，其他值均为真。</para></summary>
    IntBoolean = 0x12,
    /// <summary>An 8-bit-per-channel ARGB color. <para>每通道 8 位的 ARGB 颜色。</para></summary>
    IntColorArgb8 = 0x1c,
    /// <summary>An 8-bit-per-channel RGB color. <para>每通道 8 位的 RGB 颜色。</para></summary>
    IntColorRgb8 = 0x1d,
    /// <summary>A 4-bit-per-channel ARGB color. <para>每通道 4 位的 ARGB 颜色。</para></summary>
    IntColorArgb4 = 0x1e,
    /// <summary>A 4-bit-per-channel RGB color. <para>每通道 4 位的 RGB 颜色。</para></summary>
    IntColorRgb4 = 0x1f
}

/// <summary>
/// A single decoded attribute on an element: its name plus a typed value held either as raw text or as the <see cref="ValueType"/>/<see cref="ValueData"/> pair.
/// <para>元素上的单个已解码属性：其名称，以及以原始文本或 <see cref="ValueType"/>/<see cref="ValueData"/> 组合保存的类型化值。</para>
/// </summary>
public sealed class AXmlAttribute
{
    internal int? OriginalNameIndex { get; init; }

    /// <summary>Namespace URI of the attribute name.
    /// <para>属性名的命名空间 URI。</para></summary>
    /// <value>The namespace URI, or <see langword="null"/> when the attribute carries no namespace.
    /// <para>命名空间 URI；属性不带命名空间时为 <see langword="null"/>。</para></value>
    public string? Namespace { get; init; }

    /// <summary>Name of the attribute.
    /// <para>属性的名称。</para></summary>
    /// <value>The name resolved from the string pool; empty when the name index was absent.
    /// <para>从字符串池解析的名称；名称索引缺失时为空字符串。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>Original source text of the attribute value, when the file retained it.
    /// <para>当文件保留了属性值的原始源文本时的内容。</para></summary>
    /// <value>The raw text, which takes precedence over <see cref="FormatValue"/>; <see langword="null"/> when absent.
    /// <para>原始文本，优先于 <see cref="FormatValue"/>；缺失时为 <see langword="null"/>。</para></value>
    public string? RawValue { get; set; }

    /// <summary>Data-type tag of the value.
    /// <para>值的数据类型标签。</para></summary>
    /// <value>The type that governs how <see cref="ValueData"/> is interpreted.
    /// <para>决定如何解释 <see cref="ValueData"/> 的类型。</para></value>
    public AXmlValueType ValueType { get; set; }

    /// <summary>Raw value payload backing the attribute.
    /// <para>属性底层的原始值载荷。</para></summary>
    /// <value>A 32-bit field holding a string-pool index, resource id, integer, float bits or packed color, interpreted per <see cref="ValueType"/>.
    /// <para>保存字符串池索引、资源 id、整数、浮点位或打包颜色的 32 位字段，按 <see cref="ValueType"/> 解释。</para></value>
    public uint ValueData { get; set; }

    /// <summary>
    /// Formats the typed value as readable text per its data type (raw value wins when present).
    /// <para>按数据类型格式化类型化值为可读文本（优先原始值）。</para>
    /// </summary>
    /// <param name="stringPool">
    /// Pool for resolving string-typed values; when absent they render as <c>@str:index</c>.
    /// <para>用于解析字符串类型值的池；缺失时渲染为 <c>@str:索引</c>。</para>
    /// </param>
    /// <param name="resourceMap">
    /// Optional resource-id map retained for signature compatibility; formatting ignores it.
    /// <para>出于签名兼容保留的可选资源 id 映射；格式化会忽略它。</para>
    /// </param>
    /// <returns>The readable text form of the value. <para>值的可读文本形式。</para></returns>
    public string FormatValue(AXmlStringPool? stringPool, IReadOnlyList<uint>? resourceMap = null)
    {


        if (RawValue is not null)
        {
            return RawValue;
        }

        switch (ValueType)
        {
            case AXmlValueType.String:
                return stringPool?.GetString(checked((int)ValueData)) ?? $"@str:{ValueData}";
            case AXmlValueType.Reference:
            case AXmlValueType.DynamicReference:
                return FormatReference(ValueData, resourceMap);
            case AXmlValueType.Attribute:
            case AXmlValueType.DynamicAttribute:
                return FormatAttribute(ValueData, resourceMap);
            case AXmlValueType.IntDec:
                return unchecked((int)ValueData).ToString(CultureInfo.InvariantCulture);
            case AXmlValueType.IntHex:
                return "0x" + ValueData.ToString("x8", CultureInfo.InvariantCulture);
            case AXmlValueType.IntBoolean:
                return ValueData != 0 ? "true" : "false";
            case AXmlValueType.Float:
                return FormatFloat();
            case AXmlValueType.Dimension:
                return FormatDimension();
            case AXmlValueType.Fraction:
                return FormatFraction();
            case AXmlValueType.IntColorArgb8:
            case AXmlValueType.IntColorRgb8:
                return "#" + ValueData.ToString("x8", CultureInfo.InvariantCulture);
            case AXmlValueType.IntColorArgb4:
            case AXmlValueType.IntColorRgb4:
                return "#" + (ValueData & 0xffff).ToString("x4", CultureInfo.InvariantCulture);
            case AXmlValueType.Null:
                return "null";
            default:
                return "0x" + ValueData.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Converts the raw integer value to read-only hexadecimal text.
    /// <para>把原始整数值转为只读十六进制文本。</para>
    /// </summary>
    /// <returns><see cref="ValueData"/> rendered as an 8-digit lowercase hexadecimal string prefixed with <c>0x</c>.
    /// <para>将 <see cref="ValueData"/> 渲染为带 <c>0x</c> 前缀的 8 位小写十六进制字符串。</para></returns>
    public string FormatRawData() => "0x" + ValueData.ToString("x8", CultureInfo.InvariantCulture);

    private string FormatReference(uint data, IReadOnlyList<uint>? resourceMap)
    {
        if (data == 0) return "@null";
        return $"@0x{data:x8}";
    }

    private string FormatAttribute(uint data, IReadOnlyList<uint>? resourceMap)
    {
        return $"?0x{data:x8}";
    }

    private string FormatFloat()
    {
        var bits = ValueData;
        var f = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        return f.ToString("R", CultureInfo.InvariantCulture);
    }

    private string FormatDimension()
    {
        var value = AndroidComplexValue.ToFloat(ValueData);
        var unit = DimensionUnit(ValueData & 0x0f);
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    private string FormatFraction()
    {
        var value = AndroidComplexValue.ToFloat(ValueData) * 100f;
        var unit = (ValueData & 0x0f) switch
        {
            0 => "%",
            1 => "%p",
            _ => string.Empty
        };
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    private static string DimensionUnit(uint low4) => low4 switch
    {
        0 => "px",
        1 => "dip",
        2 => "sp",
        3 => "pt",
        4 => "in",
        5 => "mm",
        _ => "px"
    };

    private static float DimensionFactor(uint low4) => low4 switch
    {
        0 => 1f,
        1 => 1f,
        2 => 1f,
        3 => 1f,
        4 => 1f,
        5 => 1f,
        6 => 65536f,
        _ => 1f
    };
}

internal static class AndroidComplexValue
{
    internal static float ToFloat(uint data)
    {
        var mantissa = unchecked((int)(data & 0xffffff00));
        return mantissa * (((data >> 4) & 3) switch
        {
            0 => 1f / 256,
            1 => 1f / 32768,
            2 => 1f / 8388608,
            _ => 1f / 2147483648
        });
    }
}

/// <summary>
/// A decoded element node in the AXML tree, carrying its name, attributes, nested children, optional text, and the source position captured during parsing.
/// <para>AXML 树中已解码的元素节点，保存名称、属性、嵌套子节点、可选文本，以及解析时捕获的源位置。</para>
/// </summary>
public sealed class AXmlElement
{
    /// <summary>Namespace of the element name.
    /// <para>元素名的命名空间。</para></summary>
    /// <value>The namespace URI, or <see langword="null"/> when the element carries no namespace.
    /// <para>命名空间 URI；元素不带命名空间时为 <see langword="null"/>。</para></value>
    public string? Namespace { get; set; }

    /// <summary>Element tag name.
    /// <para>元素标签名。</para></summary>
    /// <value>The name resolved from the string pool. <para>从字符串池解析的名称。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Attributes attached to this element.
    /// <para>附加于此元素的属性。</para></summary>
    /// <value>A mutable list in source order; empty when the element declares none.
    /// <para>按源顺序排列的可变列表；元素未声明属性时为空。</para></value>
    public List<AXmlAttribute> Attributes { get; } = [];

    /// <summary>Direct child elements.
    /// <para>直接子元素。</para></summary>
    /// <value>A mutable list in document order; empty for a leaf element.
    /// <para>按文档顺序排列的可变列表；叶子元素为空。</para></value>
    public List<AXmlElement> Children { get; } = [];

    /// <summary>Text content held by the element.
    /// <para>元素持有的文本内容。</para></summary>
    /// <value>The text from a CDATA/text node, or <see langword="null"/> when the element has none.
    /// <para>来自 CDATA/文本节点的文本；元素没有时为 <see langword="null"/>。</para></value>
    public string? Text { get; set; }

    /// <summary>Source line number recorded during parsing.
    /// <para>解析时记录的源行号。</para></summary>
    /// <value>The line from the start-element chunk, or 0 when the file did not supply one.
    /// <para>起始元素块中的行号；文件未提供时为 0。</para></value>
    public int LineNumber { get; set; }

    /// <summary>Nesting depth from the document root.
    /// <para>距文档根的嵌套深度。</para></summary>
    /// <value>Assigned by the parser as the element stack grows; 0 for the root.
    /// <para>由解析器在元素栈增长时赋值；根元素为 0。</para></value>
    public int Depth { get; set; }

    /// <summary>
    /// Finds the first direct child element by name.
    /// <para>按名称查找首个直接子元素。</para>
    /// </summary>
    /// <param name="name">Tag name matched with ordinal (case-sensitive) comparison. <para>以区分大小写的序号比较匹配的标签名。</para></param>
    /// <returns>The matching child, or <see langword="null"/> when no direct child has that name.
    /// <para>匹配的子元素；没有同名直接子元素时返回 <see langword="null"/>。</para></returns>
    public AXmlElement? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The decoded result of parsing an AXML file: the element tree plus the string pool, resource map and namespace declarations needed to render attribute values.
/// <para>解析 AXML 文件的解码结果：元素树，以及渲染属性值所需的字符串池、资源映射与命名空间声明。</para>
/// </summary>
public sealed class AXmlDocument
{
    /// <summary>Root element of the document.
    /// <para>文档根元素。</para></summary>
    /// <value>The top-level element, or a fresh empty element when parsing produced none.
    /// <para>顶层元素；解析未产生元素时为新建的空元素。</para></value>
    public AXmlElement Root { get; set; } = new();

    /// <summary>String pool backing the document.
    /// <para>文档底层的字符串池。</para></summary>
    /// <value>The shared pool used for element, attribute and value lookups.
    /// <para>供元素、属性与值查找共享的池。</para></value>
    public AXmlStringPool StringPool { get; } = new();

    /// <summary>Resource identifiers from the XML resource-map chunk.
    /// <para>来自 XML 资源映射块的资源标识。</para></summary>
    /// <value>A mutable list indexed by attribute position; empty when the file carries no resource map.
    /// <para>按属性位置索引的可变列表；文件不含资源映射时为空。</para></value>
    public List<uint> ResourceMap { get; } = [];

    /// <summary>Namespace declarations gathered while parsing.
    /// <para>解析期间收集的命名空间声明。</para></summary>
    /// <value>A mutable list built from start-namespace chunks; may contain duplicate prefixes.
    /// <para>由起始命名空间块构建的可变列表；可能包含重复前缀。</para></value>
    public List<AXmlNamespace> Namespaces { get; } = [];

    /// <summary>Size in bytes of the ResXMLTree header.
    /// <para>ResXMLTree 头的字节大小。</para></summary>
    /// <value>Read from the file's <c>headerSize</c> field; defaults to 8 for a document built in code.
    /// <para>从文件的 <c>headerSize</c> 字段读取；以代码方式构建时默认为 8。</para></value>
    public int HeaderSize { get; set; } = 8;

    /// <summary>
    /// Produces the XML text representation (indentation + attributes + text) for display and export.
    /// <para>生成 XML 文本表示（缩进 + 属性 + 文本），供展示与导出。</para>
    /// </summary>
    /// <returns>An XML declaration followed by the serialized tree, with namespace prefixes bound from <see cref="Namespaces"/> and <c>xml</c>/<c>xmlns</c> prefixes skipped.
    /// <para>先输出 XML 声明，再输出已序列化的树；命名空间前缀由 <see cref="Namespaces"/> 绑定，并跳过 <c>xml</c>/<c>xmlns</c> 前缀。</para></returns>
    public string ToXml()
    {
        var root = BuildElement(Root);
        foreach (var ns in Namespaces.GroupBy(n => n.Prefix).Select(g => g.First()))
        {
            if (ns.Prefix == "xml" || ns.Prefix == "xmlns") continue;
            root.SetAttributeValue(ns.Prefix.Length == 0 ? "xmlns" : XNamespace.Xmlns + ns.Prefix, ns.Uri);
        }
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root;

        XElement BuildElement(AXmlElement element)
        {
            var result = new XElement(XName.Get(element.Name, element.Namespace ?? string.Empty));
            foreach (var attr in element.Attributes)
                result.Add(new XAttribute(XName.Get(attr.Name, attr.Namespace ?? string.Empty),
                    attr.FormatValue(StringPool, ResourceMap)));
            if (element.Text is not null) result.Add(new XText(element.Text));
            foreach (var child in element.Children) result.Add(BuildElement(child));
            return result;
        }
    }
}

/// <summary>
/// A namespace binding (prefix to URI) recorded while parsing AXML start-namespace chunks.
/// <para>解析 AXML 起始命名空间块时记录的命名空间绑定（前缀到 URI）。</para>
/// </summary>
public sealed class AXmlNamespace
{
    /// <summary>Namespace prefix as declared in the document.
    /// <para>文档中声明的命名空间前缀。</para></summary>
    /// <value>The bound prefix; empty for a default (unprefixed) binding.
    /// <para>所绑定的前缀；默认（无前缀）绑定为空字符串。</para></value>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Namespace URI the prefix resolves to.
    /// <para>前缀所解析到的命名空间 URI。</para></summary>
    /// <value>The URI string read from the string pool. <para>从字符串池读取的 URI 字符串。</para></value>
    public string Uri { get; init; } = string.Empty;
}