using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal class Win32API
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    public const uint FILE_DEVICE_UNKNOWN = 0x00000022;
    public const uint METHOD_BUFFERED = 0;
    public const uint FILE_READ_ACCESS = 1;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public static uint CTL_CODE(uint deviceType, uint function, uint method, uint access) => (deviceType << 16) | (access << 14) | (function << 2) | method;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr CreateFileW([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint access,
                                         uint shareMode, IntPtr securityAttributes,
                                         uint createDisposition, uint flagsAndAttributes,
                                         IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr device, uint code,
                                          byte[]? inBuffer, uint inBufferSize,
                                          byte[]? outBuffer, uint outBufferSize,
                                          out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr device, uint code,
                                          IntPtr inBuffer, uint inBufferSize,
                                          byte[]? outBuffer, uint outBufferSize,
                                          out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool WriteFile(IntPtr hFile, IntPtr buffer, uint sizeToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool ReadFile(IntPtr hFile, IntPtr buffer, uint sizeToRead, out uint bytesRead, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUID
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public GUID InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevInfoData
    {
        public uint cbSize;
        public GUID ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    // ---- advapi32 registry (used to read DeviceInterfaceGUIDs per device node) ----
    public const uint KEY_READ = 0x20019;
    public const uint REG_MULTI_SZ = 7;
    public static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(0x80000002);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, IntPtr lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegEnumKeyExW(IntPtr hKey, uint dwIndex, System.Text.StringBuilder lpName, ref uint lpcchName,
                                           IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpftLastWriteTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(IntPtr hKey);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
                                                          System.Text.StringBuilder? deviceInstanceId, uint deviceInstanceIdSize,
                                                          out uint requiredSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct USBDeviceDescriptor
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
    public struct USBDeviceConfigDescriptor
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
    public struct USBDeviceInterfaceDescriptor
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

    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public const uint DIGCF_ALLCLASSES = 0x00000004;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref GUID guid, string? enumerator, IntPtr parent, uint flag);

    // NULL class GUID overload: with DIGCF_ALLCLASSES | DIGCF_DEVICEINTERFACE it returns every
    // device interface on the system, so discovery no longer depends on a hardcoded GUID list
    // (e.g. Zadig installs WinUSB with an arbitrary interface GUID).
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flag);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                                                          ref GUID interfaceClassGuid, uint index,
                                                          ref SpDeviceInterfaceData deviceInterfaceData);

    // NULL interface-GUID overload: enumerates interfaces of every class in the set.
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                                                          IntPtr interfaceClassGuid, uint index,
                                                          ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
                                                               IntPtr deviceInterfaceDetailData,
                                                               uint detailSize,
                                                               out uint requiredSize,
                                                               IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

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
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // ---- Windows device notification (WM_DEVICECHANGE via hidden message window) ----
    public const uint WM_DEVICECHANGE = 0x0219;
    public const uint WM_QUIT = 0x0012;
    public const uint DBT_DEVICEARRIVAL = 0x8000;
    public const uint DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const uint DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_DEVICEINTERFACE_W
    {
        public uint dbcc_size;
        public uint dbcc_devicetype;
        public uint dbcc_reserved;
        public GUID dbcc_classguid;
        public ushort dbcc_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);
}
