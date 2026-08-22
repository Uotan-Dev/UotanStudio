using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

internal static class HarmonyOSUsbDDK
{
    public const string LibUsbNdk = "libusb_ndk.z.so";

    public const int USB_DDK_NO_ERROR = 0;
    public const int USB_DDK_INVALID_PARAMETER = -1;
    public const int USB_DDK_INVALID_OPERATION = -2;
    public const int USB_DDK_INIT_ERROR = -3;
    public const int USB_DDK_SERVICE_ERROR = -4;
    public const int USB_DDK_MEMORY_ERROR = -5;
    public const int USB_DDK_IO_ERROR = -6;
    public const int USB_DDK_DEVICE_BUSY = -7;
    public const int USB_DDK_TIMEOUT = -8;

    internal static bool IsHarmonyOSPlatform()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return false;
            }

            try
            {
                var osRelease = File.ReadAllText("/etc/os-release");
                if (osRelease.IndexOf("HarmonyOS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            if (File.Exists("/etc/hmos-release") || File.Exists("/system/etc/hmos-release"))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbControlRequestSetup
    {
        public byte bmRequestType;
        public byte bRequest;
        public ushort wValue;
        public ushort wIndex;
        public ushort wLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbDeviceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbConfigDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort wTotalLength;
        public byte bNumInterfaces;
        public byte bConfigurationValue;
        public byte iConfiguration;
        public byte bmAttributes;
        public byte MaxPower;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbInterfaceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbEndpointDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bEndpointAddress;
        public byte bmAttributes;
        public ushort wMaxPacketSize;
        public byte bInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbDdkConfigDescriptor
    {
        public UsbConfigDescriptor configDescriptor;
        public IntPtr iface;
        public byte numIface;
        public IntPtr extra;
        public int extraLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbDdkInterface
    {
        public IntPtr altsetting;
        public byte numAltsetting;
        public IntPtr extra;
        public int extraLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbDdkInterfaceDescriptor
    {
        public UsbInterfaceDescriptor interfaceDescriptor;
        public IntPtr endpoint;
        public byte numEndpoint;
        public IntPtr extra;
        public int extraLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbRequestPipe
    {
        public ulong interfaceHandle;
        public byte endpointAddress;
        public uint timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbDeviceMemMap
    {
        public ulong deviceId;
        public IntPtr buffer;
        public UIntPtr size;
        public UIntPtr offset;
        public UIntPtr length;
    }

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_Init")]
    public static extern int OH_Usb_Init();

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_Release")]
    public static extern void OH_Usb_Release();

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_ReleaseResource")]
    public static extern int OH_Usb_ReleaseResource();

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_GetDeviceDescriptor")]
    public static extern int OH_Usb_GetDeviceDescriptor(ulong deviceId, ref UsbDeviceDescriptor desc);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_GetConfigDescriptor")]
    public static extern int OH_Usb_GetConfigDescriptor(ulong deviceId, byte configIndex, ref IntPtr config);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_FreeConfigDescriptor")]
    public static extern void OH_Usb_FreeConfigDescriptor(IntPtr config);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_ClaimInterface")]
    public static extern int OH_Usb_ClaimInterface(ulong deviceId, byte interfaceIndex, ref ulong interfaceHandle);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_ReleaseInterface")]
    public static extern int OH_Usb_ReleaseInterface(ulong interfaceHandle);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_SelectInterfaceSetting")]
    public static extern int OH_Usb_SelectInterfaceSetting(ulong interfaceHandle, byte settingIndex);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_GetCurrentInterfaceSetting")]
    public static extern int OH_Usb_GetCurrentInterfaceSetting(ulong interfaceHandle, ref byte settingIndex);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_SendControlReadRequest")]
    public static extern int OH_Usb_SendControlReadRequest(
        ulong interfaceHandle,
        ref UsbControlRequestSetup setup,
        uint timeout,
        byte[] data,
        ref uint dataLen);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_SendControlWriteRequest")]
    public static extern int OH_Usb_SendControlWriteRequest(
        ulong interfaceHandle,
        ref UsbControlRequestSetup setup,
        uint timeout,
        byte[] data,
        uint dataLen);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_SendPipeRequest")]
    public static extern int OH_Usb_SendPipeRequest(ref UsbRequestPipe pipe, ref UsbDeviceMemMap devMmap);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_CreateDeviceMemMap")]
    public static extern int OH_Usb_CreateDeviceMemMap(ulong deviceId, UIntPtr size, ref IntPtr devMmap);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_DestroyDeviceMemMap")]
    public static extern int OH_Usb_DestroyDeviceMemMap(IntPtr devMmap);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_GetDescriptor")]
    public static extern int OH_Usb_GetDescriptor(ulong deviceId, byte descType, byte descIndex, byte[] data, ref uint dataLen);

    [DllImport(LibUsbNdk, EntryPoint = "OH_Usb_GetInterfaceSetting")]
    public static extern int OH_Usb_GetInterfaceSetting(ulong deviceId, byte configIndex, byte interfaceIndex, ref byte altsetting);

    internal static string GetErrorMessage(int errorCode)
    {
        switch (errorCode)
        {
            case USB_DDK_NO_ERROR: return "Success";
            case USB_DDK_INVALID_PARAMETER: return "Invalid parameter";
            case USB_DDK_INVALID_OPERATION: return "Invalid operation";
            case USB_DDK_INIT_ERROR: return "Initialization error";
            case USB_DDK_SERVICE_ERROR: return "Service error";
            case USB_DDK_MEMORY_ERROR: return "Memory error";
            case USB_DDK_IO_ERROR: return "I/O error";
            case USB_DDK_DEVICE_BUSY: return "Device busy";
            case USB_DDK_TIMEOUT: return "Timeout";
            default: return $"Unknown error ({errorCode})";
        }
    }
}
