using System.Collections;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Supported XML changes; XPath selects existing elements or attributes.
/// <para>支持的 XML 变更操作；XPath 只能选中已存在的元素或属性。</para>
/// </summary>
public enum XmlPatchOperation
{
    /// <summary>Replaces the value of a selected attribute or childless element. <para>替换选中的属性或无子元素节点的文本值。</para></summary>
    SetValue,

    /// <summary>Sets the named attribute on each selected element. <para>在每个选中的元素上设置指定名称的属性。</para></summary>
    SetAttribute,

    /// <summary>
    /// Appends the fragment root parsed from the value to each selected element unless an equivalent child already exists.
    /// <para>将 <c>value</c> 解析出的片段根节点追加到每个选中的元素；已存在等价子节点时跳过。</para>
    /// </summary>
    AppendElement,

    /// <summary>Removes the selected attribute, or a selected element that has a parent. <para>移除选中的属性，或具有父节点的选中元素。</para></summary>
    Delete
}

/// <summary>
/// DTD-free XML query and editing with guarded cardinality.
/// <para>禁用 DTD 的 XML 查询与编辑，并对匹配数量加以约束。</para>
/// </summary>
public static class XmlPatch
{
    /// <summary>
    /// Queries element or attribute values. Namespace mappings use one prefix=URI per line.
    /// <para>查询元素或属性的值。命名空间映射每行一条 prefix=URI。</para>
    /// </summary>
    /// <param name="text">XML document text, bounded by <see cref="TextPatch.MaxCharacters"/>.
    /// <para>XML 文档文本，大小受 <see cref="TextPatch.MaxCharacters"/> 限制。</para></param>
    /// <param name="xpath">XPath expression, 1 to 4096 characters, selecting only elements or attributes.
    /// <para>XPath 表达式，长度 1 到 4096 个字符，只能选中元素或属性。</para></param>
    /// <param name="namespaces">Namespace mappings, one prefix=URI per line, at most 16384 characters; may be empty.
    /// <para>命名空间映射，每行一条 prefix=URI，最多 16384 个字符，可以为空。</para></param>
    /// <param name="expectedMatches">Exact number of targets the expression must select, 0 to 100000.
    /// <para>表达式必须选中的精确目标数，范围 0 到 100000。</para></param>
    /// <returns>The value of every selected element or attribute, in selection order.
    /// <para>每个选中的元素或属性的值，按选中顺序排列。</para></returns>
    /// <exception cref="InvalidDataException">
    /// A size or cardinality limit is exceeded, a namespace mapping is malformed, or the expression selects nodes that are
    /// not elements or attributes.
    /// <para>超出大小或数量上限、命名空间映射格式错误，或表达式选中的节点不是元素或属性。</para></exception>
    public static IReadOnlyList<string> Query(string text, string xpath, string namespaces, int expectedMatches)
    {
        return Select(Parse(text), xpath, namespaces, expectedMatches).Select(Value).ToArray();
    }

