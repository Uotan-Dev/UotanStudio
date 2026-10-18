using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Recognised storage shape of one privapp-permissions allow entry inside an XML document.
/// <para>privapp-permissions 授权条目在 XML 文档中的存储形态。</para>
/// </summary>
internal enum PrivAppPermissionShape
{
    /// <summary>A <c>&lt;permission name="..."/&gt;</c> child of a <c>&lt;privapp-permissions package="..."&gt;</c> container.
    /// <para>位于 <c>&lt;privapp-permissions package="..."&gt;</c> 容器内的 <c>&lt;permission name="..."/&gt;</c> 子节点。</para></summary>
    ContainerChild,

    /// <summary>A flat <c>&lt;permission name="..." package="..."/&gt;</c> child of the root element.
    /// <para>根元素下的扁平 <c>&lt;permission name="..." package="..."/&gt;</c> 子节点。</para></summary>
    FlatPermission,

    /// <summary>A flat <c>&lt;allow name="..." package="..."/&gt;</c> override child of the root element.
    /// <para>根元素下的扁平 <c>&lt;allow name="..." package="..."/&gt;</c> 覆盖子节点。</para></summary>
    FlatAllow
}

/// <summary>
/// One parsed package/permission allow pair.
/// <para>解析得到的一条包名/权限授权对。</para>
/// </summary>
/// <param name="Package">Granting application package name. <para>授权目标应用包名。</para></param>
/// <param name="Permission">Requested permission identifier. <para>申请的权限标识。</para></param>
/// <param name="Shape">Document shape that carried the entry. <para>承载该条目的文档形态。</para></param>
internal sealed record PrivAppPermissionEntry(string Package, string Permission, PrivAppPermissionShape Shape);

/// <summary>
/// Parses and edits the XML allow lists that grant privileged apps their signature-restricted permissions:
/// the nested <c>&lt;privapp-permissions package="..."&gt;&lt;permission name="..."/&gt;&lt;/privapp-permissions&gt;</c>
/// whitelist shape, the flat <c>&lt;permission name="..." package="..."/&gt;</c> shape, and the
/// <c>&lt;allow name="..." package="..."/&gt;</c> override shape. Edits are idempotent (applying the same change
/// twice equals applying it once), preserve unrelated elements, comments and processing instructions verbatim,
/// and keep the original declaration prefix and newline style. The document format explicitly allows comments;
/// they are treated as first-class preserved content, never stripped. Untrusted documents are size-bounded by
/// <see cref="TextPatch.MaxCharacters"/>, parsed with DTD processing prohibited, and malformed entries are
/// rejected with <see cref="InvalidDataException"/>.
/// <para>解析并编辑授予特权应用签名级权限的 XML 允许列表：嵌套白名单形态
/// <c>&lt;privapp-permissions package="..."&gt;&lt;permission name="..."/&gt;&lt;/privapp-permissions&gt;</c>、
/// 扁平形态 <c>&lt;permission name="..." package="..."/&gt;</c>，以及覆盖形态 <c>&lt;allow name="..." package="..."/&gt;</c>。
/// 编辑具备幂等性（应用两次等于一次），逐字保留无关元素、注释与处理指令，并保留原有声明前缀与换行风格。
/// 该文件格式明确允许注释；注释被视为必须保留的一等内容，绝不剥离。不可信文档按
/// <see cref="TextPatch.MaxCharacters"/> 限制大小，解析时禁用 DTD，格式错误的条目会以
/// <see cref="InvalidDataException"/> 拒绝。</para>
/// </summary>
internal static class PrivAppPermissionsPatch
{
    private const int MaximumPackageNameLength = 200;
    private const int MaximumPermissionLength = 256;
    private const int MaximumEntries = 100_000;

