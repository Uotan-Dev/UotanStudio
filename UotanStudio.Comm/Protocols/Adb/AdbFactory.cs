using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Factory for creating <see cref="AdbSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="AdbSession"/> 实例的工厂。</para>
/// A factory targets the standard ADB USB interface (class 0xFF, subclass 0x42,
/// protocol 0x01) and matches by VID/PID when provided. When interface metadata was
/// not observed, only the Google ADB PnP identity is accepted as a fallback — a bare
/// "has any interface" result never counts, so unrelated device classes (fastboot,
/// EDL, MTP) are never claimed and never receive CNXN frames.
/// <para>工厂针对标准 ADB USB 接口（类 0xFF、子类 0x42、协议 0x01），并在提供时按
/// VID/PID 匹配。接口元数据未观测时，仅接受 Google ADB PnP 标识作为兜底；“存在任意
/// 接口”不构成证据，因此 fastboot、EDL、MTP 等无关设备类不会被认领，也不会收到 CNXN 帧。</para>
/// </summary>
internal sealed class AdbFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;

    /// <summary>Creates a new ADB factory that matches any ADB device.
    /// <para>创建匹配任何 ADB 设备的新工厂。</para></summary>
    public AdbFactory() { }

    /// <summary>Creates a new ADB factory that matches a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新 ADB 工厂。</para></summary>
    /// <param name="vendorId">The vendor identifier. 厂商 ID。</param>
    /// <param name="productId">The product identifier. 产品 ID。</param>
    public AdbFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "adb";

    /// <summary>
    /// Tries to create an ADB session over the specified USB transport session.
    /// <para>尝试在指定 USB 传输会话上创建 ADB 会话。</para>
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

            var hasAdbInterface = false;
            if (info.InterfaceMetadataObserved)
            {
                foreach (var iface in info.Interfaces)
                {
                    if (iface.Class == AdbConstants.InterfaceClass &&
                        iface.SubClass == AdbConstants.InterfaceSubclass &&
                        iface.Protocol == AdbConstants.InterfaceProtocol)
                    {
                        hasAdbInterface = true;
                        break;
                    }
                }
            }
            else
            {
                hasAdbInterface = info.VendorId == 0x18D1 && (info.ProductId == 0x4EE2 || info.ProductId == 0x4EE7);
            }

            if (!hasAdbInterface)
            {
                return false;
            }

            session = new AdbSession(new UsbDeviceTransport(transport));
            return true;
        }
        catch
        {
            return false;
        }
    }
}