namespace UotanStudio.Comm.Protocols.Apple.Usbmuxd;

/// <summary>
/// A device known to the host usbmuxd daemon.
/// <para>主机 usbmuxd 守护进程已知的一台设备。</para>
/// </summary>
/// <param name="DeviceId">The usbmuxd-assigned connection identifier, valid for the daemon lifetime. <para>usbmuxd 分配的连接标识，随守护进程生命周期有效。</para></param>
/// <param name="SerialNumber">The USB serial number, which for Apple devices is the UDID. <para>USB 序列号；Apple 设备上即为 UDID。</para></param>
/// <param name="ConnectionType">The reported connection type, typically <c>USB</c> or <c>Network</c>. <para>上报的连接类型，通常为 <c>USB</c> 或 <c>Network</c>。</para></param>
/// <param name="ProductId">The USB product identifier. <para>USB 产品 ID。</para></param>
/// <param name="LocationId">The USB hub location identifier. <para>USB 集线器位置 ID。</para></param>
public sealed record UsbmuxdDevice(
    int DeviceId,
    string SerialNumber,
    string ConnectionType,
    ushort ProductId,
    int LocationId)
{
    /// <summary>Gets the USB connection type constant. <para>获取 USB 连接类型常量。</para></summary>
    public const string ConnectionTypeUsb = "USB";

    /// <summary>Gets the network (Wi-Fi sync) connection type constant. <para>获取网络（Wi-Fi 同步）连接类型常量。</para></summary>
    public const string ConnectionTypeNetwork = "Network";
}