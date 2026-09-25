using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A bounded, key-matched property reported by an XML agent, not authenticated device evidence.
/// <para>由 XML agent 报告的、经过长度限制且按键匹配的属性，并非经过认证的设备证据。</para></summary>
public sealed class MtkXmlSystemProperty
{
    internal const int MaximumDocumentLength = 64 * 1024;

    private MtkXmlSystemProperty(string envelopeName, string? version, string key, string value)
    {
        EnvelopeName = envelopeName;
        Version = version;
        Key = key;
        Value = value;
    }

    /// <summary>Gets the outer element name exactly as reported; generic queries do not infer a vendor schema.
    /// <para>获取与上报内容完全一致的外层元素名称；通用查询不会推断厂商架构。</para></summary>
    /// <value>The root element's local name, with no namespace.
    /// <para>根元素的本地名称，不含命名空间。</para></value>
    public string EnvelopeName { get; }
    /// <summary>Gets the optional opaque document version, without interpreting it as protocol authorization.
    /// <para>获取可选的不透明文档版本，不会将其解释为协议授权。</para></summary>
    /// <value>The version text when a scalar <c>version</c> field of length 1 to 32 was present,
    /// otherwise <see langword="null"/>.
    /// <para>当存在长度为 1 至 32 的标量 <c>version</c> 字段时为该版本文本，否则为 <see langword="null"/>。</para></value>
    public string? Version { get; }
    /// <summary>Gets the exact property key matched to the request.
    /// <para>获取与请求精确匹配的属性键。</para></summary>
    /// <value>The caller-supplied key that the <c>item</c> element's <c>key</c> attribute equals.
    /// <para>调用方提供的键，<c>item</c> 元素的 <c>key</c> 属性与之相等。</para></value>
    public string Key { get; }
    /// <summary>Gets the scalar property value without trimming, case folding or boolean coercion.
    /// <para>获取标量属性值，不进行裁剪、大小写折叠或布尔强制转换。</para></summary>
    /// <value>The <c>item</c> element text, of length 1 to 4096, preserved verbatim.
    /// <para><c>item</c> 元素的文本，长度 1 至 4096，原样保留。</para></value>
    public string Value { get; }

    internal static MtkXmlSystemProperty Parse(string text, string key, string? expectedEnvelope)
    {
        if (text.Length is 0 or > MaximumDocumentLength)
        {
            throw new InvalidDataException("The XML property document length is invalid.");
        }
        using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentLength,
            IgnoreWhitespace = true
        }))
        {
            var nodes = 0;
            while (reader.Read())
            {
                if (++nodes > 64 || reader.Depth > 2
                    || reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
                {
                    throw new InvalidDataException("Unsupported XML property document structure.");
                }
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var item = reader.Depth == 1 && reader.LocalName == "item";
                    if (reader.NamespaceURI.Length != 0
                        || (item ? reader.AttributeCount != 1 || reader.GetAttribute("key") is null : reader.AttributeCount != 0))
                    {
                        throw new InvalidDataException("Unsupported XML property namespace or attributes.");
                    }
                }
            }
        }
        var root = XDocument.Parse(text).Root;
        if (root is null || root.Name.LocalName.Length > 128 || root.Name.LocalName == "item"
            || (expectedEnvelope is not null && root.Name.LocalName != expectedEnvelope)
            || root.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
        {
            throw new InvalidDataException("The XML property envelope does not match the selected profile.");
        }
        XElement? itemElement = null;
        string? version = null;
        foreach (var field in root.Elements())
        {
            if (field.HasElements)
            {
                throw new InvalidDataException("XML property fields must be scalar.");
            }
            if (field.Name == "item" && itemElement is null)
            {
                itemElement = field;
            }
            else if (field.Name == "version" && version is null && field.Value.Length is > 0 and <= 32)
            {
                version = field.Value;
            }
            else
            {
                throw new InvalidDataException("Duplicate or unsupported XML property fields.");
            }
        }
        if (itemElement is null || itemElement.Attribute("key")?.Value != key
            || itemElement.Value.Length is 0 or > 4096)
        {
            throw new InvalidDataException("The XML property key or scalar value is missing, mismatched or oversized.");
        }
        return new MtkXmlSystemProperty(root.Name.LocalName, version, key, itemElement.Value);
    }
}

/// <summary>Exact textual SLA indications; Unknown never grants authorization.
/// <para>精确的文本 SLA 指示；Unknown 从不授予授权。</para></summary>
public enum MtkXmlSlaIndication
{
    /// <summary>The returned text is not one of the recognized indications.
    /// <para>返回文本不属于任何已识别的指示。</para></summary>
    Unknown,
    /// <summary>The agent reported exactly ENABLED; this is not an independently verified security state.
    /// <para>agent 报告的文本恰为 ENABLED；这不是经过独立验证的安全状态。</para></summary>
    EnabledReported,
    /// <summary>The agent reported exactly DISABLED; this does not authorize storage access.
    /// <para>agent 报告的文本恰为 DISABLED；这并不授权存储访问。</para></summary>
    DisabledReported
}

/// <summary>An immutable, non-authoritative SLA report from a completed property query.
/// <para>来自已完成属性查询的不可变、非权威 SLA 报告。</para></summary>
public sealed class MtkXmlSlaReport
{
    internal MtkXmlSlaReport(MtkXmlSystemProperty property)
    {
        Property = property;
        Indication = property.Value switch
        {
            "ENABLED" => MtkXmlSlaIndication.EnabledReported,
            "DISABLED" => MtkXmlSlaIndication.DisabledReported,
            _ => MtkXmlSlaIndication.Unknown
        };
    }

    /// <summary>Gets the original key-matched property and envelope metadata.
    /// <para>获取原始的按键匹配属性及信封元数据。</para></summary>
    /// <value>The parsed property whose <c>Value</c> text was classified.
    /// <para>其 <c>Value</c> 文本被分类的已解析属性。</para></value>
    public MtkXmlSystemProperty Property { get; }
    /// <summary>Gets the exact textual indication without fallback on unknown values or query errors.
    /// <para>获取精确的文本指示，对未知值或查询错误不作回退。</para></summary>
    /// <value>
    /// <see cref="MtkXmlSlaIndication.EnabledReported"/> or <see cref="MtkXmlSlaIndication.DisabledReported"/>
    /// only on an exact string match, otherwise <see cref="MtkXmlSlaIndication.Unknown"/>.
    /// <para>仅在字符串精确匹配时为 <see cref="MtkXmlSlaIndication.EnabledReported"/> 或
    /// <see cref="MtkXmlSlaIndication.DisabledReported"/>，否则为 <see cref="MtkXmlSlaIndication.Unknown"/>。</para></value>
    public MtkXmlSlaIndication Indication { get; }
}