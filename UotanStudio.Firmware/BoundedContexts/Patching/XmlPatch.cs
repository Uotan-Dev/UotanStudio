using System.Collections;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Supported XML changes; XPath selects existing elements or attributes.</summary>
public enum XmlPatchOperation
{
    SetValue,
    SetAttribute,
    AppendElement,
    Delete
}

/// <summary>DTD-free XML query and editing with guarded cardinality.</summary>
public static class XmlPatch
{
    /// <summary>Queries element or attribute values. Namespace mappings use one prefix=URI per line.</summary>
    public static IReadOnlyList<string> Query(string text, string xpath, string namespaces, int expectedMatches)
    {
        return Select(Parse(text), xpath, namespaces, expectedMatches).Select(Value).ToArray();
    }

    /// <summary>Applies a structural change while retaining whitespace nodes and the original newline style.</summary>
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
        // XDocument.ToString omits the declaration. Preserve its original spelling, including encoding.
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