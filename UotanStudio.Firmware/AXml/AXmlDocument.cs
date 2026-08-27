using System.Globalization;

namespace UotanStudio.Firmware.AXml;

/// <summary>
/// <para>AXML 属性值数据类型（对应 AOSP <c>Res_value</c> 的 <c>dataType</c>）。</para>
/// AXML attribute value data types (matching the <c>dataType</c> of AOSP <c>Res_value</c>).
/// </summary>
public enum AXmlValueType : byte
{
    /// <summary>空值。<para>Null value.</para></summary>
    Null = 0x00,
    /// <summary>资源引用（@ref）。<para>Resource reference (@ref).</para></summary>
    Reference = 0x01,
    /// <summary>属性引用（?attr）。<para>Attribute reference (?attr).</para></summary>
    Attribute = 0x02,
    /// <summary>字符串索引（指向字符串池）。<para>String index (into the string pool).</para></summary>
    String = 0x03,
    /// <summary>单精度浮点。<para>Single-precision float.</para></summary>
    Float = 0x04,
    /// <summary>尺寸（dp/sp/pt/px/in/mm）。<para>Dimension (dp/sp/pt/px/in/mm).</para></summary>
    Dimension = 0x05,
    /// <summary>分数（% / %p）。<para>Fraction (% / %p).</para></summary>
    Fraction = 0x06,
    /// <summary>动态资源引用。<para>Dynamic resource reference.</para></summary>
    DynamicReference = 0x07,
    /// <summary>动态属性引用。<para>Dynamic attribute reference.</para></summary>
    DynamicAttribute = 0x08,
    /// <summary>十进制整数。<para>Decimal integer.</para></summary>
    IntDec = 0x10,
    /// <summary>十六进制整数。<para>Hex integer.</para></summary>
    IntHex = 0x11,
    /// <summary>布尔值。<para>Boolean.</para></summary>
    IntBoolean = 0x12,
    /// <summary>ARGB8 颜色。<para>ARGB8 color.</para></summary>
    IntColorArgb8 = 0x1c,
    /// <summary>RGB8 颜色。<para>RGB8 color.</para></summary>
    IntColorRgb8 = 0x1d,
    /// <summary>ARGB4 颜色。<para>ARGB4 color.</para></summary>
    IntColorArgb4 = 0x1e,
    /// <summary>RGB4 颜色。<para>RGB4 color.</para></summary>
    IntColorRgb4 = 0x1f
}

/// <summary>
/// <para>AXML 属性：命名空间、名称、原始字符串值与类型化值。</para>
/// AXML attribute: namespace, name, raw string value and typed value.
/// </summary>
public sealed class AXmlAttribute
{
    /// <summary>属性命名空间 URI；无命名空间为 <c>null</c>。<para>Namespace URI, or <c>null</c> when none.</para></summary>
    public string? Namespace { get; init; }

    /// <summary>属性名称。<para>Attribute name.</para></summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>原始字符串值（字符串池索引对应的文本）；无原始值为 <c>null</c>。</summary>
    /// The raw string value (text from the string pool); <c>null</c> when absent.
    public string? RawValue { get; set; }

    /// <summary>类型化值的数据类型。<para>Typed value data type.</para></summary>
    public AXmlValueType ValueType { get; set; }

    /// <summary>类型化值的数据字段。<para>Typed value data field.</para></summary>
    public uint ValueData { get; set; }

