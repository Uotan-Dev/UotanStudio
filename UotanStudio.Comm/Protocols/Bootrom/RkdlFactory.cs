using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Factory for creating <see cref="RkdlSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="RkdlSession"/> 实例的工厂。</para>
/// </summary>
internal sealed class RkdlFactory : IProtocolFactory
{
    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "rkdl";

    /// <summary>
    /// Tries to create an RKDL session over the specified USB transport session. A session is
    /// created when the device matches the Rockchip vendor ID and a known bootrom/loader
    /// product ID (see <see cref="IsRockchipDevice"/>).
    /// <para>尝试在指定 USB 传输会话上创建 RKDL 会话。当设备匹配 Rockchip 厂商 ID 与已知的
    /// bootrom/loader 产品 ID 时创建会话（见 <see cref="IsRockchipDevice"/>）。</para>
    /// </summary>
    /// <param name="transport">The underlying USB transport session. <para>底层 USB 传输会话。</para></param>
    /// <param name="session">The created protocol session, or <c>null</c> when the transport is not compatible. <para>创建的协议会话；传输不兼容时返回 <c>null</c>。</para></param>
    /// <returns><c>true</c> when the protocol session was created. <para>协议会话创建成功返回 <c>true</c>。</para></returns>
    public bool TryCreate(IUsbDeviceSession transport, out IProtocolSession? session)
    {
        session = null;
        try
        {
            if (!IsRockchipDevice(transport))
            {
                return false;
            }

            session = new RkdlSession(new UsbDeviceTransport(transport));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if the USB device is a Rockchip bootrom/loader device, using the upstream
    /// generic rule (vendor 0x2207 and pid >> 8 != 0) plus the known loader product IDs
    /// (see rkdeveloptool RKScan.cpp IsRockusbDevice).
    /// <para>使用上游通用规则（厂商 0x2207 且 pid >> 8 != 0）加已知 loader 产品 ID 检查
    /// USB 设备是否为 Rockchip bootrom/loader 设备（见 rkdeveloptool RKScan.cpp 的
    /// IsRockusbDevice）。</para>
    /// </summary>
    public static bool IsRockchipDevice(IUsbDeviceSession transport)
    {
        if (transport.DeviceInfo.VendorId != RkdlConstants.RockchipVid)
        {
            return false;
        }

        var pid = transport.DeviceInfo.ProductId;
        return (pid >> 8) != 0 || RkdlConstants.KnownLoaderProductIds.Contains(pid);
    }
}