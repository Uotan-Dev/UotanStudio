using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Factory for creating <see cref="EdlSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="EdlSession"/> 实例的工厂。</para>
/// Matches Qualcomm HS-USB QDLoader 9008 (class 0xFF, subclass 0x01) or Rockchip RKUD (class 0xFF, subclass 0x00) interfaces.
/// <para>匹配 Qualcomm HS-USB QDLoader 9008（类 0xFF、子类 0x01）或 Rockchip RKUD（类 0xFF、子类 0x00）接口。</para>
/// </summary>
public sealed class EdlFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;
    private readonly byte? _interfaceSubclass;

    /// <summary>Creates a new EDL factory matching any Qualcomm/Rockchip EDL device.
    /// <para>创建匹配任何 Qualcomm/Rockchip EDL 设备的新工厂。</para></summary>
    public EdlFactory() { }

    /// <summary>Creates a new EDL factory matching a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新工厂。</para></summary>
    public EdlFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Creates a new EDL factory matching a specific interface subclass (0x01=Qualcomm, 0x00=Rockchip).
    /// <para>创建匹配指定接口子类（0x01=Qualcomm，0x00=Rockchip）的新工厂。</para></summary>
    public EdlFactory(byte interfaceSubclass)
    {
        _interfaceSubclass = interfaceSubclass;
    }

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "edl";

    /// <summary>
    /// Tries to create an EDL session over the specified USB transport session.
    /// <para>尝试在指定 USB 传输会话上创建 EDL 会话。</para>
    /// </summary>
    public bool TryCreate(IUsbDeviceSession transport, out IProtocolSession? session)
    {
        session = null;
        try
        {
            var info = transport.DeviceInfo;

            if (_vendorId.HasValue && info.VendorId != _vendorId.Value)
            {
                return false;
            }

            if (_productId.HasValue && info.ProductId != _productId.Value)
            {
                return false;
            }

            foreach (var iface in info.Interfaces)
            {
                if (iface.Class == 0xFF && iface.SubClass == 0x01)
                {
                    session = new EdlSession(new UsbDeviceTransport(transport));
                    return true;
                }
            }

            if (info.VendorId == 0x05C6 && (
                info.ProductId == 0x9008 ||  // Qualcomm QDLoader 9008
                info.ProductId == 0x6000 ||  // Samsung Qualcomm EDL
                info.ProductId == 0x900E ||  // Xiaomi Qualcomm EDL
                info.ProductId == 0x9025 ||  // OnePlus Qualcomm EDL
                info.ProductId == 0x9087))   // Generic Qualcomm EDL
            {
                session = new EdlSession(new UsbDeviceTransport(transport));
                return true;
            }

            if (info.VendorId == 0x2207 && (
                info.ProductId == 0x300A ||  // RK3066 Maskrom
                info.ProductId == 0x320B ||  // RK3288 Loader
                info.ProductId == 0x330C ||  // RK3399 Loader
                info.ProductId == 0x350B ||  // RK3588 Loader
                info.ProductId == 0x330A ||  // RK3326 Loader
                info.ProductId == 0x320A))   // RK3229 Loader
            {
                session = new EdlSession(new UsbDeviceTransport(transport));
                return true;
            }

            if (_interfaceSubclass.HasValue)
            {
                foreach (var iface in info.Interfaces)
                {
                    if (iface.Class == 0xFF && iface.SubClass == _interfaceSubclass.Value)
                    {
                        session = new EdlSession(new UsbDeviceTransport(transport));
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}