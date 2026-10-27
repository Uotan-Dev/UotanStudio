using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Factory for creating <see cref="HdcSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="HdcSession"/> 实例的工厂。</para>
/// Matches the FF/50/01 interface and its bound bulk endpoint pair.
/// </summary>
internal sealed class HdcFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;

    /// <summary>Creates a new HDC factory that matches any HDC device.
    /// <para>创建匹配任何 HDC 设备的新工厂。</para></summary>
    public HdcFactory() { }

    /// <summary>Creates a new HDC factory that matches a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新 HDC 工厂。</para></summary>
    /// <param name="vendorId">The vendor identifier. 厂商 ID。</param>
    /// <param name="productId">The product identifier. 产品 ID。</param>
    public HdcFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    public string ProtocolName => "hdc";

    /// <summary>
    /// Tries to create an HDC session over the specified USB transport session.
    /// <para>尝试在指定 USB 传输会话上创建 HDC 会话。</para>
    /// </summary>
    /// <param name="transport">The underlying transport session. 底层传输会话。</param>
    /// <param name="session">The created protocol session, or <c>null</c> when the transport is not compatible. 创建的协议会话；传输不兼容时返回 <c>null</c>。</param>
    /// <returns><c>true</c> when the protocol session was created. 协议会话创建成功返回 <c>true</c>。</returns>
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

            var iface = info.Interfaces.FirstOrDefault(i => i.Class == 0xff && i.SubClass == 0x50 && i.Protocol == 1
                && i.Endpoints.Any(e => e.EndpointAddress == transport.EndpointIn)
                && i.Endpoints.Any(e => e.EndpointAddress == transport.EndpointOut));
            var hasHdcInterface = iface is not null ||
                (info.Interfaces.Count == 0 && info.InterfaceClass == 0xff && info.InterfaceSubClass == 0x50 && info.InterfaceProtocol == 1);
            if (!hasHdcInterface || transport.EndpointIn == 0 || transport.EndpointOut == 0) return false;

            session = new HdcSession(new UsbDeviceTransport(transport));
            return true;
        }
        catch
        {
            return false;
        }
    }
}