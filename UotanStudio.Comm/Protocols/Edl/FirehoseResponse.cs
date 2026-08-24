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
    public Dictionary<string, string> Attributes { get; set; } = [];

    /// <summary>Gets an attribute by key. 按键获取属性。</summary>
    public string? this[string key] => Attributes.TryGetValue(key, out var value) ? value : null;

    /// <summary>Gets an attribute as Int64, or null. 获取 Int64 属性，无则 null。</summary>
    public long? GetInt64(string key)
    {
        var val = this[key];
        return val == null ? null : long.TryParse(val, out var result) ? result : null;
    }

    /// <summary>Gets an attribute as Int32, or null. 获取 Int32 属性，无则 null。</summary>
    public int? GetInt32(string key)
    {
        var val = this[key];
        return val == null ? null : int.TryParse(val, out var result) ? result : null;
    }

    /// <summary>Returns a string representation of the response. 返回响应的字符串表示。</summary>
    public override string ToString() =>
        IsRawMode ? "FirehoseResponse(RAW_MODE)" :
        IsLog ? $"FirehoseResponse(LOG, Value={LogValue})" :
        $"FirehoseResponse({(IsAck ? "ACK" : "NAK")}, Attributes={Attributes.Count})";
}

/// <summary>
/// Parses Firehose XML responses from the target device.
/// <para>解析来自目标设备的 Firehose XML 响应。</para>
/// </summary>
public static class FirehoseResponseParser
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
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var dataNode = doc.SelectSingleNode("data") ?? throw new FirehoseException("No <data> element in response.");
            var responseNode = dataNode.SelectSingleNode("response");
            if (responseNode != null)
            {
                response = ParseResponseNode(responseNode, xml);
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

    /// <summary>
    /// Parses a complete <c>&lt;data&gt;</c> block, extracting <c>&lt;log&gt;</c> nodes and the
    /// <c>&lt;response&gt;</c> node. Some targets send log and response within the same block;
    /// node-level parsing prevents a block containing a response from being discarded
    /// wholesale as a log.
    /// <para>解析一个完整的 <c>&lt;data&gt;</c> 块，逐节点提取 <c>&lt;log&gt;</c> 与
    /// <c>&lt;response&gt;</c>。某些设备会在同一块内同时发送 log 与 response；节点级解析
    /// 可避免把含 response 的块整体当作 log 丢弃。</para>
    /// </summary>
    /// <param name="xml">The complete <c>&lt;data&gt;</c> XML block. 完整的 <c>&lt;data&gt;</c> XML 块。</param>
    /// <returns>A <see cref="FirehoseDataBlock"/> with any logs and/or the response found. 包含所提取 log 及/或 response 的 <see cref="FirehoseDataBlock"/>。</returns>
    public static FirehoseDataBlock ParseDataBlock(string xml)
    {
        var block = new FirehoseDataBlock();

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var dataNode = doc.SelectSingleNode("data");
            if (dataNode == null)
            {
                return block;
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
                    block.Response = ParseResponseNode(node, xml);
                }
            }
        }
        catch (XmlException)
        {
            // Return an empty block on malformed XML; the caller falls back to existing handling.
            // 非完整/畸形 XML 时返回空块，由调用方按既有路径处理。
        }

        return block;
    }

    /// <summary>
    /// Parses a <c>&lt;response&gt;</c> element node into a <see cref="FirehoseResponse"/>.
    /// <para>将 <c>&lt;response&gt;</c> 元素节点解析为 <see cref="FirehoseResponse"/>。</para>
    /// </summary>
    /// <param name="responseNode">The response element node. response 元素节点。</param>
    /// <param name="xml">The containing XML text (stored as RawXml). 所在 XML 文本（存为 RawXml）。</param>
    /// <returns>The parsed response. 解析后的响应。</returns>
    private static FirehoseResponse ParseResponseNode(XmlNode responseNode, string xml)
    {
        var response = new FirehoseResponse { RawXml = xml };

        var valueAttr = responseNode.Attributes?["value"];
        var rawModeAttr = responseNode.Attributes?["rawmode"];

        response.RawValue = valueAttr?.Value;
        response.IsAck = string.Equals(valueAttr?.Value, "ACK", StringComparison.OrdinalIgnoreCase);
        response.IsNak = string.Equals(valueAttr?.Value, "NAK", StringComparison.OrdinalIgnoreCase);
        response.IsRawMode = string.Equals(rawModeAttr?.Value, "true", StringComparison.OrdinalIgnoreCase);

        if (responseNode.Attributes != null)
        {
            foreach (XmlAttribute attr in responseNode.Attributes)
            {
                response.Attributes[attr.Name] = attr.Value;
            }
        }

        return response;
    }
}

/// <summary>
/// Result of parsing a complete <c>&lt;data&gt;</c> block: zero or more log nodes and an
/// optional response node.
/// <para>一个完整 <c>&lt;data&gt;</c> 块的解析结果：零个或多个 log 节点，以及可选的
/// response 节点。</para>
/// </summary>
public sealed class FirehoseDataBlock
{
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
/// <param name="value">The log Message. 日志消息。</param>
public sealed class FirehoseLogEventArgs(string value) : EventArgs
{

    /// <summary>
    /// Gets the log Message.
    /// <para>获取日志消息。</para>
    /// </summary>
    public string Value { get; } = value;
}