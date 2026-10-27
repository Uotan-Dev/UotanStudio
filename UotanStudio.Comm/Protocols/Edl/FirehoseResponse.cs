using System.Xml;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Represents a Firehose protocol response parsed from target XML.
/// <para>表示从目标 XML 解析的 Firehose 协议响应。</para>
/// </summary>
public class FirehoseResponse
{
    /// <summary>
    /// Gets validated geometry from the logs associated with a getstorageinfo response, if reported.
    /// <para>获取与 getstorageinfo 响应关联的日志中经校验的几何信息（若有报告）。</para>
    /// </summary>
    /// <value>The geometry, or <see langword="null"/> when no getstorageinfo geometry was parsed.
    /// <para>几何信息；未解析到 getstorageinfo 几何信息时为 <see langword="null"/>。</para></value>
    public FirehoseStorageGeometry? StorageGeometry { get; internal set; }

    /// <summary>
    /// Gets or sets whether the response is an ACK.
    /// <para>获取或设置响应是否为 ACK。</para>
    /// </summary>
    /// <value><see langword="true"/> when the response value is <c>ACK</c> or <c>true</c>. <para>响应值为 <c>ACK</c> 或 <c>true</c> 时为 <see langword="true"/>。</para></value>
    public bool IsAck { get; set; }

    /// <summary>
    /// Gets or sets whether the response is a NAK.
    /// <para>获取或设置响应是否为 NAK。</para>
    /// </summary>
    /// <value><see langword="true"/> when the response value is <c>NAK</c> or <c>false</c>. <para>响应值为 <c>NAK</c> 或 <c>false</c> 时为 <see langword="true"/>。</para></value>
    public bool IsNak { get; set; }

    /// <summary>
    /// Gets or sets whether the response is a log block.
    /// <para>获取或设置响应是否为日志块。</para>
    /// </summary>
    /// <value><see langword="true"/> when the parsed block carried only a log node. <para>所解析的块仅包含 log 节点时为 <see langword="true"/>。</para></value>
    public bool IsLog { get; set; }

    /// <summary>
    /// Gets or sets whether the response indicates raw mode entry.
    /// <para>获取或设置响应是否指示进入原始模式。</para>
    /// </summary>
    /// <value><see langword="true"/> when the response carries <c>rawmode="true"</c>. <para>响应带有 <c>rawmode="true"</c> 时为 <see langword="true"/>。</para></value>
    public bool IsRawMode { get; set; }

    /// <summary>
    /// Gets or sets the raw response value.
    /// <para>获取或设置原始响应值。</para>
    /// </summary>
    /// <value>The <c>value</c> attribute of the response or log node, or <see langword="null"/>.
    /// <para>response 或 log 节点的 <c>value</c> 属性；无则为 <see langword="null"/>。</para></value>
    public string? RawValue { get; set; }

    /// <summary>
    /// Gets the classification of a rejected response.
    /// <para>获取被拒绝响应的分类结果。</para>
    /// </summary>
    /// <value>The evidence-backed error description, or <see langword="null"/> when the response is
    /// an acknowledgement, a raw-mode indication, or a log-only block.
    /// <para>基于证据的错误描述；当响应为确认、原始模式指示或仅含日志的块时为
    /// <see langword="null"/>。</para></value>
    public FirehoseResponseError? Error { get; internal set; }

    /// <summary>
    /// Gets or sets the raw XML.
    /// <para>获取或设置原始 XML。</para>
    /// </summary>
    /// <value>The containing XML text the response was parsed from. <para>解析该响应时所在的 XML 文本。</para></value>
    public string? RawXml { get; set; }

    /// <summary>
    /// Gets or sets the log value (if IsLog).
    /// <para>获取或设置日志值（当 IsLog 时）。</para>
    /// </summary>
    /// <value>The log node's <c>value</c> attribute, or <see langword="null"/> when this is not a log.
    /// <para>log 节点的 <c>value</c> 属性；非日志响应时为 <see langword="null"/>。</para></value>
    public string? LogValue { get; set; }

    /// <summary>
    /// Gets the response attributes dictionary.
    /// <para>获取响应属性字典。</para>
    /// </summary>
    /// <value>Every XML attribute of the parsed node keyed by name; never <see langword="null"/>.
    /// <para>以名称为键的所解析节点的全部 XML 属性；不会为 <see langword="null"/>。</para></value>
    public Dictionary<string, string> Attributes { get; set; } = [];

    /// <summary>
    /// Gets an attribute by key.
    /// <para>按键获取属性。</para>
    /// </summary>
    /// <param name="key">
    /// The attribute name to look up. <para>要查找的属性名。</para>
    /// </param>
    /// <value>The attribute value, or <see langword="null"/> when the key is absent.
    /// <para>属性值；键不存在时为 <see langword="null"/>。</para></value>
    public string? this[string key] => Attributes.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Gets an attribute as Int64, or null.
    /// <para>获取 Int64 属性；无或无法解析时为 null。</para>
    /// </summary>
    /// <param name="key">
    /// The attribute name to read. <para>要读取的属性名。</para>
    /// </param>
    /// <returns>The parsed value, or <see langword="null"/> when absent or not a valid Int64.
    /// <para>解析后的值；属性缺失或不是有效 Int64 时为 <see langword="null"/>。</para></returns>
    public long? GetInt64(string key)
    {
        var val = this[key];
        return val == null ? null : long.TryParse(val, out var result) ? result : null;
    }

