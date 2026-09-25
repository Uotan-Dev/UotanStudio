using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Factory for creating <see cref="EdlSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="EdlSession"/> 实例的工厂。</para>
/// Defaults to Qualcomm 05C6:9008. Other identities require an explicit device profile.
/// </summary>
internal sealed class EdlFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;
    private readonly byte? _interfaceSubclass;

    /// <summary>Creates an EDL factory matching Qualcomm QDLoader 9008.</summary>
    public EdlFactory() { }

    /// <summary>Creates a new EDL factory matching a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新工厂。</para></summary>
    public EdlFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Further restricts the default QDLoader identity to an observed interface subclass.</summary>
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
            if (!Matches(transport.DeviceInfo))
            {
                return false;
            }

            session = new EdlSession(new UsbDeviceTransport(transport));
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool Matches(UsbDeviceInfo info)
    {
        if (info.VendorId != (_vendorId ?? 0x05C6) || info.ProductId != (_productId ?? 0x9008))
        {
            return false;
        }

        return !_interfaceSubclass.HasValue
            || info.Interfaces.Any(iface => iface.Class == 0xFF && iface.SubClass == _interfaceSubclass.Value);
    }
}