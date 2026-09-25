using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBAPI
{
    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_GetOverlappedResult(IntPtr interfaceHandle, IntPtr overlapped,
        out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinUSBPipeInfo
    {
        public WinUSBPipeType PipeType;
        public byte PipeID;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    internal enum WinUSBPipeType
    {
        UsbdPipeTypeControl,
        UsbdPipeTypeIsochronous,
        UsbdPipeTypeBulk,
        UsbdPipeTypeInterrupt
    }

    public static readonly byte USB_DEVICE_DESCRIPTOR_TYPE = 0x01;
    public static readonly byte USB_CONFIGURATION_DESCRIPTOR_TYPE = 0x02;
    public static readonly byte USB_ENDPOINT_DIRECTION_MASK = 0x80;
    public static readonly byte USB_STRING_DESCRIPTOR_TYPE = 0x03;

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Initialize(IntPtr DeviceHandle, out IntPtr InterfaceHandle);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Free(IntPtr InterfaceHandle);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_GetCurrentAlternateSetting(IntPtr InterfaceHandle, out byte SettingNumber);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_GetDescriptor(IntPtr InterfaceHandle, byte DescriptorType, byte Index, ushort LangID,
        IntPtr Buffer, uint BufferLength, out uint LengthTransferred);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryInterfaceSettings(IntPtr InterfaceHandle, byte AlternateInterfaceNumber, out Win32API.USBDeviceInterfaceDescriptor UsbAltInterfaceDescriptor);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryPipe(IntPtr InterfaceHandle, byte AlternateInterfaceNumber, byte PipeIndex, out WinUSBPipeInfo PipeInformation);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, IntPtr Buffer,
        uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, IntPtr Buffer,
        uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ControlTransfer(IntPtr InterfaceHandle, WINUSB_SETUP_PACKET SetupPacket,
        byte[]? Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ResetPipe(IntPtr InterfaceHandle, byte PipeID);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, byte PipeID, uint PolicyType, uint ValueLength, ref uint Value);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, byte PipeID, uint PolicyType, uint ValueLength, ref byte Value);

    [LibraryImport("Winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryDeviceInformation(IntPtr InterfaceHandle, uint InformationType, ref uint BufferLength, out uint Buffer);

    public const uint SHORT_PACKET_TERMINATE = 0x01;
    public const uint AUTO_CLEAR_STALL = 0x02;
    public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
    public const uint IGNORE_SHORT_PACKETS = 0x04;
    public const uint ALLOW_PARTIAL_READS = 0x05;
    public const uint AUTO_FLUSH = 0x06;
    public const uint RAW_IO = 0x07;

    /// <summary>
    /// WinUsb_QueryDeviceInformation information type: negotiated USB speed.
    /// <para>WinUsb_QueryDeviceInformation 信息类型：协商的 USB 速度。</para>
    /// </summary>
    public const uint DEVICE_SPEED = 0x01;
}