    /// <summary>
    /// Gets an attribute as Int32, or null.
    /// <para>获取 Int32 属性；无或无法解析时为 null。</para>
    /// </summary>
    /// <param name="key">
    /// The attribute name to read. <para>要读取的属性名。</para>
    /// </param>
    /// <returns>The parsed value, or <see langword="null"/> when absent or not a valid Int32.
    /// <para>解析后的值；属性缺失或不是有效 Int32 时为 <see langword="null"/>。</para></returns>
    public int? GetInt32(string key)
    {
        var val = this[key];
        return val == null ? null : int.TryParse(val, out var result) ? result : null;
    }

    /// <summary>
    /// Returns a string representation of the response.
    /// <para>返回响应的字符串表示。</para>
    /// </summary>
    public override string ToString() =>
        IsRawMode ? "FirehoseResponse(RAW_MODE)" :
        IsLog ? $"FirehoseResponse(LOG, Value={LogValue})" :
        $"FirehoseResponse({(IsAck ? "ACK" : "NAK")}, Attributes={Attributes.Count})";
}

/// <summary>
/// Parses Firehose XML responses from the target device.
/// <para>解析来自目标设备的 Firehose XML 响应。</para>
/// </summary>
internal static class FirehoseResponseParser
{
    /// <summary>
    /// Parses a complete <c>&lt;data&gt;</c> XML block into a <see cref="FirehoseResponse"/>.
    /// <para>将完整的 <c>&lt;data&gt;</c> XML 块解析为 <see cref="FirehoseResponse"/>。</para>
    /// </summary>
    /// <param name="xml">The complete XML block. 完整的 XML 块。</param>
    /// <returns>The parsed response. 解析后的响应。</returns>
    /// <exception cref="FirehoseException">Thrown when the XML is empty or malformed. XML 为空或畸形时抛出。</exception>
    public static FirehoseResponse Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new FirehoseException("Empty response XML.");
        }

        var response = new FirehoseResponse { RawXml = xml };

        try
        {
            var doc = LoadDocument(xml);

            var dataNode = doc.SelectSingleNode("data") ?? throw new FirehoseException("No <data> element in response.");
            var responseNode = dataNode.SelectSingleNode("response");
            if (responseNode != null)
            {
                response = ParseResponseNode(responseNode, xml, null);
            }
            else
            {
                var logNode = dataNode.SelectSingleNode("log");
                if (logNode != null)
                {
                    var valueAttr = logNode.Attributes?["value"];
                    response = new FirehoseResponse
                    {
                        RawXml = xml,
                        RawValue = valueAttr?.Value,
                        LogValue = valueAttr?.Value,
                        IsAck = false,
                        IsLog = true
                    };

                    if (logNode.Attributes != null)
                    {
                        foreach (XmlAttribute attr in logNode.Attributes)
                        {
                            response.Attributes[attr.Name] = attr.Value;
                        }
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FirehoseException($"Failed to parse Firehose response XML: {ex.Message}", ex);
        }

        return response;
    }

    /// <summary>Parses a complete <c>&lt;data&gt;</c> block, extracting <c>&lt;log&gt;</c> nodes and the <c>&lt;response&gt;</c> node.
    /// <para>解析一个完整的 <c>&lt;data&gt;</c> 块，逐节点提取 <c>&lt;log&gt;</c> 与 <c>&lt;response&gt;</c>。</para></summary>
    /// <remarks>
    /// Some targets send log and response within the same block; node-level parsing prevents a block containing a response from being discarded wholesale as a log.
    /// <para>某些设备会在同一块内同时发送 log 与 response；节点级解析可避免把含 response 的块整体当作 log 丢弃。</para>
    /// </remarks>
    /// <param name="xml">The complete <c>&lt;data&gt;</c>XML block. 完整的 <c>&lt;data&gt;</c>XML 块。</param>
    /// <param name="capturePayloads">Whether to retain and validate scalar vendor payload attributes.</param>
    /// <returns>A <see cref="FirehoseDataBlock"/>with any logs and/or the response found. 包含所提取 log 及/或
    /// response 的 <see cref="FirehoseDataBlock"/>。</returns>
    public static FirehoseDataBlock ParseDataBlock(string xml, bool capturePayloads = false)
    {
        var block = new FirehoseDataBlock();

        try
        {
            var doc = LoadDocument(xml);

            var dataNode = doc.SelectSingleNode("data");
            if (dataNode == null)
            {
                throw new FirehoseException("No <data> element in response.");
            }

            foreach (XmlNode node in dataNode.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (string.Equals(node.Name, "log", StringComparison.OrdinalIgnoreCase))
                {
                    var logValue = node.Attributes?["value"]?.Value;
                    if (logValue != null)
                    {
                        block.Logs.Add(logValue);
                    }
                }
                else if (string.Equals(node.Name, "response", StringComparison.OrdinalIgnoreCase))
                {
                    if (block.Response is not null)
                    {
                        throw new FirehoseException("Multiple responses in one Firehose document are ambiguous.");
                    }

                    block.Response = ParseResponseNode(node, xml, block.Logs.Count > 0 ? block.Logs[^1] : null);
                }
                else if (capturePayloads)
                {
                    if (node.NamespaceURI.Length != 0 || node.HasChildNodes || node.Attributes is null
                        || node.Attributes.Count > 32 || block.PayloadElements.Count >= 32)
                    {
                        throw new FirehoseException("Invalid vendor authentication payload element.");
                    }
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (XmlAttribute attribute in node.Attributes)
                    {
                        if (attribute.NamespaceURI.Length != 0 || attribute.Value.Length > 16384)
                        {
                            throw new FirehoseException("Invalid vendor authentication payload attribute.");
                        }
                        attributes.Add(attribute.Name, attribute.Value);
                    }
                    if (!block.PayloadElements.TryAdd(node.Name, attributes))
                    {
                        throw new FirehoseException("Duplicate vendor authentication payload element.");
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FirehoseException("Malformed Firehose response XML.", ex);
        }

        return block;
    }

    private static XmlDocument LoadDocument(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024
        });
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        return document;
    }

    /// <summary>
    /// Parses a <c>&lt;response&gt;</c> element node into a <see cref="FirehoseResponse"/> and
    /// attaches the evidence-backed classification of a rejection.
    /// <para>将 <c>&lt;response&gt;</c> 元素节点解析为 <see cref="FirehoseResponse"/>，并为被拒绝的
    /// 响应附加基于证据的分类。</para>
    /// </summary>
    /// <param name="responseNode">The response element node. response 元素节点。</param>
    /// <param name="xml">The containing XML text (stored as RawXml). 所在 XML 文本（存为 RawXml）。</param>
    /// <param name="lastLogValue">The value text of the last log node preceding this response within
    /// the same document, or <see langword="null"/> when there is none.
    /// 同一文档中位于该响应之前的最后一条 log 节点的文本；没有时为 <see langword="null"/>。</param>
    /// <returns>The parsed response. 解析后的响应。</returns>
    private static FirehoseResponse ParseResponseNode(XmlNode responseNode, string xml, string? lastLogValue)
    {
        var response = new FirehoseResponse { RawXml = xml };

        var valueAttr = responseNode.Attributes?["value"];
        var rawModeAttr = responseNode.Attributes?["rawmode"];

        response.RawValue = valueAttr?.Value;
        response.IsAck = string.Equals(valueAttr?.Value, "ACK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(valueAttr?.Value, "true", StringComparison.OrdinalIgnoreCase);
        response.IsNak = string.Equals(valueAttr?.Value, "NAK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(valueAttr?.Value, "false", StringComparison.OrdinalIgnoreCase);
        response.IsRawMode = string.Equals(rawModeAttr?.Value, "true", StringComparison.OrdinalIgnoreCase);

        if (responseNode.Attributes != null)
        {
            foreach (XmlAttribute attr in responseNode.Attributes)
            {
                response.Attributes[attr.Name] = attr.Value;
            }
        }

        response.Error = FirehoseErrorClassifier.Classify(response, lastLogValue);
        return response;
    }
}

/// <summary>
/// Result of parsing a complete <c>&lt;data&gt;</c> block: zero or more log nodes and an
/// optional response node.
/// <para>一个完整 <c>&lt;data&gt;</c> 块的解析结果：零个或多个 log 节点，以及可选的
/// response 节点。</para>
/// </summary>
internal sealed class FirehoseDataBlock
{
    /// <summary>Gets explicitly requested bounded vendor payload attributes. <para>获取显式请求的、有界的厂商 payload 属性。</para></summary>
    public Dictionary<string, Dictionary<string, string>> PayloadElements { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the log messages extracted from this block.
    /// <para>从该块中提取的 log 消息列表。</para>
    /// </summary>
    public List<string> Logs { get; } = [];

    /// <summary>
    /// Gets or sets the response node in this block, if present.
    /// <para>该块中的 response 节点（若存在）。</para>
    /// </summary>
    public FirehoseResponse? Response { get; set; }
}

/// <summary>
/// Event arguments carrying a log Message emitted by the target device.
/// <para>携带目标设备发出的日志消息的事件参数。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FirehoseLogEventArgs"/> class.
/// <para>初始化 <see cref="FirehoseLogEventArgs"/> 类的新实例。</para>
/// </remarks>
/// <param name="value">
/// The log text emitted by the target inside a Firehose log node.
/// <para>目标设备在 Firehose log 节点内输出的日志文本。</para>
/// </param>
public sealed class FirehoseLogEventArgs(string value) : EventArgs
{

    /// <summary>
    /// Gets the log Message.
    /// <para>获取日志消息。</para>
    /// </summary>
    /// <value>The <c>value</c> text emitted by the target inside a Firehose log node.
    /// <para>目标设备在 Firehose log 节点内输出的 <c>value</c> 文本。</para></value>
    public string Value { get; } = value;
}