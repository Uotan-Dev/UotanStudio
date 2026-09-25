using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class Win32API
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public const uint FILE_DEVICE_UNKNOWN = 0x00000022;
    public const uint METHOD_BUFFERED = 0;
    public const uint FILE_READ_ACCESS = 1;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public static uint CTL_CODE(uint deviceType, uint function, uint method, uint access) => (deviceType << 16) | (access << 14) | (function << 2) | method;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr CreateFileW([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint access,
                                         uint shareMode, IntPtr securityAttributes,
                                         uint createDisposition, uint flagsAndAttributes,
                                         IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(IntPtr device, uint code,
                                          byte[]? inBuffer, uint inBufferSize,
                                          byte[]? outBuffer, uint outBufferSize,
                                          out uint bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(IntPtr device, uint code,
                                          IntPtr inBuffer, uint inBufferSize,
                                          byte[]? outBuffer, uint outBufferSize,
                                          out uint bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(IntPtr hFile, IntPtr buffer, uint sizeToWrite, out uint bytesWritten, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(IntPtr hFile, IntPtr buffer, uint sizeToRead, out uint bytesRead, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    public const uint KEY_READ = 0x20019;
    public const uint REG_MULTI_SZ = 7;
    public static readonly IntPtr HKEY_LOCAL_MACHINE = new(0x80000002);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int RegOpenKeyExW(IntPtr hKey, [MarshalAs(UnmanagedType.LPWStr)] string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int RegQueryValueExW(IntPtr hKey, [MarshalAs(UnmanagedType.LPWStr)] string lpValueName, IntPtr lpReserved, out uint lpType, IntPtr lpData, ref uint lpcbData);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int RegEnumKeyExW(IntPtr hKey, uint dwIndex, IntPtr lpName, ref uint lpcchName,
                                           IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpftLastWriteTime);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int RegCloseKey(IntPtr hKey);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
                                                          IntPtr deviceInstanceId, uint deviceInstanceIdSize,
                                                          out uint requiredSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct USBDeviceDescriptor
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
    internal struct USBDeviceConfigDescriptor
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
    internal struct USBDeviceInterfaceDescriptor
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

    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public const uint DIGCF_ALLCLASSES = 0x00000004;

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial IntPtr SetupDiGetClassDevsW(ref Guid guid, [MarshalAs(UnmanagedType.LPWStr)] string? enumerator, IntPtr parent, uint flag);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial IntPtr SetupDiGetClassDevsW(IntPtr classGuid, [MarshalAs(UnmanagedType.LPWStr)] string? enumerator, IntPtr parent, uint flag);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                                                          ref Guid interfaceClassGuid, uint index,
                                                          ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                                                          IntPtr interfaceClassGuid, uint index,
                                                          ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
                                                               IntPtr deviceInterfaceDetailData,
                                                               uint detailSize,
                                                               out uint requiredSize,
                                                               IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
                                                               IntPtr deviceInterfaceDetailData,
                                                               uint detailSize,
                                                               out uint requiredSize,
                                                               ref SpDevInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    public static IntPtr SimpleCreateHandle(string filePath, bool overlapped = false)
    {
        return CreateFileW(filePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING,
            overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);
    }

    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MORE_ITEMS = 259;

    /// <summary>
    /// OVERLAPPED structure used for native asynchronous (overlapped) I/O.
    /// <para>用于原生异步（重叠）I/O 的 OVERLAPPED 结构。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr HEvent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(IntPtr handle, IntPtr overlapped);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    public const uint WM_DEVICECHANGE = 0x0219;
    public const uint WM_QUIT = 0x0012;
    public const uint DBT_DEVICEARRIVAL = 0x8000;
    public const uint DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const uint DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSW
    {
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public IntPtr LpszMenuName;
        public IntPtr LpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEV_BROADCAST_DEVICEINTERFACE_W
    {
        public uint Dbcc_size;
        public uint Dbcc_devicetype;
        public uint Dbcc_reserved;
        public Guid Dbcc_classguid;
        public ushort Dbcc_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int Pt_x;
        public int Pt_y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreateWindowExW(uint dwExStyle, [MarshalAs(UnmanagedType.LPWStr)] string lpClassName, [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr HInstance, IntPtr lpParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterDeviceNotification(IntPtr handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);
}