using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A bounded, key-matched property reported by an XML agent, not authenticated device evidence.</summary>
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

    /// <summary>Gets the outer element name exactly as reported; generic queries do not infer a vendor schema.</summary>
    public string EnvelopeName { get; }
    /// <summary>Gets the optional opaque document version, without interpreting it as protocol authorization.</summary>
    public string? Version { get; }
    /// <summary>Gets the exact property key matched to the request.</summary>
    public string Key { get; }
    /// <summary>Gets the scalar property value without trimming, case folding or boolean coercion.</summary>
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

/// <summary>Exact textual SLA indications; Unknown never grants authorization.</summary>
public enum MtkXmlSlaIndication
{
    /// <summary>The returned text is not one of the recognized indications.</summary>
    Unknown,
    /// <summary>The agent reported exactly ENABLED; this is not an independently verified security state.</summary>
    EnabledReported,
    /// <summary>The agent reported exactly DISABLED; this does not authorize storage access.</summary>
    DisabledReported
}

/// <summary>An immutable, non-authoritative SLA report from a completed property query.</summary>
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

    /// <summary>Gets the original key-matched property and envelope metadata.</summary>
    public MtkXmlSystemProperty Property { get; }
    /// <summary>Gets the exact textual indication without fallback on unknown values or query errors.</summary>
    public MtkXmlSlaIndication Indication { get; }
}
