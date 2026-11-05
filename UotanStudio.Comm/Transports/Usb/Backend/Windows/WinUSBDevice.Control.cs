using System.ComponentModel;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.WinUSBAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal partial class WinUSBDevice
{
    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        var usb = AcquireWinUsbHandle();
        try
        {
            if (buffer == null)
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
            }
            else
            {
                ValidateBufferRange(buffer, offset, length);
            }

            var transferPacket = new WINUSB_SETUP_PACKET
            {
                RequestType = setupPacket.RequestType,
                Request = setupPacket.Request,
                Value = setupPacket.Value,
                Index = setupPacket.Index,
                Length = (ushort)Math.Max(0, length)
            };

            byte[]? transferBuffer = null;
            var isInDirection = (setupPacket.RequestType & 0x80) != 0;

            if (length > 0)
            {
                if (buffer != null && offset == 0 && length == buffer.Length)
                {
                    transferBuffer = buffer;
                }
                else
                {
                    transferBuffer = new byte[length];
                    if (!isInDirection && buffer != null)
                    {
                        Buffer.BlockCopy(buffer, offset, transferBuffer, 0, length);
                    }
                }
            }

            if (!WinUsb_ControlTransfer(usb, transferPacket, transferBuffer, (uint)length, out var bytesTransferred, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (isInDirection && buffer != null && transferBuffer != null && transferBuffer != buffer)
            {
                Buffer.BlockCopy(transferBuffer, 0, buffer, offset, (int)bytesTransferred);
            }

            return (int)bytesTransferred;
        }
        finally
        {
            ReleaseWinUsbHandle();
        }
    }

    /// <summary>
    /// Reads the device's iSerialNumber string descriptor into <see cref="UsbDevice.SerialNumber"/>.
    /// <para>读取设备的 iSerialNumber 字符串描述符到 <see cref="UsbDevice.SerialNumber"/>。</para>
    /// </summary>
    /// <remarks>
    /// A device descriptor with <c>iSerialNumber == 0</c> has no serial string at all, so asking for descriptor
    /// index 0 returns the supported-languages table, whose payload <c>0x0409</c> decodes into a single Cyrillic
    /// character. Treating that as the serial would rewrite the identity of every gadget without one — including
    /// the SpacemiT U-Boot fastboot stage — so the resolved serial (the Windows device-instance suffix, as adb
    /// uses) is kept instead.
    /// <para>设备描述符中 <c>iSerialNumber == 0</c> 表示根本没有序列号字符串，此时请求描述符索引 0 返回的是支持语言表，
    /// 其负载 <c>0x0409</c> 会被解码成一个西里尔字符。把它当作序列号会改写每个没有序列号的 gadget 的身份——包括
    /// 进迭时空 U-Boot 的 fastboot 阶段——因此这里改为保留已解析的序列号（Windows 设备实例后缀，与 adb 一致）。</para>
    /// </remarks>
    /// <returns>Zero on success, or the native error / -1 when the device has no serial string.
    /// <para>成功返回 0；设备没有序列号字符串时返回原生错误或 -1。</para></returns>
    public override int GetSerialNumber()
    {
        if (_usbDeviceDescriptor.ISerialNumber == 0)
        {
            return -1;
        }

        var usb = AcquireWinUsbHandle();
        try
        {
            uint bytes_get;
            uint descriptorSize = 64;
            const uint maxDescriptorSize = 4096;
            var ptr = Marshal.AllocHGlobal((int)descriptorSize);
            Marshal.Copy(new byte[descriptorSize], 0, ptr, (int)descriptorSize);
            while (!WinUsb_GetDescriptor(usb, USB_STRING_DESCRIPTOR_TYPE,
                _usbDeviceDescriptor.ISerialNumber, 0x0409,
                ptr, descriptorSize, out bytes_get))
            {
                if ((uint)Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                {
                    Marshal.FreeHGlobal(ptr);
                    return Marshal.GetLastWin32Error();
                }

                descriptorSize *= 2;
                if (descriptorSize > maxDescriptorSize)
                {
                    Marshal.FreeHGlobal(ptr);
                    return -1;
                }

                Marshal.FreeHGlobal(ptr);
                ptr = Marshal.AllocHGlobal((int)descriptorSize);
                Marshal.Copy(new byte[descriptorSize], 0, ptr, (int)descriptorSize);
            }

            if (bytes_get <= 2)
            {
                Marshal.FreeHGlobal(ptr);
                return -1;
            }

            var descriptor = new byte[bytes_get];
            Marshal.Copy(ptr, descriptor, 0, descriptor.Length);
            SerialNumber = UsbStringDescriptor.Decode(descriptor, descriptor.Length);
            Marshal.FreeHGlobal(ptr);
            return 0;
        }
        finally
        {
            ReleaseWinUsbHandle();
        }
    }
}