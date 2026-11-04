using System.Globalization;
using System.Xml.Linq;

namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Minimal reader for the XML property lists emitted by <c>diskutil -plist</c>.
/// <para><c>diskutil -plist</c> 输出的 XML property list 的最小读取器。</para>
/// </summary>
internal static class MacPlist
{
    /// <summary>
    /// Parses an XML plist into dictionaries, lists, strings, numbers and booleans.
    /// <para>把 XML plist 解析为字典、列表、字符串、数字与布尔值。</para>
    /// </summary>
    /// <param name="xml">Command output.<para>命令输出。</para></param>
    /// <returns>The root value, or <see langword="null"/> when the document has no plist root.
    /// <para>根值；文档没有 plist 根节点时为 <see langword="null"/>。</para></returns>
    internal static object? Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        return ParseNode(document.Element("plist")?.Elements().FirstOrDefault());
    }

    /// <summary>
    /// Reads a dictionary value by key.
    /// <para>按键读取字典值。</para>
    /// </summary>
    /// <param name="node">Dictionary node.<para>字典节点。</para></param>
    /// <param name="key">Key to look up.<para>要查询的键。</para></param>
    /// <returns>The value, or <see langword="null"/> when the node is not a dictionary or the key is missing.
    /// <para>值；节点不是字典或键不存在时为 <see langword="null"/>。</para></returns>
    internal static object? GetValue(object? node, string key) =>
        node is IReadOnlyDictionary<string, object?> dictionary && dictionary.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Reads a string value by key.
    /// <para>按键读取字符串值。</para>
    /// </summary>
    /// <param name="node">Dictionary node.<para>字典节点。</para></param>
    /// <param name="key">Key to look up.<para>要查询的键。</para></param>
    /// <returns>The string, or <see langword="null"/> when absent or not a string.
    /// <para>字符串；缺失或不是字符串时为 <see langword="null"/>。</para></returns>
    internal static string? GetString(object? node, string key) => GetValue(node, key) as string;

    /// <summary>
    /// Reads an integer value by key.
    /// <para>按键读取整数值。</para>
    /// </summary>
    /// <param name="node">Dictionary node.<para>字典节点。</para></param>
    /// <param name="key">Key to look up.<para>要查询的键。</para></param>
    /// <returns>The integer, or <see langword="null"/> when absent or not an integer.
    /// <para>整数；缺失或不是整数时为 <see langword="null"/>。</para></returns>
    internal static long? GetInt64(object? node, string key) => GetValue(node, key) is long value ? value : null;

    /// <summary>
    /// Reads a boolean value by key, accepting both plist booleans and textual flags.
    /// <para>按键读取布尔值，同时接受 plist 布尔与文本标志。</para>
    /// </summary>
    /// <param name="node">Dictionary node.<para>字典节点。</para></param>
    /// <param name="key">Key to look up.<para>要查询的键。</para></param>
    /// <returns>The boolean, or <see langword="null"/> when absent or not recognized.
    /// <para>布尔值；缺失或无法识别时为 <see langword="null"/>。</para></returns>
    internal static bool? GetBoolean(object? node, string key)
    {
        return GetValue(node, key) switch
        {
            bool value => value,
            string text when bool.TryParse(text, out var parsed) => parsed,
            string text when text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || text.Equals("removable", StringComparison.OrdinalIgnoreCase) => true,
            _ => null,
        };
    }

    /// <summary>
    /// Reads an array value by key.
    /// <para>按键读取数组值。</para>
    /// </summary>
    /// <param name="node">Dictionary node.<para>字典节点。</para></param>
    /// <param name="key">Key to look up.<para>要查询的键。</para></param>
    /// <returns>The array items, or an empty list when absent or not an array.
    /// <para>数组项；缺失或不是数组时为空列表。</para></returns>
    internal static IReadOnlyList<object?> GetArray(object? node, string key) =>
        GetValue(node, key) as IReadOnlyList<object?> ?? [];

    private static object? ParseNode(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        switch (element.Name.LocalName)
        {
            case "dict":
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                var children = element.Elements().ToArray();
                for (var index = 0; index + 1 < children.Length; index += 2)
                {
                    if (children[index].Name.LocalName == "key")
                    {
                        dictionary[children[index].Value] = ParseNode(children[index + 1]);
                    }
                }

                return dictionary;
            case "array":
                return element.Elements().Select(ParseNode).ToList();
            case "string":
                return element.Value;
            case "integer":
                return long.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer
                    : null;
            case "real":
                return double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    ? real
                    : null;
            case "true":
                return true;
            case "false":
                return false;
            case "date":
            case "data":
                return element.Value;
            default:
                return element.Value;
        }
    }
}