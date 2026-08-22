using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Creates <see cref="UsbDeviceInfo"/> instances from backend <see cref="UsbDevice"/> objects.
/// <para>从后端 <see cref="UsbDevice"/> 对象创建 <see cref="UsbDeviceInfo"/> 实例。</para>
/// </summary>
internal static class UsbDeviceInfoFactory
{
    /// <summary>
    /// Creates a <see cref="UsbDeviceInfo"/> from the specified backend device.
    /// <para>从指定的后端设备创建 <see cref="UsbDeviceInfo"/>。</para>
    /// </summary>
    /// <param name="apiName">The API name associated with the device. <para>与设备关联的 API 名称。</para></param>
    /// <param name="apiKind">The backend API kind. <para>后端 API 类型。</para></param>
    /// <param name="device">The backend USB device. <para>后端 USB 设备。</para></param>
    /// <returns>A populated <see cref="UsbDeviceInfo"/> instance. <para>已填充的 <see cref="UsbDeviceInfo"/> 实例。</para></returns>
    public static UsbDeviceInfo FromBackendDevice(string apiName, UsbApiKind apiKind, UsbDevice device)
    {
        var info = new UsbDeviceInfo
        {
            ApiName = apiName,
            SourceApiKind = apiKind,
            SourceDeviceType = device.GetType().Name,
            DevicePath = device.DevicePath,
            SerialNumber = device.SerialNumber,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            InterfaceClass = device.InterfaceClass,
            InterfaceSubClass = device.InterfaceSubClass,
            InterfaceProtocol = device.InterfaceProtocol,
            InterfaceMetadataObserved = device.InterfaceMetadataObserved,
            Speed = device.Speed,
            Interfaces = device.Interfaces
        };

        info.DeviceKey = UsbDeviceIdentity.BuildKey(info);
        return info;
    }
}