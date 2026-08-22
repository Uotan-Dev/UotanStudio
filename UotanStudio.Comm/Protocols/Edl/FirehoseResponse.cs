using System.Xml;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Represents a Firehose protocol response parsed from target XML.
/// <para>表示从目标 XML 解析的 Firehose 协议响应。</para>
/// </summary>
public class FirehoseResponse
{
    /// <summary>Gets or sets whether the response is an ACK. 是否为 ACK。</summary>
    public bool IsAck { get; set; }

    /// <summary>Gets or sets whether the response is a NAK. 是否为 NAK。</summary>
    public bool IsNak { get; set; }

    /// <summary>Gets or sets whether the response is a log block. 是否为日志块。</summary>
    public bool IsLog { get; set; }

    /// <summary>Gets or sets whether the response indicates raw mode entry. 是否指示原始模式进入。</summary>
    public bool IsRawMode { get; set; }

    /// <summary>Gets or sets the raw response value. 原始响应值。</summary>
    public string? RawValue { get; set; }

    /// <summary>Gets or sets the raw XML. 原始 XML。</summary>
    public string? RawXml { get; set; }

    /// <summary>Gets or sets the log value (if IsLog). 日志值。</summary>
    public string? LogValue { get; set; }

    /// <summary>Gets the response attributes dictionary. 响应属性字典。</summary>
    public Dictionary<string, string> Attributes { get; set; } = new();

    /// <summary>Gets an attribute by key. 按键获取属性。</summary>
    public string? this[string key] => Attributes.TryGetValue(key, out var value) ? value : null;

    /// <summary>Gets an attribute as Int64, or null. 获取 Int64 属性，无则 null。</summary>
    public long? GetInt64(string key)
    {
        var val = this[key];
        if (val == null) return null;
        return long.TryParse(val, out var result) ? result : null;
    }

    /// <summary>Gets an attribute as Int32, or null. 获取 Int32 属性，无则 null。</summary>
    public int? GetInt32(string key)
    {
        var val = this[key];
        if (val == null) return null;
        return int.TryParse(val, out var result) ? result : null;
    }

    /// <summary>Parses a Firehose XML response string. 解析 Firehose XML 响应字符串。</summary>
    public static FirehoseResponse Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new FirehoseException("Empty response XML.");

        var response = new FirehoseResponse { RawXml = xml };

        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);

            XmlNode? dataNode = doc.SelectSingleNode("data");
            if (dataNode == null)
                throw new FirehoseException("No <data> element in response.");

            XmlNode? responseNode = dataNode.SelectSingleNode("response");
            if (responseNode != null)
            {
                XmlAttribute? valueAttr = responseNode.Attributes?["value"];
                XmlAttribute? rawModeAttr = responseNode.Attributes?["rawmode"];

                response.RawValue = valueAttr?.Value;
                response.IsAck = string.Equals(valueAttr?.Value, "ACK", StringComparison.OrdinalIgnoreCase);
                response.IsNak = string.Equals(valueAttr?.Value, "NAK", StringComparison.OrdinalIgnoreCase);
                response.IsRawMode = string.Equals(rawModeAttr?.Value, "true", StringComparison.OrdinalIgnoreCase);

                if (responseNode.Attributes != null)
                {
                    foreach (System.Xml.XmlAttribute attr in responseNode.Attributes)
                    {
                        response.Attributes[attr.Name] = attr.Value;
                    }
                }
            }
            else
            {
                XmlNode? logNode = dataNode.SelectSingleNode("log");
                if (logNode != null)
                {
                    response.IsLog = true;
                    response.LogValue = logNode.Attributes?["value"]?.Value;
                    response.RawValue = response.LogValue;

                    if (logNode.Attributes != null)
                    {
                        foreach (System.Xml.XmlAttribute attr in logNode.Attributes)
                        {
                            response.Attributes[attr.Name] = attr.Value;
                        }
                    }
                }
            }
        }
        catch (System.Xml.XmlException ex)
        {
            throw new FirehoseException($"Failed to parse Firehose response XML: {ex.Message}", ex);
        }

        return response;
    }
}