    /// <summary>
    /// Applies a structural change while retaining whitespace nodes and the original newline style.
    /// <para>应用结构性变更，同时保留空白节点与原有的换行风格。</para>
    /// </summary>
    /// <param name="text">XML document text to edit.
    /// <para>待编辑的 XML 文档文本。</para></param>
    /// <param name="xpath">XPath expression, 1 to 4096 characters, selecting only elements or attributes.
    /// <para>XPath 表达式，长度 1 到 4096 个字符，只能选中元素或属性。</para></param>
    /// <param name="namespaces">Namespace mappings, one prefix=URI per line, at most 16384 characters; may be empty.
    /// <para>命名空间映射，每行一条 prefix=URI，最多 16384 个字符，可以为空。</para></param>
    /// <param name="operation">The structural change to apply to every selected target.
    /// <para>应用于每个选中的目标的结构变更。</para></param>
    /// <param name="value">New value, or an XML fragment for <see cref="XmlPatchOperation.AppendElement"/>; it is size-checked
    /// and, for the append operation, parsed with DTD processing prohibited.
    /// <para>新值；对 <see cref="XmlPatchOperation.AppendElement"/> 则为 XML 片段，会检查大小并以禁用 DTD 的方式解析。</para></param>
    /// <param name="attributeName">Attribute name consulted only by <see cref="XmlPatchOperation.SetAttribute"/>; other operations
    /// still count its length against the expansion guard.
    /// <para>仅由 <see cref="XmlPatchOperation.SetAttribute"/> 使用的属性名；其他操作仍会将其长度计入扩张守卫。</para></param>
    /// <param name="expectedMatches">Exact number of targets the XPath must select, 0 to 100000.
    /// <para>XPath 必须选中的精确目标数，范围 0 到 100000。</para></param>
    /// <param name="checkOldValue">When <see langword="true"/>, each target's current value must equal
    /// <paramref name="expectedOldValue"/> before it is changed.
    /// <para>为 <see langword="true"/> 时，每个目标的当前值必须先等于 <paramref name="expectedOldValue"/> 才会被修改。</para></param>
    /// <param name="expectedOldValue">Required old value under <paramref name="checkOldValue"/>, compared with ordinal semantics.
    /// <para>启用 <paramref name="checkOldValue"/> 时要求的旧值，按 Ordinal 规则比较。</para></param>
    /// <returns>
    /// The patched document with the changed flag set, or the original text when every target already holds the desired value
    /// or an equivalent appended child. The declaration prefix and newline style of the input are preserved, and the result is
    /// re-parsed before being returned.
    /// <para>修改标志置位的补丁后文档；当每个目标已是期望值或已存在等价的追加子节点时返回原始文本。结果会保留输入的声明前缀与换行风格，并在返回前重新解析。</para></returns>
    /// <exception cref="InvalidDataException">
    /// The input or value exceeds the size limit, a namespace mapping is malformed, the projected or final expansion exceeds
    /// <see cref="TextPatch.MaxCharacters"/>, the match count or old-value check fails, the appended fragment has no root, or
    /// the operation is incompatible with a selected target.
    /// <para>输入或新值超出大小上限、命名空间映射格式错误、预期或最终扩展超过 <see cref="TextPatch.MaxCharacters"/>、匹配数或旧值校验失败、追加的片段没有根节点，或操作与选中的目标不兼容。</para></exception>
    public static TextPatchResult Apply(string text, string xpath, string namespaces, XmlPatchOperation operation,
        string value, string attributeName, int expectedMatches, bool checkOldValue, string expectedOldValue)
    {
        var document = Parse(text);
        var selected = Select(document, xpath, namespaces, expectedMatches);
        TextPatch.ValidateSize(value);
        if (operation != XmlPatchOperation.Delete
            && (long)text.Length + ((long)selected.Count * ((value.Length * 6L) + attributeName.Length + 8)) > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Projected XML expansion exceeds the patch size limit.");
        }
        var changed = false;
        foreach (var target in selected)
        {
            var current = operation == XmlPatchOperation.SetAttribute && target is XElement parent
                ? (string?)parent.Attribute(XName.Get(attributeName)) : Value(target);
            if (operation is XmlPatchOperation.SetValue or XmlPatchOperation.SetAttribute
                && string.Equals(current, value, StringComparison.Ordinal))
            {
                continue;
            }
            if (checkOldValue && !string.Equals(current, expectedOldValue, StringComparison.Ordinal))
            {
                throw new InvalidDataException("XML target does not match the expected old value.");
            }
            switch (operation)
            {
                case XmlPatchOperation.SetValue when target is XAttribute attribute:
                    attribute.Value = value;
                    changed = true;
                    break;
                case XmlPatchOperation.SetValue when target is XElement element && !element.HasElements:
                    element.Value = value;
                    changed = true;
                    break;
                case XmlPatchOperation.SetAttribute when target is XElement element:
                    element.SetAttributeValue(XName.Get(attributeName), value);
                    changed = true;
                    break;
                case XmlPatchOperation.AppendElement when target is XElement element:
                    var child = Parse(value).Root ?? throw new InvalidDataException("Missing XML fragment root.");
                    if (!element.Elements().Any(existing => XNode.DeepEquals(existing, child)))
                    {
                        element.Add(new XElement(child));
                        changed = true;
                    }
                    break;
                case XmlPatchOperation.Delete when target is XAttribute attribute:
                    attribute.Remove();
                    changed = true;
                    break;
                case XmlPatchOperation.Delete when target is XElement element && element.Parent is not null:
                    element.Remove();
                    changed = true;
                    break;
                default:
                    throw new InvalidDataException("The XML operation is incompatible with the selected target.");
            }
        }
        if (!changed)
        {
            return new TextPatchResult(text, selected.Count, false);
        }
        var body = document.ToString(SaveOptions.DisableFormatting);
        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            body = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }
        if (text.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var end = text.IndexOf("?>", StringComparison.Ordinal);
            body = text[..(end + 2)] + body;
        }
        TextPatch.ValidateSize(body);
        _ = Parse(body);
        return new TextPatchResult(body, selected.Count, true);
    }

    private static XDocument Parse(string text)
    {
        TextPatch.ValidateSize(text);
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = TextPatch.MaxCharacters,
            MaxCharactersFromEntities = 1024
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static List<XObject> Select(XDocument document, string xpath, string namespaces, int expectedMatches)
    {
        if (expectedMatches is < 0 or > 100_000 || xpath.Length is 0 or > 4096 || namespaces.Length > 16_384)
        {
            throw new InvalidDataException("Invalid XPath or match limit.");
        }
        var resolver = new XmlNamespaceManager(new NameTable());
        foreach (var mapping in namespaces.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = mapping.IndexOf('=');
            if (equals <= 0 || equals == mapping.Length - 1)
            {
                throw new InvalidDataException("Namespace mappings must use prefix=URI.");
            }
            resolver.AddNamespace(mapping[..equals].Trim(), mapping[(equals + 1)..].Trim());
        }
        if (document.XPathEvaluate(xpath, resolver) is not IEnumerable matches)
        {
            throw new InvalidDataException("XPath must select elements or attributes.");
        }
        var selected = new List<XObject>();
        foreach (var item in matches)
        {
            if (item is not XElement and not XAttribute)
            {
                throw new InvalidDataException("XPath must select elements or attributes.");
            }
            selected.Add((XObject)item);
            if (selected.Count > expectedMatches)
            {
                throw new InvalidDataException($"Expected {expectedMatches} XML targets; found more.");
            }
        }
        if (selected.Count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} XML targets; found {selected.Count}.");
        }
        return selected;
    }

    private static string Value(XObject item) => item switch
    {
        XElement element => element.Value,
        XAttribute attribute => attribute.Value,
        _ => throw new InvalidDataException("Unsupported XML target.")
    };
}