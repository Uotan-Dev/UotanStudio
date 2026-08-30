using System.Buffers.Binary;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Apk;

/// <summary>
/// <para>Android vector drawable AXML 解析器：从 <c>&lt;vector&gt;</c> 根元素提取
/// 视口尺寸、透明度与全部 <c>&lt;path&gt;</c> 的路径数据/填充/描边，供应用层渲染。
/// 纯托管、无反射、AOT 兼容；仅接受 AXML 魔数（<c>03 00 08 00</c>）输入。</para>
/// Parser for Android vector drawable AXML: extracts viewport size, alpha and every
/// <c>&lt;path&gt;</c> path-data/fill/stroke from the <c>&lt;vector&gt;</c> root so the UI
/// can render a bitmap. Fully managed, reflection-free and AOT-friendly; only accepts
/// AXML magic (<c>03 00 08 00</c>) input.
/// </summary>
public static class VectorDrawableParser
{
    /// <summary>
    /// <para>解析 AXML 字节为 vector drawable 模型；输入不是 <c>&lt;vector&gt;</c> 根
    /// 的 AXML 时返回 <c>null</c>。</para>
    /// Parses AXML bytes into a <see cref="VectorDrawable"/>; returns <c>null</c> when the
    /// input is not an AXML whose root is <c>&lt;vector&gt;</c>.
    /// </summary>
    /// <param name="bytes">AXML 字节。<para>AXML bytes.</para></param>
    /// <returns>模型；不适用时返回 <c>null</c>。<para>The model, or <c>null</c> when not applicable.</para></returns>
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
    /// <para>递归收集 <c>&lt;path&gt;</c> 元素（支持 <c>&lt;group&gt;</c> 嵌套）。</para>
    /// Recursively collects <c>&lt;path&gt;</c> elements (including <c>&lt;group&gt;</c> nesting).
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
    /// <para>读取字符串型属性值（优先原始值，其次字符串池索引）。</para>
    /// Reads a string-typed attribute (raw value wins; falls back to the string-pool index).
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
    /// <para>读取浮点属性（<c>Float</c> 类型的 data 字段即单精度位模式）。</para>
    /// Reads a float attribute (the <c>Float</c> data field already holds the single-precision bits).
    /// </summary>
    private static float ReadFloat(AXmlElement el, string name, float fallback)
    {
        var attr = FindAttr(el, name);
        return attr is null ? fallback : BitConverter.Int32BitsToSingle(unchecked((int)attr.ValueData));
    }

    /// <summary>
    /// <para>读取 dp 尺寸属性（<c>Dimension</c>：AOSP complex 编码，高 24 位尾数 + radix，
    /// 单位固定 dp/px 等；UI 渲染按逻辑像素使用，忽略单位差异）。</para>
    /// Reads a dp dimension attribute (<c>Dimension</c>: AOSP complex encoding, 24-bit
    /// mantissa + radix; units are treated as logical pixels for rendering).
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
            // AOSP Res_value complexToFloat：尾数(24 位) + radix(2 位)。
            // AOSP Res_value complexToFloat: 24-bit mantissa plus 2-bit radix.
            var data = attr.ValueData;
            var mantissa = (int)(data >> 8) & 0xFFFFFF;
            if ((mantissa & 0x800000) != 0)
            {
                mantissa |= unchecked((int)0xFF000000); // 符号扩展
            }

            var radix = (int)((data >> 4) & 0x03);
            return mantissa * MathF.Pow(2, radix);
        }

        return fallback;
    }

    /// <summary>
    /// <para>读取颜色属性（<c>IntColorArgb8</c>/<c>IntColorRgb8</c>）；RGB8 补全 alpha 为不透明。</para>
    /// Reads a color attribute (<c>IntColorArgb8</c>/<c>IntColorRgb8</c>); RGB8 gets a fully opaque alpha.
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
    /// <para>按名称查找属性（不区分大小写，忽略命名空间前缀）。</para>
    /// Finds an attribute by name (case-insensitive, ignoring namespace prefixes).
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
