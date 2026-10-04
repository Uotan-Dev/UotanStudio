using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Factory for creating <see cref="MtkSession"/> instances from USB device sessions bound to
/// the MediaTek boot chain (BROM 0E8D:0003, preloader/DA 0E8D:2000|2001) or the OEM vendor ids the
/// reference client treats as MediaTek download ports (0x1004 LG, 0x22D9 OPPO, 0x0FCE Sony).
/// <para>从绑定联发科启动链（BROM 0E8D:0003、preloader/DA 0E8D:2000|2001）的 USB 设备会话，或参考客户端
/// 视为联发科下载端口的 OEM 厂商 ID（0x1004 LG、0x22D9 OPPO、0x0FCE Sony）创建
/// <see cref="MtkSession"/> 实例的工厂。</para>
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
        info.VendorId == 0x0E8D && info.ProductId is 0x0003 or 0x2000 or 0x2001
        || IsOemDownloadPort(info.VendorId, info.ProductId);

    /// <summary>
    /// Reports whether the pair is an OEM rebranding of a MediaTek boot download port. The reference
    /// client filters enumeration to vendor ids 0x0E8D, 0x1004, 0x22D9 and 0x0FCE
    /// (<c>mtkclient/Library/Connection/usblib.py</c>, line 306) and the port table
    /// (<c>mtkclient/config/usb_ids.py</c>, lines 8-14) records LG 0x1004:0x6000 as a preloader port,
    /// OPPO 0x22D9:0x0006 as a preloader port, and Sony 0x0FCE:0xF200, 0xD1E9, 0xD1E2, 0xD1EC and
    /// 0xD1DD as bootrom ports; no other product id under those vendors is a download port.
    /// <para>报告该组合是否为联发科启动下载端口的 OEM 换牌版本。参考客户端把枚举过滤限定在厂商
    /// 0x0E8D、0x1004、0x22D9 与 0x0FCE（<c>mtkclient/Library/Connection/usblib.py</c> 第 306 行），
    /// 而端口表（<c>mtkclient/config/usb_ids.py</c> 第 8-14 行）把 LG 0x1004:0x6000 记为 preloader
    /// 端口、OPPO 0x22D9:0x0006 记为 preloader 端口，把 Sony 0x0FCE:0xF200、0xD1E9、0xD1E2、0xD1EC
    /// 与 0xD1DD 记为 bootrom 端口；这些厂商下的其它产品 ID 都不是下载端口。</para>
    /// </summary>
    /// <param name="vendorId">USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">USB product id. <para>USB 产品 ID。</para></param>
    /// <returns><see langword="true"/> for a documented OEM download port.
    /// <para>对已记载的 OEM 下载端口返回 <see langword="true"/>。</para></returns>
    internal static bool IsOemDownloadPort(ushort vendorId, ushort productId) =>
        (vendorId, productId) switch
        {
            (0x1004, 0x6000) => true,
            (0x22D9, 0x0006) => true,
            (0x0FCE, 0xF200 or 0xD1E9 or 0xD1E2 or 0xD1EC or 0xD1DD) => true,
            _ => false
        };
}