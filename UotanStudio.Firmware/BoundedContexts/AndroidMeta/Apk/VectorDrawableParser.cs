using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk;

public static class VectorDrawableParser
{
    /// <summary>
    /// Parses AXML bytes into a VectorDrawable; returns null when the input root is not &lt;vector&gt;.
    /// <para>解析 AXML 字节为 vector drawable 模型；根元素不是 &lt;vector&gt; 时返回 null。</para>
    /// </summary>
    public static VectorDrawable? Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4 || bytes[0] != 0x03)
        {
            return null;
        }

        AXmlDocument doc;
        try
        {
            doc = AXmlParser.Parse(bytes);
        }
        catch
        {
            return null;
        }

        var root = doc.Root;
        if (!string.Equals(root.Name, "vector", StringComparison.Ordinal))
        {
            return null;
        }

        var paths = new List<VectorPath>();
        CollectPaths(root, paths, doc.StringPool);

        return new VectorDrawable
        {
            Width = ReadDp(root, "width", 24f),
            Height = ReadDp(root, "height", 24f),
            ViewportWidth = ReadFloat(root, "viewportWidth", 24f),
            ViewportHeight = ReadFloat(root, "viewportHeight", 24f),
            Alpha = ReadFloat(root, "alpha", 1f),
            Paths = paths
        };
    }

    /// <summary>
    /// Recursively collects &lt;path&gt; elements (including &lt;group&gt; nesting).
    /// <para>递归收集 &lt;path&gt; 元素（支持 &lt;group&gt; 嵌套）。</para>
    /// </summary>
    private static void CollectPaths(AXmlElement el, List<VectorPath> paths, AXmlStringPool stringPool)
    {
        foreach (var child in el.Children)
        {
            if (string.Equals(child.Name, "path", StringComparison.Ordinal))
            {
                var pathData = ReadString(child, "pathData", stringPool);
                if (string.IsNullOrWhiteSpace(pathData))
                {
                    continue;
                }

                var fillColor = ReadColor(child, "fillColor");
                var strokeColor = ReadColor(child, "strokeColor");
                paths.Add(new VectorPath
                {
                    PathData = pathData,
                    FillColor = fillColor,
                    FillAlpha = ReadFloat(child, "fillAlpha", 1f),
                    StrokeColor = strokeColor,
                    StrokeWidth = ReadDp(child, "strokeWidth", 0f),
                    StrokeAlpha = ReadFloat(child, "strokeAlpha", 1f)
                });
            }
            else
            {
                CollectPaths(child, paths, stringPool);
            }
        }
    }

    /// <summary>
    /// Reads a string-typed attribute (raw value wins; falls back to string-pool index).
    /// <para>读取字符串型属性值（优先原始值，其次字符串池索引）。</para>
    /// </summary>
    private static string ReadString(AXmlElement el, string name, AXmlStringPool stringPool)
    {
        var attr = FindAttr(el, name);
        if (attr is null)
        {
            return string.Empty;
        }

        if (attr.RawValue is not null)
        {
            return attr.RawValue;
        }

        return attr.ValueType == AXmlValueType.String
            ? stringPool.GetString(checked((int)attr.ValueData))
            : string.Empty;
    }

    /// <summary>
    /// Reads a float attribute from the data field.
    /// <para>读取浮点属性（data 字段即单精度位模式）。</para>
    /// </summary>
    private static float ReadFloat(AXmlElement el, string name, float fallback)
    {
        var attr = FindAttr(el, name);
        return attr is null ? fallback : BitConverter.Int32BitsToSingle(unchecked((int)attr.ValueData));
    }

    /// <summary>
    /// Reads a dp dimension attribute (AOSP complex encoding).
    /// <para>读取 dp 尺寸属性（AOSP complex 编码）。</para>
    /// </summary>
    private static float ReadDp(AXmlElement el, string name, float fallback)
    {
        var attr = FindAttr(el, name);
        if (attr is null)
        {
            return fallback;
        }

        if (attr.ValueType == AXmlValueType.Float)
        {
            return BitConverter.Int32BitsToSingle(unchecked((int)attr.ValueData));
        }

        if (attr.ValueType == AXmlValueType.Dimension)
        {


            var data = attr.ValueData;
            var mantissa = (int)(data >> 8) & 0xFFFFFF;
            if ((mantissa & 0x800000) != 0)
            {
                mantissa |= unchecked((int)0xFF000000);
            }

            var radix = (int)((data >> 4) & 0x03);
            return mantissa * MathF.Pow(2, radix);
        }

        return fallback;
    }

    /// <summary>
    /// Reads a color attribute (ARGB8/RGB8); RGB8 gets a fully opaque alpha.
    /// <para>读取颜色属性（ARGB8/RGB8）；RGB8 补全 alpha 为不透明。</para>
    /// </summary>
    private static uint? ReadColor(AXmlElement el, string name)
    {
        var attr = FindAttr(el, name);
        if (attr is null)
        {
            return null;
        }

        return attr.ValueType switch
        {
            AXmlValueType.IntColorArgb8 => attr.ValueData,
            AXmlValueType.IntColorRgb8 => attr.ValueData | 0xFF000000u,
            _ => null
        };
    }

    /// <summary>
    /// Finds an attribute by name (case-insensitive, ignoring namespace prefixes).
    /// <para>按名称查找属性（不区分大小写，忽略命名空间前缀）。</para>
    /// </summary>
    private static AXmlAttribute? FindAttr(AXmlElement el, string name)
    {
        foreach (var attr in el.Attributes)
        {
            var attrName = attr.Name;
            var colon = attrName.LastIndexOf(':');
            if (colon >= 0)
            {
                attrName = attrName[(colon + 1)..];
            }

            if (string.Equals(attrName, name, StringComparison.OrdinalIgnoreCase))
            {
                return attr;
            }
        }

        return null;
    }
}