    /// <summary>
    /// <para>按数据类型格式化类型化值为可读文本（优先原始值）。</para>
    /// Formats the typed value as readable text per its data type (raw value wins when present).
    /// </summary>
    /// <param name="stringPool">字符串池（用于解析 String 类型）。<para>The string pool (for String-typed values).</para></param>
    /// <param name="resourceMap">资源 ID 映射（用于 Reference 类型）。<para>The resource map (for Reference-typed values).</para></param>
    public string FormatValue(AXmlStringPool? stringPool, IReadOnlyList<uint>? resourceMap = null)
    {
        // 原始字符串优先（所有带 rawValue 的属性在 Android 中均以其为准显示）。
        // The raw string wins for display (attributes carrying a raw value display it).
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
    /// <para>把原始整数值转为只读文本（用于属性面板展示）。</para>
    /// Converts the raw integer value to read-only text (for the properties panel).
    /// </summary>
    public string FormatRawData() => "0x" + ValueData.ToString("x8", CultureInfo.InvariantCulture);

    private string FormatReference(uint data, IReadOnlyList<uint>? resourceMap)
    {
        if (data == 0) return "@null";
        if (resourceMap is not null)
        {
            var idx = checked((int)data);
            if (idx >= 0 && idx < resourceMap.Count && resourceMap[idx] != 0)
            {
                return $"@0x{resourceMap[idx]:x8}";
            }
        }
        return $"@0x{data:x8}";
    }

    private string FormatAttribute(uint data, IReadOnlyList<uint>? resourceMap)
    {
        if (resourceMap is not null)
        {
            var idx = checked((int)data);
            if (idx >= 0 && idx < resourceMap.Count && resourceMap[idx] != 0)
            {
                return $"?0x{resourceMap[idx]:x8}";
            }
        }
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
        var value = (float)unchecked((int)ValueData);
        var unit = DimensionUnit(ValueData & 0x0f);
        value /= DimensionFactor(ValueData & 0x0f);
        return value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    private string FormatFraction()
    {
        var value = (float)unchecked((int)ValueData);
        var unit = (ValueData & 0x0f) switch
        {
            0 => "%",
            1 => "%p",
            _ => string.Empty
        };
        value /= 65536f;
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
        0 => 1f,        // px
        1 => 1f,        // dip
        2 => 1f,        // sp
        3 => 1f,        // pt
        4 => 1f,        // in
        5 => 1f,        // mm
        6 => 65536f,    // complex mantissa scale
        _ => 1f
    };
}

/// <summary>
/// <para>AXML 元素节点：名称、命名空间、属性、子元素与文本。</para>
/// AXML element node: name, namespace, attributes, children and text.
/// </summary>
public sealed class AXmlElement
{
    /// <summary>元素命名空间 URI；无命名空间为 <c>null</c>。<para>Namespace URI, or <c>null</c> when none.</para></summary>
    public string? Namespace { get; set; }

    /// <summary>元素名称。<para>Element name.</para></summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>属性列表。<para>Attribute list.</para></summary>
    public List<AXmlAttribute> Attributes { get; } = [];

    /// <summary>子元素列表。<para>Child elements.</para></summary>
    public List<AXmlElement> Children { get; } = [];

    /// <summary>元素文本（CDATA）；无文本为 <c>null</c>。<para>Element text (CDATA); <c>null</c> when none.</para></summary>
    public string? Text { get; set; }

    /// <summary>源文件行号（用于错误定位）。<para>Source line number (for error location).</para></summary>
    public int LineNumber { get; set; }

    /// <summary>元素深度（根为 0）。<para>Element depth (root is 0).</para></summary>
    public int Depth { get; set; }

    /// <summary>
    /// <para>按名称查找首个直接子元素。</para>
    /// Finds the first direct child element by name.
    /// </summary>
    public AXmlElement? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// <para>解析后的 AXML 文档：根元素、命名空间与字符串池/资源映射（供值显示与重建）。</para>
/// Parsed AXML document: root element, namespaces, and the string pool / resource map
/// (for value display and rebuild).
/// </summary>
public sealed class AXmlDocument
{
    /// <summary>文档根元素。<para>Document root element.</para></summary>
    public AXmlElement Root { get; set; } = new();

    /// <summary>字符串池（属性/元素名称与字符串值）。<para>String pool (names and string values).</para></summary>
    public AXmlStringPool StringPool { get; } = new();

    /// <summary>资源 ID 映射（attribute 索引 → 资源 ID）。<para>Resource map (attribute index → resource ID).</para></summary>
    public List<uint> ResourceMap { get; } = [];

    /// <summary>命名空间声明（prefix → uri）。<para>Namespace declarations (prefix → uri).</para></summary>
    public List<AXmlNamespace> Namespaces { get; } = [];

    /// <summary>文件版本/头部原始大小（重建用）。<para>Header size from the source (for rebuild).</para></summary>
    public int HeaderSize { get; set; } = 8;

    /// <summary>
    /// <para>生成 XML 文本表示（缩进 + 属性 + 文本），供展示与导出。</para>
    /// Produces the XML text representation (indentation + attributes + text) for display and export.
    /// </summary>
    public string ToXml()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        foreach (var ns in Namespaces)
        {
            sb.Append("xmlns:").Append(ns.Prefix).Append("=\"").Append(Escape(ns.Uri)).Append("\"\n");
        }
        AppendElement(sb, this, Root, 0);
        return sb.ToString();
    }

    private static void AppendElement(System.Text.StringBuilder sb, AXmlDocument doc, AXmlElement element, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append('<').Append(element.Name);

        foreach (var attr in element.Attributes)
        {
            sb.Append(' ').Append(attr.Name).Append("=\"")
              .Append(Escape(attr.FormatValue(doc.StringPool, doc.ResourceMap))).Append('"');
        }

        if (element.Text is not null && element.Children.Count == 0)
        {
            sb.Append('>').Append(Escape(element.Text)).Append("</").Append(element.Name).Append(">\n");
            return;
        }

        if (element.Children.Count == 0)
        {
            sb.Append("/>\n");
            return;
        }

        sb.Append(">\n");
        foreach (var child in element.Children)
        {
            AppendElement(sb, doc, child, depth + 1);
        }
        sb.Append(indent).Append("</").Append(element.Name).Append(">\n");
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}

/// <summary>
/// <para>命名空间声明。</para>
/// A namespace declaration.
/// </summary>
public sealed class AXmlNamespace
{
    /// <summary>命名空间前缀（"android" 等）。<para>Namespace prefix (e.g. "android").</para></summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>命名空间 URI。<para>Namespace URI.</para></summary>
    public string Uri { get; init; } = string.Empty;
}
