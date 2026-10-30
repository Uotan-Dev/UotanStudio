using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBFinder
{
    private static UsbDevice? ProbeDevice(string path, UsbSessionOptions? options, string? serial)
    {
        (var vid, var pid) = TryParseVidPid(path);
        var metadata = CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
        if (options?.MetadataOnly == true)
        {
            return AttachEnumeratedSerial(metadata, serial);
        }
        UsbAccessPolicy.RequireDirect(metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol, serial);


        var hDevice = Win32API.SimpleCreateHandle(path);
        if (hDevice == Win32API.INVALID_HANDLE_VALUE)
        {
            UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice cannot open [{path}] - reported with metadata only.");
            return AttachEnumeratedSerial(CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0), serial);
        }

        var isLegacy = false;
        try
        {
            var buffer = new byte[256];
            if (Win32API.DeviceIoControl(hDevice, LegacyUsbDevice.IoGetSerialCode, null, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
            {
                isLegacy = true;
            }
        }
        finally
        {
            Win32API.CloseHandle(hDevice);
        }

        if (isLegacy)
        {
            var dev = new LegacyUsbDevice
            {
                DevicePath = path,
                VendorId = vid ?? 0,
                ProductId = pid ?? 0
            };
            if (dev.CreateHandle() == 0)
            {
                return dev;
            }

            UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice legacy open failed for [{path}] - reported with metadata only.");
            return dev;
        }

        if (vid == RockchipVendorId)
        {
            var rockDev = new RockusbDevice
            {
                DevicePath = path,
                VendorId = vid ?? 0,
                ProductId = pid ?? 0,
                SessionOptions = options
            };
            if (rockDev.CreateHandle() == 0)
            {
                if (!string.IsNullOrWhiteSpace(serial))
                {
                    rockDev.SerialNumber = serial;
                }

                UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice opened [{path}] through rockusb.sys.");
                return rockDev;
            }

            rockDev.Dispose();
            UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice rockusb open failed for [{path}] - trying WinUSB.");
        }

        var winDev = new WinUSBDevice
        {
            DevicePath = path,
            VendorId = vid ?? 0,
            ProductId = pid ?? 0,
            SessionOptions = options
        };
        if (winDev.CreateHandle() == 0)
        {
            return winDev;
        }

        UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice WinUSB open failed for [{path}] - reported with metadata only.");
        return AttachEnumeratedSerial(winDev, serial);
    }

    private static UsbDevice? TryOpenWinUSB(string path, UsbSessionOptions? options, string? serial)
    {
        (var vid, var pid) = TryParseVidPid(path);
        var metadata = CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
        if (options?.MetadataOnly == true)
        {
            return AttachEnumeratedSerial(metadata, serial);
        }
        UsbAccessPolicy.RequireDirect(metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol, serial);

        var dev = new WinUSBDevice
        {
            DevicePath = path,
            VendorId = vid ?? 0,
            ProductId = pid ?? 0,
            SessionOptions = options
        };
        if (dev.CreateHandle() == 0)
        {
            return dev;
        }

        UsbTrace.LogFormatted($"WinUSBFinder: TryOpenWinUSB open failed for [{path}] - reported with metadata only.");
        return AttachEnumeratedSerial(dev, serial);
    }

}