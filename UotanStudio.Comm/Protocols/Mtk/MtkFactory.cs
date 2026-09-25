using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Factory for creating <see cref="MtkSession"/> instances from USB device sessions bound to
/// the MediaTek boot chain (BROM 0E8D:0003, preloader/DA 0E8D:2000|2001).
/// <para>从绑定联发科启动链（BROM 0E8D:0003、preloader/DA 0E8D:2000|2001）的 USB 设备会话
/// 创建 <see cref="MtkSession"/> 实例的工厂。</para>
/// </summary>
internal sealed class MtkFactory : IProtocolFactory
{
    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "mtk";

    /// <summary>
    /// Tries to create an MTK session over the specified USB transport session. The session is
    /// not initialized here; call <see cref="MtkSession.InitializeAsync"/> to run the handshake.
    /// <para>尝试在指定 USB 传输会话上创建 MTK 会话。此处不做初始化；调用
    /// <see cref="MtkSession.InitializeAsync"/> 执行握手。</para>
    /// </summary>
    public bool TryCreate(IUsbDeviceSession transport, out IProtocolSession? session)
    {
        session = null;
        try
        {
            if (!Matches(transport.DeviceInfo))
            {
                return false;
            }

            session = new MtkSession(new UsbDeviceTransport(transport));
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool Matches(UsbDeviceInfo info) =>
        info.VendorId == 0x0E8D && info.ProductId is 0x0003 or 0x2000 or 0x2001;
}