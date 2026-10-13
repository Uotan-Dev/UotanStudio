using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Factory for creating <see cref="EdlSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="EdlSession"/> 实例的工厂。</para>
/// With no explicit profile it accepts exactly the bootrom identities the USB classifier reports as
/// <c>edl</c>, so the factory and the discovery table can never disagree about which ports are EDL;
/// other identities require an explicit device profile.
/// <para>未指定设备档案时，它接受的范围与 USB 分类器判定为 <c>edl</c> 的 bootrom 标识完全一致，
/// 因此工厂与发现表绝不可能对哪些端口属于 EDL 产生分歧；其余标识需要显式设备档案。</para>
/// </summary>
internal sealed class EdlFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;
    private readonly byte? _interfaceSubclass;

    /// <summary>Creates an EDL factory that accepts every identity the USB classifier reports as EDL, without
    /// any interface-subclass restriction.
    /// <para>创建接受 USB 分类器判定为 EDL 的全部标识、且不附加接口子类限制的 EDL 工厂。</para></summary>
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

    /// <summary>Further restricts the accepted identities to an observed interface subclass.
    /// <para>在可接受标识之上进一步限制观测到的接口子类。</para></summary>
    /// <param name="interfaceSubclass">Subclass byte that at least one vendor-class <c>0xFF</c> interface must
    /// carry, on top of the classifier's EDL identity table. <para>至少一个厂商类 <c>0xFF</c> 接口必须携带的
    /// 子类字节，附加在分类器的 EDL 标识表之上。</para></param>
    public EdlFactory(byte interfaceSubclass)
    {
        _interfaceSubclass = interfaceSubclass;
    }

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
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
        if (_vendorId is null && _productId is null)
        {
            if (UsbProtocolClassifier.Classify(info.VendorId, info.ProductId, null, null, null) != "edl")
            {
                return false;
            }
        }
        else if (info.VendorId != _vendorId.GetValueOrDefault(0x05C6)
                 || info.ProductId != _productId.GetValueOrDefault(0x9008))
        {
            return false;
        }

        return !_interfaceSubclass.HasValue
            || info.Interfaces.Any(iface => iface.Class == 0xFF && iface.SubClass == _interfaceSubclass.Value);
    }
}