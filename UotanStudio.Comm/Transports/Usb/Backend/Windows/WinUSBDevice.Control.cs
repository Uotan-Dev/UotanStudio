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

    public override int GetSerialNumber()
    {
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