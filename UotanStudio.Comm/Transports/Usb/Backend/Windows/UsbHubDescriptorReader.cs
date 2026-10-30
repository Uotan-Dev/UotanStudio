using System.Buffers.Binary;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Reads a USB device's device descriptor from the hub it is plugged into, without opening the device itself. A port that
/// belongs to a vendor kernel driver — a Rockchip boot ROM port serviced by <c>rockusb.sys</c>, for example — cannot be
/// opened through WinUSB at all, yet the hub driver still reports the descriptor it enumerated, including the
/// <c>bcdUSB</c> value that tells the boot ROM's mask ROM stage from its loader stage.
/// <para>从设备所插入的集线器读取 USB 设备的设备描述符，而不打开设备本身。属于厂商内核驱动的端口——例如由
/// <c>rockusb.sys</c> 服务的瑞芯微 boot ROM 端口——根本无法经 WinUSB 打开，但集线器驱动仍会报告它枚举到的描述符，
/// 其中包含用于区分 boot ROM 的 mask ROM 阶段与 loader 阶段的 <c>bcdUSB</c> 取值。</para>
/// </summary>
internal static class UsbHubDescriptorReader
{
    /// <summary><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION</c>, the request that reports one hub port's device.
    /// <para><c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION</c>，报告单个集线器端口设备的请求。</para>
    /// </summary>
    private const uint IoctlUsbGetNodeConnectionInformation = 0x22040C;

    /// <summary>Bytes of the request buffer: the connection index plus the unused tail the driver tolerates.
    /// <para>请求缓冲区字节数：连接索引加上驱动可容忍的未使用尾部。</para>
    /// </summary>
    private const uint RequestBytes = 32;

    /// <summary>Bytes of the response buffer, above <c>sizeof(USB_NODE_CONNECTION_INFORMATION)</c> plus its pipe table.
    /// <para>响应缓冲区字节数，大于 <c>sizeof(USB_NODE_CONNECTION_INFORMATION)</c> 与其管道表之和。</para>
    /// </summary>
    private const uint ResponseBytes = 512;

    /// <summary>Offset of the embedded <c>USB_DEVICE_DESCRIPTOR</c> behind the leading connection index.
    /// <para>内嵌 <c>USB_DEVICE_DESCRIPTOR</c> 相对开头连接索引的偏移。</para>
    /// </summary>
    private const int DescriptorOffset = 4;

    private const int BcdUsbOffset = DescriptorOffset + 2;
    private const int IdVendorOffset = DescriptorOffset + 8;
    private const int IdProductOffset = DescriptorOffset + 10;

    /// <summary>The USB hub device-interface class, published by every hub devnode on this machine.
    /// <para>USB 集线器设备接口类，本机每个集线器设备节点都会发布。</para>
    /// </summary>
    private static readonly Guid UsbHubInterfaceClass = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    /// <summary>
    /// Reads the <c>bcdUSB</c> value of a connected device through its parent hub, verifying that the hub reports the
    /// same vendor and product id the enumeration already saw.
    /// <para>经父集线器读取在位设备的 <c>bcdUSB</c> 取值，并核对集线器报告的厂商与产品 ID 与枚举结果一致。</para>
    /// </summary>
    /// <param name="instanceId">The device instance id from enumeration. <para>枚举得到的设备实例标识。</para></param>
    /// <param name="expectedVendorId">The vendor id enumeration reported for this device.
    /// <para>枚举为该设备报告的厂商 ID。</para></param>
    /// <param name="expectedProductId">The product id enumeration reported for this device.
    /// <para>枚举为该设备报告的产品 ID。</para></param>
    /// <param name="bcdUsb">The descriptor's <c>bcdUSB</c> value when this returns <see langword="true"/>.
    /// <para>返回 <see langword="true"/> 时描述符的 <c>bcdUSB</c> 取值。</para></param>
    /// <returns><see langword="true"/> when the hub answered for this port and its identity matched; every failure is
    /// reported as <see langword="false"/> rather than as a guess.
    /// <para>集线器为该端口作答且身份匹配时为 <see langword="true"/>；任何失败都返回 <see langword="false"/>，不做猜测。</para></returns>
    internal static bool TryReadBcdUsb(string? instanceId, ushort expectedVendorId, ushort expectedProductId,
        out ushort bcdUsb)
    {
        bcdUsb = 0;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId) ||
            !UsbPnpNative.TryLocateDevNode(instanceId, out var devInst) ||
            !UsbPnpNative.TryReadAddressPort(devInst, out var port) ||
            !UsbPnpNative.TryGetParent(devInst, out var hubDevInst))
        {
            return false;
        }

        var hubInstanceId = UsbPnpNative.GetDeviceId(hubDevInst);
        if (string.IsNullOrWhiteSpace(hubInstanceId) ||
            UsbPnpNative.FindDeviceInterfacePath(UsbHubInterfaceClass, hubInstanceId) is not { } hubPath)
        {
            return false;
        }

        var handle = Win32API.CreateFileW(hubPath, Win32API.GENERIC_READ | Win32API.GENERIC_WRITE,
            Win32API.FILE_SHARE_READ | Win32API.FILE_SHARE_WRITE, IntPtr.Zero, Win32API.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == Win32API.INVALID_HANDLE_VALUE)
        {
            UsbTrace.Log($"UsbHubDescriptorReader: hub '{hubInstanceId}' could not be opened for {instanceId}.");
            return false;
        }

        try
        {
            var request = new byte[RequestBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(request, port);
            var response = new byte[ResponseBytes];
            if (!Win32API.DeviceIoControl(handle, IoctlUsbGetNodeConnectionInformation, request,
                (uint)request.Length, response, (uint)response.Length, out var returned, IntPtr.Zero) ||
                returned < IdProductOffset + sizeof(ushort))
            {
                return false;
            }

            var vendorId = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(IdVendorOffset));
            var productId = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(IdProductOffset));
            if (vendorId != expectedVendorId || productId != expectedProductId)
            {
                UsbTrace.Log($"UsbHubDescriptorReader: port {port} of '{hubInstanceId}' reports " +
                    $"{vendorId:X4}:{productId:X4} instead of {expectedVendorId:X4}:{expectedProductId:X4}.");
                return false;
            }

            bcdUsb = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(BcdUsbOffset));
            return true;
        }
        finally
        {
            Win32API.CloseHandle(handle);
        }
    }
}