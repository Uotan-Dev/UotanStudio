namespace UotanStudio.Comm.Protocols.Common;

/// <summary>
/// Represents a logical protocol session over a device transport session.
/// <para>表示基于设备传输会话的逻辑协议会话。</para>
/// A protocol session wraps an <see cref="UotanStudio.Comm.Transports.Usb.Abstractions.IUsbDeviceSession"/>
/// (or other transport) and implements a specific device protocol (fastboot, EDL, bootrom, etc.).
/// <para>协议会话包装 <see cref="UotanStudio.Comm.Transports.Usb.Abstractions.IUsbDeviceSession"/>
/// （或其他传输），实现特定设备协议（fastboot、EDL、bootrom 等）。</para>
/// </summary>
public interface IProtocolSession : IDisposable
{
    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    UotanStudio.Comm.Transports.Usb.Abstractions.IUsbDeviceSession Transport { get; }

    /// <summary>
    /// Gets a value indicating whether the session is currently connected and ready for commands.
    /// <para>获取一个值，指示会话当前是否已连接并准备好接收命令。</para>
    /// </summary>
    bool IsConnected { get; }
}