    /// <summary>
    /// Enumerates every recognised allow entry in the document and rejects malformed input.
    /// <para>枚举文档中所有可识别的授权条目，并拒绝畸形输入。</para>
    /// </summary>
    /// <param name="text">XML document text, bounded by <see cref="TextPatch.MaxCharacters"/>.
    /// <para>XML 文档文本，大小受 <see cref="TextPatch.MaxCharacters"/> 限制。</para></param>
    /// <returns>The entries in document order. <para>按文档顺序返回的条目。</para></returns>
    /// <exception cref="InvalidDataException">The document is malformed or carries an unrecognisable recognised element.
    /// <para>文档畸形，或带有可识别却无法正常解读的元素。</para></exception>
    public static IReadOnlyList<PrivAppPermissionEntry> Parse(string text)
    {
        var entries = new List<PrivAppPermissionEntry>();
        foreach (var entry in Enumerate(ParseDocument(text)))
        {
            entries.Add(entry);
            if (entries.Count > MaximumEntries)
            {
                throw new PatchingCorruptedException("The privapp-permissions document carries too many entries.");
            }
        }
        return entries;
    }

    /// <summary>Adds one package and permission allow entry to the document.
    /// <para>向文档添加一条包名与权限授权条目。</para></summary>
    /// <param name="text">XML document text, bounded by <see cref="TextPatch.MaxCharacters"/>.
    /// <para>XML 文档文本，大小受 <see cref="TextPatch.MaxCharacters"/> 限制。</para></param>
    /// <param name="packageName">Granting application package name; non-blank, at most 200 characters, no control characters.
    /// <para>授权目标应用包名；非空白、至多 200 个字符且无控制字符。</para></param>
    /// <param name="permission">Permission identifier; non-blank, at most 256 characters, no control characters.
    /// <para>权限标识；非空白、至多 256 个字符且无控制字符。</para></param>
    /// <returns>The patched document, or the untouched input when the entry already exists.
    /// <para>补丁后的文档；条目已存在时原样返回输入。</para></returns>
    /// <exception cref="InvalidDataException">The document is malformed or an identifier is invalid.
    /// <para>文档畸形，或标识不合法。</para></exception>
    public static TextPatchResult Add(string text, string packageName, string permission)
    {
        ValidateIdentifier(packageName, MaximumPackageNameLength, "Package name");
        ValidateIdentifier(permission, MaximumPermissionLength, "Permission");
        var document = ParseDocument(text);
        var root = document.Root!;
        var container = root.Elements("privapp-permissions")
            .FirstOrDefault(element => (string?)element.Attribute("package") == packageName);
        if (container is not null)
        {
            if (container.Elements("permission").Any(element => (string?)element.Attribute("name") == permission))
            {
                return new TextPatchResult(text, 1, false);
            }
            container.Add(new XElement("permission", new XAttribute("name", permission)));
            return Serialize(text, document, 1);
        }
        if (Enumerate(document).Any(entry => entry.Package == packageName && entry.Permission == permission))
        {
            return new TextPatchResult(text, 1, false);
        }
        var flatShape = root.Elements("permission").Any(element => element.Attribute("package") is not null)
            ? PrivAppPermissionShape.FlatPermission
            : root.Elements("allow").Any(element => element.Attribute("package") is not null)
                ? PrivAppPermissionShape.FlatAllow
                : PrivAppPermissionShape.ContainerChild;
        if (flatShape == PrivAppPermissionShape.ContainerChild)
        {
            root.Add(new XElement("privapp-permissions",
                new XAttribute("package", packageName),
                new XElement("permission", new XAttribute("name", permission))));
        }
        else
        {
            root.Add(new XElement(flatShape == PrivAppPermissionShape.FlatPermission ? "permission" : "allow",
                new XAttribute("name", permission),
                new XAttribute("package", packageName)));
        }
        return Serialize(text, document, 1);
    }

