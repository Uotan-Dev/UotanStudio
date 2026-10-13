using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Factory for creating <see cref="AdbSession"/> instances from USB device sessions,
/// matching the standard ADB USB interface and optionally a VID/PID pair.
/// <para>从 USB 设备会话创建 <see cref="AdbSession"/> 实例的工厂，匹配标准 ADB USB
/// 接口，并可选按 VID/PID 对匹配。</para>
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
    /// <param name="vendorId">The vendor identifier. <para>厂商 ID。</para></param>
    /// <param name="productId">The product identifier. <para>产品 ID。</para></param>
    public AdbFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    /// <value>The fixed protocol name string. <para>固定的协议名称字符串。</para></value>
    public string ProtocolName => "adb";

    /// <summary>
    /// Tries to create an ADB session over the specified USB transport session.
    /// <para>尝试在指定 USB 传输会话上创建 ADB 会话。</para>
    /// </summary>
    /// <param name="transport">The underlying transport session. <para>底层传输会话。</para></param>
    /// <param name="session">The created protocol session, or <c>null</c> when the transport is not compatible.
    /// <para>创建的协议会话；传输不兼容时为 <c>null</c>。</para></param>
    /// <returns><c>true</c> when the protocol session was created. <para>协议会话创建成功时返回 <c>true</c>。</para></returns>
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