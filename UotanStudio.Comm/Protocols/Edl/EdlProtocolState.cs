namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Identifies the wire protocol observed on an EDL endpoint.
/// <para>标识在 EDL 端点上实际观测到的通信协议。</para>
/// </summary>
public enum EdlProtocolState
{
    /// <summary>No complete protocol response has been validated.<para>尚未校验到完整的协议响应。</para></summary>
    Unknown,
    /// <summary>A complete Sahara packet has been validated.<para>已校验到完整的 Sahara 数据包。</para></summary>
    Sahara,
    /// <summary>A Firehose response or completed loader transition has been observed.<para>已观测到 Firehose 响应或已完成的加载器切换。</para></summary>
    Firehose
}