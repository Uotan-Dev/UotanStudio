using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>Reads bounded, flat XML manifests without resolving external resources.</summary>
internal static class FlashBundleXml
{
    private const int MaximumCharacters = 4 * 1024 * 1024;
    private const int MaximumEntries = 16384;
    private const int MaximumAttributes = 128;

    internal static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    internal static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
            CloseInput = false
        });
        var document = new XDocument();
        var entries = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth > 1 || reader.AttributeCount > MaximumAttributes
                        || (reader.Depth == 1 && ++entries > MaximumEntries))
                    {
                        throw new XmlException("Flash manifest structure exceeds the supported limits.");
                    }

                    var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            var name = reader.Name == "xmlns"
                                ? XName.Get("xmlns")
                                : XName.Get(reader.LocalName, reader.NamespaceURI);
                            element.Add(new XAttribute(name, reader.Value));
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (reader.Depth == 0)
                    {
                        document.Add(element);
                    }
                    else
                    {
                        document.Root!.Add(element);
                    }

                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (!string.IsNullOrWhiteSpace(reader.Value))
                    {
                        throw new XmlException("Flash manifest commands must contain attributes only.");
                    }

                    break;
            }
        }

        return document;
    }
}