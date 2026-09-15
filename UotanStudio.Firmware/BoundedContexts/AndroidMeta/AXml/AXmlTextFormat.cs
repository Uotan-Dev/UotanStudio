using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml;

/// <summary>
/// <para>AXML 的 XML 文本表示与模型之间的转换。文本由 <see cref="AXmlDocument.ToXml"/> 生成；
/// 反向解析用于 Manifest 编辑器「文本替换 → 重编译保存」路径。反向解析以原模型为类型参照：
/// 结构未变时逐元素/逐属性复用原始 <see cref="AXmlValueType"/>，文本变更后再按该类型重新编码，
/// 从而在编辑字符串值的同时保留整数、引用、布尔等强类型属性的字节语义。</para>
/// Conversion between the XML text representation of AXML and its model. The text is produced by
/// <see cref="AXmlDocument.ToXml"/>; reverse parsing serves the Manifest editor's
/// "text replacement → recompile-save" path. Reverse parsing treats the original model as a type
/// reference: when the structure is unchanged it reuses each element's/attribute's original
/// <see cref="AXmlValueType"/> and re-encodes the changed text per that type, preserving the byte
/// semantics of typed attributes (ints, references, booleans) while string values are edited.
/// </summary>
public static class AXmlTextFormat
{
    /// <summary>
    /// <para>把 XML 文本解析回 AXML 模型；<paramref name="reference"/> 提供类型与命名空间参照。</para>
    /// Parses XML text back into an AXML model; <paramref name="reference"/> supplies type and
    /// namespace hints.
    /// </summary>
    /// <param name="xml">XML 文本（通常来自 <see cref="AXmlDocument.ToXml"/>）。<para>The XML text (usually from <see cref="AXmlDocument.ToXml"/>).</para></param>
    /// <param name="reference">原模型；为 <c>null</c> 时按文本推断类型。<para>The original model; types are inferred from text when <c>null</c>.</para></param>
    /// <returns>重建的 AXML 模型。<para>The rebuilt AXML model.</para></returns>
    /// <exception cref="AXmlException">XML 解析失败或缺少根元素时抛出。<para>Thrown when XML parsing fails or the root element is missing.</para></exception>
    public static AXmlDocument Parse(string xml, AXmlDocument? reference = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new AXmlException($"Invalid XML text: {ex.Message}");
        }

        if (document.Root is null)
        {
            throw new AXmlException("XML text has no root element");
        }

        var result = new AXmlDocument { HeaderSize = reference?.HeaderSize ?? 8 };
        if (reference is not null)
        {
            foreach (var ns in reference.Namespaces)
            {
                result.Namespaces.Add(ns);
            }

            result.ResourceMap.AddRange(reference.ResourceMap);
        }
        else
        {
            CollectNamespaces(document.Root, result);
        }

