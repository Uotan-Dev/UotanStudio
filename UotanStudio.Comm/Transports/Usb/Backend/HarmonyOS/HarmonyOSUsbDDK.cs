using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

internal static partial class HarmonyOSUsbDDK
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

            return File.Exists("/etc/hmos-release") || File.Exists("/system/etc/hmos-release");
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbControlRequestSetup
    {
        public byte BmRequestType;
        public byte BRequest;
        public ushort WValue;
        public ushort WIndex;
        public ushort WLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDeviceDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public ushort BcdUSB;
        public byte BDeviceClass;
        public byte BDeviceSubClass;
        public byte BDeviceProtocol;
        public byte BMaxPacketSize0;
        public ushort IdVendor;
        public ushort IdProduct;
        public ushort BcdDevice;
        public byte IManufacturer;
        public byte IProduct;
        public byte ISerialNumber;
        public byte BNumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbConfigDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public ushort WTotalLength;
        public byte BNumInterfaces;
        public byte BConfigurationValue;
        public byte IConfiguration;
        public byte BmAttributes;
        public byte MaxPower;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbInterfaceDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public byte BInterfaceNumber;
        public byte BAlternateSetting;
        public byte BNumEndpoints;
        public byte BInterfaceClass;
        public byte BInterfaceSubClass;
        public byte BInterfaceProtocol;
        public byte IInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbEndpointDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public byte BEndpointAddress;
        public byte BmAttributes;
        public ushort WMaxPacketSize;
        public byte BInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDdkConfigDescriptor
    {
        public UsbConfigDescriptor ConfigDescriptor;
        public IntPtr Iface;
        public byte NumIface;
        public IntPtr Extra;
        public int ExtraLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDdkInterface
    {
        public IntPtr Altsetting;
        public byte NumAltsetting;
        public IntPtr Extra;
        public int ExtraLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDdkInterfaceDescriptor
    {
        public UsbInterfaceDescriptor InterfaceDescriptor;
        public IntPtr Endpoint;
        public byte NumEndpoint;
        public IntPtr Extra;
        public int ExtraLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbRequestPipe
    {
        public ulong InterfaceHandle;
        public byte EndpointAddress;
        public uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDeviceMemMap
    {
        public ulong DeviceId;
        public IntPtr Buffer;
        public UIntPtr Size;
        public UIntPtr Offset;
        public UIntPtr Length;
    }

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_Init")]
    public static partial int OH_Usb_Init();

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_Release")]
    public static partial void OH_Usb_Release();

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_ReleaseResource")]
    public static partial int OH_Usb_ReleaseResource();

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetDeviceDescriptor")]
    public static partial int OH_Usb_GetDeviceDescriptor(ulong DeviceId, ref UsbDeviceDescriptor desc);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetConfigDescriptor")]
    public static partial int OH_Usb_GetConfigDescriptor(ulong DeviceId, byte configIndex, ref IntPtr config);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_FreeConfigDescriptor")]
    public static partial void OH_Usb_FreeConfigDescriptor(IntPtr config);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_ClaimInterface")]
    public static partial int OH_Usb_ClaimInterface(ulong DeviceId, byte interfaceIndex, ref ulong interfaceHandle);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_ReleaseInterface")]
    public static partial int OH_Usb_ReleaseInterface(ulong interfaceHandle);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_SelectInterfaceSetting")]
    public static partial int OH_Usb_SelectInterfaceSetting(ulong interfaceHandle, byte settingIndex);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetCurrentInterfaceSetting")]
    public static partial int OH_Usb_GetCurrentInterfaceSetting(ulong interfaceHandle, ref byte settingIndex);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_SendControlReadRequest")]
    public static partial int OH_Usb_SendControlReadRequest(
        ulong interfaceHandle,
        ref UsbControlRequestSetup setup,
        uint Timeout,
        byte[] Data,
        ref uint dataLen);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_SendControlWriteRequest")]
    public static partial int OH_Usb_SendControlWriteRequest(
        ulong interfaceHandle,
        ref UsbControlRequestSetup setup,
        uint Timeout,
        byte[] Data,
        uint dataLen);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_SendPipeRequest")]
    public static partial int OH_Usb_SendPipeRequest(ref UsbRequestPipe pipe, ref UsbDeviceMemMap devMmap);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_CreateDeviceMemMap")]
    public static partial int OH_Usb_CreateDeviceMemMap(ulong DeviceId, UIntPtr Size, ref IntPtr devMmap);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_DestroyDeviceMemMap")]
    public static partial int OH_Usb_DestroyDeviceMemMap(IntPtr devMmap);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetDescriptor")]
    public static partial int OH_Usb_GetDescriptor(ulong DeviceId, byte descType, byte descIndex, IntPtr Data, ref uint dataLen);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetInterfaceSetting")]
    public static partial int OH_Usb_GetInterfaceSetting(ulong DeviceId, byte configIndex, byte interfaceIndex, ref byte Altsetting);

    internal static string GetErrorMessage(int errorCode)
    {
        return errorCode switch
        {
            USB_DDK_NO_ERROR => "Success",
            USB_DDK_INVALID_PARAMETER => "Invalid parameter",
            USB_DDK_INVALID_OPERATION => "Invalid operation",
            USB_DDK_INIT_ERROR => "Initialization error",
            USB_DDK_SERVICE_ERROR => "Service error",
            USB_DDK_MEMORY_ERROR => "Memory error",
            USB_DDK_IO_ERROR => "I/O error",
            USB_DDK_DEVICE_BUSY => "Device busy",
            USB_DDK_TIMEOUT => "Timeout",
            _ => $"Unknown error ({errorCode})",
        };
    }
}