using System.Globalization;

using System.Xml.Linq;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml;

public enum AXmlValueType : byte
{
    Null = 0x00,
    Reference = 0x01,
    Attribute = 0x02,
    String = 0x03,
    Float = 0x04,
    Dimension = 0x05,
    Fraction = 0x06,
    DynamicReference = 0x07,
    DynamicAttribute = 0x08,
    IntDec = 0x10,
    IntHex = 0x11,
    IntBoolean = 0x12,
    IntColorArgb8 = 0x1c,
    IntColorRgb8 = 0x1d,
    IntColorArgb4 = 0x1e,
    IntColorRgb4 = 0x1f
}

public sealed class AXmlAttribute
{
    internal int? OriginalNameIndex { get; init; }
    public string? Namespace { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? RawValue { get; set; }

    public AXmlValueType ValueType { get; set; }

    public uint ValueData { get; set; }

    /// <summary>
    /// Formats the typed value as readable text per its data type (raw value wins when present).
    /// <para>按数据类型格式化类型化值为可读文本（优先原始值）。</para>
    /// </summary>
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
    /// Converts the raw integer value to read-only text (for the properties panel).
    /// <para>把原始整数值转为只读文本（用于属性面板展示）。</para>
    /// </summary>
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

public sealed class AXmlElement
{
    public string? Namespace { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<AXmlAttribute> Attributes { get; } = [];

    public List<AXmlElement> Children { get; } = [];

    public string? Text { get; set; }

    public int LineNumber { get; set; }

    public int Depth { get; set; }

    /// <summary>
    /// Finds the first direct child element by name.
    /// <para>按名称查找首个直接子元素。</para>
    /// </summary>
    public AXmlElement? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

public sealed class AXmlDocument
{
    public AXmlElement Root { get; set; } = new();

    public AXmlStringPool StringPool { get; } = new();

    public List<uint> ResourceMap { get; } = [];

    public List<AXmlNamespace> Namespaces { get; } = [];

    public int HeaderSize { get; set; } = 8;

    /// <summary>
    /// Produces the XML text representation (indentation + attributes + text) for display and export.
    /// <para>生成 XML 文本表示（缩进 + 属性 + 文本），供展示与导出。</para>
    /// </summary>
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

public sealed class AXmlNamespace
{
    public string Prefix { get; init; } = string.Empty;

    public string Uri { get; init; } = string.Empty;
}