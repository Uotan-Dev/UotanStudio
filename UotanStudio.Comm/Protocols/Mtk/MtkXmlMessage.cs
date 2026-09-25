using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A bounded host-directed control message; no target paths are opened or resolved.</summary>
internal sealed record MtkXmlMessage(string Command, string? Result, IReadOnlyDictionary<string, string> Arguments)
{
    internal static MtkXmlMessage Parse(string text)
    {
        if (text.Length is 0 or > MtkXmlChannel.MaximumPayload)
        {
            throw new InvalidDataException("The XML DA message size is invalid.");
        }
        using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MtkXmlChannel.MaximumPayload,
            IgnoreWhitespace = true
        }))
        {
            var nodes = 0;
            while (reader.Read())
            {
                if (++nodes > 256 || reader.Depth > 3 || (reader.NodeType == XmlNodeType.Element && reader.AttributeCount != 0)
                    || reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
                {
                    throw new InvalidDataException("The XML DA message structure exceeds its supported limits.");
                }
            }
        }
        var root = XDocument.Parse(text).Root;
        if (root?.Name != "host" || root.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
        {
            throw new InvalidDataException("Expected a host-directed XML DA message.");
        }
        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var field in root.Elements())
        {
            if (field.Name.NamespaceName.Length != 0
                || field.Name.LocalName is not ("version" or "command" or "result" or "arg")
                || !fields.TryAdd(field.Name.LocalName, field))
            {
                throw new InvalidDataException("Unsupported or duplicate XML DA field.");
            }
        }
        if (!fields.TryGetValue("version", out var version) || version.Value is not ("1.0" or "1.1") || version.HasElements
            || !fields.TryGetValue("command", out var command) || command.HasElements
            || command.Value.Length is 0 or > 128 || !command.Value.StartsWith("CMD:", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The XML DA version or command is invalid.");
        }
        fields.TryGetValue("result", out var result);
        if (result is not null && (result.HasElements || result.Value.Length > 4096))
        {
            throw new InvalidDataException("The XML DA result is invalid.");
        }
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fields.TryGetValue("arg", out var arg))
        {
            if (arg.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
            {
                throw new InvalidDataException("XML DA arguments must be named fields.");
            }
            foreach (var value in arg.Elements())
            {
                if (arguments.Count == 32 || value.Name.NamespaceName.Length != 0 || value.HasElements
                    || value.Name.LocalName.Length > 128 || value.Value.Length > 4096
                    || !arguments.TryAdd(value.Name.LocalName, value.Value))
                {
                    throw new InvalidDataException("Unsupported or duplicate XML DA argument.");
                }
            }
        }
        return new MtkXmlMessage(command.Value, result?.Value, arguments);
    }
}