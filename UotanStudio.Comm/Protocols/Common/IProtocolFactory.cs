namespace UotanStudio.Comm.Protocols.Common;

/// <summary>
/// Creates protocol sessions from transport sessions.
/// <para>从传输会话创建协议会话。</para>
/// Implementations match a transport session against the protocol's expected device identity
/// (VID/PID, interface class, etc.) and produce a ready-to-use protocol session.
/// <para>实现类将传输会话与协议预期的设备标识（VID/PID、接口类等）进行匹配，
/// 生成可直接使用的协议会话。</para>
/// </summary>
public interface IProtocolFactory
{
    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Tries to create a protocol session over the specified transport session.
    /// <para>尝试在指定传输会话上创建协议会话。</para>
    /// </summary>
    /// <param name="transport">The underlying transport session. <para>底层传输会话。</para></param>
    /// <param name="session">The created protocol session, or <c>null</c> when the transport is not compatible. <para>创建的协议会话；传输不兼容时返回 <c>null</c>。</para></param>
    /// <returns><c>true</c> when the protocol session was created. <para>协议会话创建成功返回 <c>true</c>。</para></returns>
    bool TryCreate(UotanStudio.Comm.Transports.Usb.Abstractions.IUsbDeviceSession transport, out IProtocolSession? session);
}
