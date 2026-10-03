using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Parses package-supplied XML under the limits the firmware layer treats as untrusted input: no document
/// type declaration, no entity expansion, a bounded character budget and no external resource resolver.
/// <para>按固件层视为不可信输入的限制解析包内 XML：不允许文档类型声明、不做实体展开、字符总量受限，也不解析外部资源。</para>
/// </summary>
internal static class UntrustedXmlDocument
{
    private const int MaximumCharacters = 4 * 1024 * 1024;
    private const int MaximumEntityCharacters = 1024;

    /// <summary>
    /// Builds the reader settings every package XML parse has to use.
    /// <para>构造每次解析包内 XML 都必须使用的读取器设置。</para>
    /// </summary>
    /// <returns>The prohibiting, bounded settings. <para>禁止 DTD 且带限额的设置。</para></returns>
    internal static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumCharacters,
        MaxCharactersFromEntities = MaximumEntityCharacters
    };

    /// <summary>
    /// Parses a package XML string, refusing a document type declaration before any entity is expanded.
    /// <para>解析包内 XML 字符串，在任何实体展开之前拒绝文档类型声明。</para>
    /// </summary>
    /// <param name="xml">The package-supplied XML text. <para>包内提供的 XML 文本。</para></param>
    /// <param name="preserveWhitespace">Whether whitespace formatting is kept.
    /// <para>是否保留空白格式。</para></param>
    /// <returns>The parsed document. <para>解析得到的文档。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is <see langword="null"/>.
    /// <para><paramref name="xml"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="XmlException">The text is malformed, carries a document type declaration, or
    /// exceeds the character budget. <para>文本格式非法、含文档类型声明，或超出字符上限。</para></exception>
    internal static XDocument Parse(string xml, bool preserveWhitespace = false)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(new StringReader(xml), CreateSettings());
        return XDocument.Load(reader, preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None);
    }
}