        result.Root = BuildElement(document.Root, reference?.Root, reference, result, depth: 0);
        return result;
    }

    private static void CollectNamespaces(XElement root, AXmlDocument document)
    {
        foreach (var attribute in root.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            var prefix = attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName;
            document.Namespaces.Add(new AXmlNamespace { Prefix = prefix, Uri = attribute.Value });
        }
    }

    private static AXmlElement BuildElement(XElement source, AXmlElement? reference, AXmlDocument? referenceDocument,
        AXmlDocument document, int depth)
    {
        var element = new AXmlElement
        {
            Name = source.Name.LocalName,
            Namespace = string.IsNullOrEmpty(source.Name.NamespaceName) ? null : source.Name.NamespaceName,
            Depth = depth,
            LineNumber = source is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0
        };

        foreach (var attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            element.Attributes.Add(BuildAttribute(attribute, FindReferenceAttribute(reference, attribute),
                referenceDocument, document));
        }

        var children = source.Elements().ToList();
        if (children.Count == 0)
        {
            var text = string.Concat(source.Nodes().OfType<XText>().Select(node => node.Value));
            if (text.Length > 0)
            {
                element.Text = text;
            }
        }
        else
        {
            // 参照模型按文档顺序平行遍历；类型提示按同名属性匹配。
            // The reference model is walked in document order; type hints match by attribute name.
            var referenceChildren = reference?.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var referenceChild = referenceChildren is not null && i < referenceChildren.Count
                    ? referenceChildren[i]
                    : null;
                element.Children.Add(BuildElement(children[i], referenceChild, referenceDocument, document, depth + 1));
            }
        }

        return element;
    }

    private static AXmlAttribute? FindReferenceAttribute(AXmlElement? reference, XAttribute attribute)
    {
        if (reference is null)
        {
            return null;
        }

        var namespaceName = string.IsNullOrEmpty(attribute.Name.NamespaceName) ? null : attribute.Name.NamespaceName;
        foreach (var candidate in reference.Attributes)
        {
            if (string.Equals(candidate.Name, attribute.Name.LocalName, StringComparison.Ordinal)
                && string.Equals(candidate.Namespace, namespaceName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static AXmlAttribute BuildAttribute(XAttribute source, AXmlAttribute? reference,
        AXmlDocument? referenceDocument, AXmlDocument document)
    {
        var text = source.Value;
        var name = source.Name.LocalName;
        var namespaceName = string.IsNullOrEmpty(source.Name.NamespaceName) ? null : source.Name.NamespaceName;

        if (reference is not null && referenceDocument is not null
            && string.Equals(reference.FormatValue(referenceDocument.StringPool, referenceDocument.ResourceMap),
                text, StringComparison.Ordinal))
        {
            // 文本未变：复用原始类型与编码值；字符串型的 ValueData 是新池索引，须重新 Intern。
            // Unchanged text: reuse the original type and encoded value; a String-typed ValueData is a
            // pool index and must be re-interned into the new document's pool.
            var valueData = reference.ValueType == AXmlValueType.String
                ? (uint)document.StringPool.Intern(text)
                : reference.ValueData;
            return new AXmlAttribute
            {
                Name = name,
                Namespace = namespaceName,
                OriginalNameIndex = reference.OriginalNameIndex,
                RawValue = reference.RawValue ?? (reference.ValueType == AXmlValueType.String ? text : null),
                ValueType = reference.ValueType,
                ValueData = valueData
            };
        }

        var valueType = reference?.ValueType ?? InferType(text);
        return Encode(name, namespaceName, text, valueType, document.StringPool);
    }

    private static AXmlAttribute Encode(string name, string? namespaceName, string text,
        AXmlValueType type, AXmlStringPool pool)
    {
        if (TryEncode(text, type, pool, out var data))
        {
            return new AXmlAttribute
            {
                Name = name,
                Namespace = namespaceName,
                RawValue = text,
                ValueType = type,
                ValueData = data
            };
        }

        // 类型编码失败时退回字符串，避免丢失用户输入的文本。
        // Fall back to a string when type encoding fails so no user text is lost.
        return new AXmlAttribute
        {
            Name = name,
            Namespace = namespaceName,
            RawValue = text,
            ValueType = AXmlValueType.String,
            ValueData = (uint)pool.Intern(text)
        };
    }

    /// <summary>
    /// <para>无参照模型时按文本形态推断类型（十六进制/颜色/引用/属性/布尔/整数/字符串）。</para>
    /// Infers a type from the text shape when no reference model is available (hex/color/reference/
    /// attribute/boolean/integer/string).
    /// </summary>
    private static AXmlValueType InferType(string text)
    {
        if (text.StartsWith("?0x", StringComparison.OrdinalIgnoreCase))
        {
            return AXmlValueType.Attribute;
        }

        if (text.StartsWith("@null", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("@0x", StringComparison.OrdinalIgnoreCase))
        {
            return AXmlValueType.Reference;
        }

        if (text.StartsWith('#'))
        {
            return text.Length > 7 ? AXmlValueType.IntColorArgb8 : AXmlValueType.IntColorRgb8;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
        {
            return AXmlValueType.IntHex;
        }

        if (bool.TryParse(text, out _))
        {
            return AXmlValueType.IntBoolean;
        }

        return AXmlValueType.String;
    }

    private static bool TryEncode(string text, AXmlValueType type, AXmlStringPool pool, out uint data)
    {
        data = 0;
        switch (type)
        {
            case AXmlValueType.String:
                data = (uint)pool.Intern(text);
                return true;
            case AXmlValueType.Null:
                data = 0;
                return true;
            case AXmlValueType.IntDec:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                {
                    data = unchecked((uint)dec);
                    return true;
                }

                return false;
            case AXmlValueType.IntHex:
                return TryParseHex(text, "0x", out data);
            case AXmlValueType.IntBoolean:
                if (bool.TryParse(text, out var boolean))
                {
                    data = boolean ? 1u : 0u;
                    return true;
                }

                return false;
            case AXmlValueType.Float:
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    data = unchecked((uint)BitConverter.SingleToInt32Bits(value));
                    return true;
                }

                return false;
            case AXmlValueType.IntColorArgb8:
            case AXmlValueType.IntColorRgb8:
            case AXmlValueType.IntColorArgb4:
            case AXmlValueType.IntColorRgb4:
                return TryParseHex(text, "#", out data);
            case AXmlValueType.Reference:
            case AXmlValueType.DynamicReference:
                if (text.StartsWith("@null", StringComparison.OrdinalIgnoreCase))
                {
                    data = 0;
                    return true;
                }

                return TryParseHex(text, "@0x", out data);
            case AXmlValueType.Attribute:
            case AXmlValueType.DynamicAttribute:
                return TryParseHex(text, "?0x", out data);
            default:
                return false;
        }
    }

    private static bool TryParseHex(string text, string prefix, out uint data)
    {
        data = 0;
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uint.TryParse(text.AsSpan(prefix.Length), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out data);
    }
}