    /// <summary>Removes every allow entry matching the package and permission pair in any supported shape.
    /// <para>移除所有匹配该包名与权限对的授权条目，支持任意形态。</para></summary>
    /// <param name="text">XML document text, bounded by <see cref="TextPatch.MaxCharacters"/>.
    /// <para>XML 文档文本，大小受 <see cref="TextPatch.MaxCharacters"/> 限制。</para></param>
    /// <param name="packageName">Granting application package name; non-blank, at most 200 characters, no control characters.
    /// <para>授权目标应用包名；非空白、至多 200 个字符且无控制字符。</para></param>
    /// <param name="permission">Permission identifier; non-blank, at most 256 characters, no control characters.
    /// <para>权限标识；非空白、至多 256 个字符且无控制字符。</para></param>
    /// <returns>The patched document with the number of removed nodes, or the untouched input when nothing matched.
    /// <para>补丁后的文档及移除节点数；无匹配时原样返回输入。</para></returns>
    /// <exception cref="InvalidDataException">The document is malformed or an identifier is invalid.
    /// <para>文档畸形，或标识不合法。</para></exception>
    public static TextPatchResult Remove(string text, string packageName, string permission)
    {
        ValidateIdentifier(packageName, MaximumPackageNameLength, "Package name");
        ValidateIdentifier(permission, MaximumPermissionLength, "Permission");
        var document = ParseDocument(text);
        var root = document.Root!;
        var removed = 0;
        foreach (var element in root.Elements("privapp-permissions")
                     .Where(element => (string?)element.Attribute("package") == packageName)
                     .ToArray())
        {
            foreach (var child in element.Elements("permission")
                         .Where(child => (string?)child.Attribute("name") == permission)
                         .ToArray())
            {
                child.Remove();
                removed++;
            }
            if (!element.Elements("permission").Any())
            {
                element.Remove();
            }
        }
        foreach (var element in root.Elements()
                     .Where(element => element.Name.LocalName is "permission" or "allow"
                         && (string?)element.Attribute("package") == packageName
                         && (string?)element.Attribute("name") == permission)
                     .ToArray())
        {
            element.Remove();
            removed++;
        }
        if (removed == 0)
        {
            return new TextPatchResult(text, 0, false);
        }
        return Serialize(text, document, removed);
    }

    private static IEnumerable<PrivAppPermissionEntry> Enumerate(XDocument document)
    {
        var root = document.Root ?? throw new PatchingCorruptedException("The privapp-permissions document has no root element.");
        if (root.Name.LocalName != "permissions")
        {
            throw new PatchingCorruptedException("The privapp-permissions root element must be <permissions>.");
        }
        foreach (var element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "privapp-permissions":
                    RequireAttribute(element, "package", MaximumPackageNameLength, "package");
                    foreach (var child in element.Elements())
                    {
                        if (child.Name.LocalName == "permission")
                        {
                            yield return new PrivAppPermissionEntry(
                                RequireAttribute(element, "package", MaximumPackageNameLength, "package"),
                                RequireAttribute(child, "name", MaximumPermissionLength, "name"),
                                PrivAppPermissionShape.ContainerChild);
                        }
                    }
                    break;
                case "permission" or "allow" when element.Attribute("package") is not null:
                    yield return new PrivAppPermissionEntry(
                        RequireAttribute(element, "package", MaximumPackageNameLength, "package"),
                        RequireAttribute(element, "name", MaximumPermissionLength, "name"),
                        element.Name.LocalName == "permission"
                            ? PrivAppPermissionShape.FlatPermission
                            : PrivAppPermissionShape.FlatAllow);
                    break;
                default:
                    break;
            }
        }
    }

    private static XDocument ParseDocument(string text)
    {
        TextPatch.ValidateSize(text);
        try
        {
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
        catch (XmlException exception)
        {
            throw new PatchingCorruptedException($"The privapp-permissions document is not well-formed XML: {exception.Message}");
        }
    }

    private static string RequireAttribute(XElement element, string name, int maximum, string label)
    {
        var value = (string?)element.Attribute(name)
            ?? throw new PatchingCorruptedException($"A <{element.Name.LocalName}> entry is missing the '{name}' attribute.");
        ValidateIdentifier(value, maximum, label);
        return value;
    }

    private static void ValidateIdentifier(string value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new PatchingCorruptedException($"{label} must be 1 to {maximum} characters without control characters.");
        }
    }

    private static TextPatchResult Serialize(string original, XDocument document, int matches)
    {
        var body = document.ToString(SaveOptions.DisableFormatting);
        if (original.Contains("\r\n", StringComparison.Ordinal))
        {
            body = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }
        if (original.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var end = original.IndexOf("?>", StringComparison.Ordinal);
            if (end < 0)
            {
                throw new PatchingCorruptedException("The XML declaration is not terminated.");
            }
            body = original[..(end + 2)] + body;
        }
        TextPatch.ValidateSize(body);
        _ = ParseDocument(body);
        return new TextPatchResult(body, matches, true);
    }
}