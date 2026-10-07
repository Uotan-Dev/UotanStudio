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

    /// <summary>Creates an EDL factory matching Qualcomm QDLoader 9008 without any interface-subclass restriction.
    /// <para>创建匹配高通 QDLoader 9008 且不附加接口子类限制的 EDL 工厂。</para></summary>
    public EdlFactory() { }

    /// <summary>Creates a new EDL factory matching a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新工厂。</para></summary>
    /// <param name="vendorId">USB vendor identifier the device must report. <para>设备必须上报的 USB 厂商标识符。</para></param>
    /// <param name="productId">USB product identifier the device must report. <para>设备必须上报的 USB 产品标识符。</para></param>
    public EdlFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Further restricts the default QDLoader identity to an observed interface subclass.
    /// <para>在默认 QDLoader 标识之上进一步限制观测到的接口子类。</para></summary>
    /// <param name="interfaceSubclass">Subclass byte that at least one vendor-class <c>0xFF</c> interface must carry,
    /// alongside the default <c>05C6:9008</c> identity. <para>至少一个厂商类 <c>0xFF</c> 接口必须携带的子类字节，与默认的
    /// <c>05C6:9008</c> 标识一同匹配。</para></param>
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
    /// <param name="transport">USB transport session whose device identity is matched against this factory's VID, PID
    /// and optional interface subclass; ownership is transferred to the created session.
    /// <para>其设备标识将与本工厂的 VID、PID 及可选接口子类比对的 USB 传输会话；所有权转移给所创建的会话。</para></param>
    /// <param name="session">Receives the new EDL session on success, or <see langword="null"/> when the identity does not
    /// match or the session could not be created. <para>成功时接收新建的 EDL 会话；标识不匹配或无法创建会话时为
    /// <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when an EDL session was created; <see langword="false"/> reports a mismatch or a
    /// failed setup without throwing. <para>成功创建 EDL 会话时为 <see langword="true"/>；不匹配或初始化失败时返回
    /// <see langword="false"/> 而不抛出异常。</para></returns>
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