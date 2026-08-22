using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Factory for creating <see cref="FastbootSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="FastbootSession"/> 实例的工厂。</para>
/// A factory targets the standard fastboot USB interface (class 0xFF, subclass 0x42, protocol 0x03)
/// and matches by VID/PID when provided.
/// <para>工厂针对标准 fastboot USB 接口（类 0xFF、子类 0x42、协议 0x03），
/// 并在提供时按 VID/PID 匹配。</para>
/// </summary>
public sealed class FastbootFactory : IProtocolFactory
{
    private readonly ushort? _vendorId;
    private readonly ushort? _productId;
    private readonly int? _timeoutMs;

    /// <summary>Creates a new fastboot factory that matches any fastboot device.
    /// <para>创建匹配任何 fastboot 设备的新工厂。</para></summary>
    public FastbootFactory() { }

    /// <summary>Creates a new fastboot factory that matches a specific VID/PID.
    /// <para>创建匹配指定 VID/PID 的新工厂。</para></summary>
    public FastbootFactory(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Creates a new fastboot factory with a specific I/O timeout.
    /// <para>创建具有特定 I/O 超时的新工厂。</para></summary>
    public FastbootFactory(int timeoutMs)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "fastboot";

    /// <summary>
    /// Tries to create a fastboot session over the specified USB transport session.
    /// <para>尝试在指定 USB 传输会话上创建 fastboot 会话。</para>
    /// </summary>
    public bool TryCreate(IUsbDeviceSession transport, out IProtocolSession? session)
    {
        session = null;
        try
        {
            UsbDeviceInfo info = transport.DeviceInfo;

            // Check VID/PID if specified
            if (_vendorId.HasValue && info.VendorId != _vendorId.Value) return false;
            if (_productId.HasValue && info.ProductId != _productId.Value) return false;

            // Check for fastboot interface class/subclass/protocol
            // The device doesn't need to advertise the class via the backend;
            // we also allow any device that has bulk endpoints (for WinUSB-bound devices).
            var hasFastbootInterface = false;
            if (info.InterfaceMetadataObserved)
            {
                foreach (UsbInterfaceInfo iface in info.Interfaces)
                {
                    if (iface.Class == FastbootConstants.FastbootInterfaceClass &&
                        iface.SubClass == FastbootConstants.FastbootInterfaceSubclass &&
                        iface.Protocol == FastbootConstants.FastbootInterfaceProtocol)
                    {
                        hasFastbootInterface = true;
                        break;
                    }
                }
            }
            else
            {
                // When interface metadata is not observed, check if we have bulk endpoints
                // or if the VID/PID matches known fastboot IDs
                hasFastbootInterface = info.Interfaces.Count > 0 ||
                    (info.VendorId == 0x18D1 && (info.ProductId == 0x4EE0 || info.ProductId == 0x4EE1 || info.ProductId == 0xD00D)) ||
                    (info.VendorId == 0x05C6) || // Qualcomm fastboot
                    (info.VendorId == 0x0FCE) || // Sony fastboot
                    (info.VendorId == 0x0BB4) || // HTC fastboot
                    (info.VendorId == 0x12D1) || // Huawei fastboot
                    (info.VendorId == 0x22B8) || // Motorola fastboot
                    (info.VendorId == 0x04E8);   // Samsung fastboot
            }

            if (!hasFastbootInterface) return false;

            session = new FastbootSession(transport, _timeoutMs);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
