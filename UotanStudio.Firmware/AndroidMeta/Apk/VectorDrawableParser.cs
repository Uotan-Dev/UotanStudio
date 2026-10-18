using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Resources;

namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>
/// Converts compiled (AXML) vector drawables into <see cref="VectorDrawable"/> models, tolerating
/// malformed or truncated binary XML by returning <see langword="null"/> instead of throwing.
/// <para>把编译后的（AXML）vector drawable 转换为 <see cref="VectorDrawable"/> 模型；对损坏或截断的二进制
/// XML 返回 <see langword="null"/> 而非抛出异常。</para>
/// </summary>
public static class VectorDrawableParser
{
    /// <summary>Parses AXML bytes into a VectorDrawable; returns null when the input root is not &lt;vector&gt;.
    /// <para>解析 AXML 字节为 vector drawable 模型；根元素不是 &lt;vector&gt; 时返回 null。</para></summary>
    /// <param name="bytes">Raw compiled-XML file bytes, or <see langword="null"/>. A null reference, fewer than 4 bytes, a leading byte other than 0x03 (AXML magic) and any AXML structural failure, including oversized claimed lengths, are all rejected silently.
    /// <para>编译后的 XML 原始字节，可为 <see langword="null"/>。空引用、不足 4 字节、首字节不是 0x03 （AXML 魔数），以及任何 AXML 结构性失败（含虚报的超长长度）都会被静默拒绝。</para></param>
    /// <returns>The decoded drawable, or <see langword="null"/> when the input is not well-formed binary XML rooted at a &lt;vector&gt; element; attribute defaults (24f dp sizes, 1f alphas) are applied to accepted documents.
    /// <para>解析成功时返回解码后的 drawable；输入不是以 &lt;vector&gt; 为根的合法二进制 XML 时返回 <see langword="null"/>；对接受的文档套用属性默认值（尺寸 24f dp、alpha 1f）。</para></returns>
    /// <example>
    /// <code>
    /// VectorDrawable? drawable = VectorDrawableParser.Parse(File.ReadAllBytes("icon.xml.axml"));
    /// </code>
    /// </example>
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