namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Passively captured Sahara HELLO facts reported by the target before any handshake response.
/// <para>在任何握手响应之前，从目标被动捕获的 Sahara HELLO 信息。</para>
/// </summary>
/// <param name="ProtocolVersion">
/// The target's Sahara protocol version. <para>目标的 Sahara 协议版本。</para>
/// </param>
/// <param name="MinProtocolVersion">
/// The minimum Sahara protocol version the target accepts.
/// <para>目标可接受的最低 Sahara 协议版本。</para>
/// </param>
/// <param name="MaxCommandLength">
/// The target's maximum command length (<c>max_cmd_len</c>).
/// <para>目标的最大命令长度（<c>max_cmd_len</c>）。</para>
/// </param>
/// <param name="Mode">
/// The operating mode requested by the target's HELLO.
/// <para>目标 HELLO 请求的操作模式。</para>
/// </param>
public sealed record SaharaHandshakeInfo(
    uint ProtocolVersion,
    uint MinProtocolVersion,
    uint MaxCommandLength,
    SaharaMode Mode);