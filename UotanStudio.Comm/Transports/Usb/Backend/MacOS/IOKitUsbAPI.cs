using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// P/Invoke surface for the IOKit.framework classic (MIG) API.
/// <para>IOKit.framework 经典（MIG）API 的 P/Invoke 表面。</para>
/// </summary>
/// <remarks>
/// The macOS backend targets the IOKit classic API (loadable on every macOS
/// release) because IOUSBHost.framework is unloadable on current macOS — its
/// main binary is absent and only a BridgeSupport stub remains. Method
/// pointers are read via the double indirection validated on real hardware
/// (vtable pointer, then method slot); the <c>Offset_*</c> constants below
/// match the verified layout, with IUnknown at offsets 1/3 behind a leading
/// pseudo-vtable slot. Do NOT "normalize" them to a standard IUnknown 0/1/2
/// layout — that dereferences the wrong slot and SIGSEGVs the process.
/// Device-level <c>USBDeviceOpen</c> is deliberately never called (follows
/// adb usb_osx.cc). See README "Platform Notes (macOS IOKit backend)".
/// <para>macOS 后端面向 IOKit 经典 API（每个 macOS 发行版均可加载），因为
/// IOUSBHost.framework 在当前 macOS 上不可加载——主二进制缺失，仅剩 BridgeSupport
/// 桩。方法指针按真实硬件验证的双重解引用读取（先读 vtable 指针，再读方法槽位）；
/// 下方 <c>Offset_*</c> 常量与已验证布局一致，IUnknown 位于前导伪 vtable 槽之后的
/// 偏移 1/3。切勿"规范化"为标准 IUnknown 0/1/2 布局——那会解引用错误槽位并使进程
/// SIGSEGV。刻意不调用设备级 <c>USBDeviceOpen</c>（遵循 adb usb_osx.cc）。
/// 详见 README「平台注意事项（macOS IOKit 后端）」。</para>
/// </remarks>
internal static partial class IOKitUsbAPI
{
    public const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string Libc = "libc";

    [LibraryImport(IOKit)]
    public static partial IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(IOKit)]
    public static partial int IOServiceGetMatchingServices(IntPtr masterPort, IntPtr matching, out IntPtr existing);

    [LibraryImport(IOKit)]
    public static partial IntPtr IOServiceGetMatchingService(IntPtr masterPort, IntPtr matching);

    [LibraryImport(IOKit)]
    public static partial uint IOIteratorNext(IntPtr iterator);

    [LibraryImport(IOKit)]
    public static partial int IOObjectRelease(IntPtr obj);

    [LibraryImport(IOKit)]
    public static partial int IOObjectRetain(IntPtr obj);

    [LibraryImport(IOKit)]
    public static partial IntPtr IORegistryEntryCreateCFProperty(IntPtr entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(IOKit)]
    public static partial int IORegistryEntryCreateCFProperties(IntPtr entry, out IntPtr properties, IntPtr allocator, uint options);

    [LibraryImport(IOKit)]
    public static partial int IORegistryEntryGetLocationInPlane(IntPtr entry, [MarshalAs(UnmanagedType.LPStr)] string plane, IntPtr location);

    [LibraryImport(IOKit)]
    public static partial int IORegistryEntryGetPath(IntPtr entry, [MarshalAs(UnmanagedType.LPStr)] string plane, IntPtr path);

    [LibraryImport(IOKit)]
    public static partial int IORegistryEntryGetName(IntPtr entry, IntPtr name);

    [LibraryImport(IOKit)]
    public static partial ulong IORegistryEntryGetRegistryEntryID(uint entry);

    [LibraryImport(IOKit)]
    public static partial int IOCreatePlugInInterfaceForService(
        IntPtr service,
        IntPtr pluginType,
        IntPtr interfaceType,
        ref IntPtr theInterface,
        ref int theScore);

    [LibraryImport(IOKit)]
    public static partial int IOCreatePlugInInterfaceForService(
        IntPtr service,
        Guid pluginType,
        Guid interfaceType,
        out IntPtr theInterface,
        out int theScore);

    [LibraryImport(IOKit)]
    public static partial int IODestroyPlugInInterface(ref IntPtr theInterface);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(IntPtr obj);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFRetain(IntPtr obj);

    [LibraryImport(CoreFoundation)]
    public static partial long CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPStr)] string cStr, int encoding);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFNumberCreate(IntPtr alloc, int theType, IntPtr valuePtr);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFPropertyListCreateWithData(IntPtr alloc, IntPtr data, uint options, out int format, out IntPtr error);

    [LibraryImport(CoreFoundation)]
    public static partial void CFShow(IntPtr obj);

    [LibraryImport(Libc, EntryPoint = "free")]
    public static partial void Free(IntPtr ptr);

    public const int KCFStringEncodingUTF8 = 0x08000100;
    public const int KCFStringEncodingMacRoman = 0;

    public const int KIOReturnSuccess = 0;
    public const int KIOReturnError = unchecked((int)0xE00002BC);
    public const int KIOReturnNoDevice = unchecked((int)0xE00002C0);
    public const int KIOReturnNotPrivileged = unchecked((int)0xE00002C1);
    public const int KIOReturnBadArgument = unchecked((int)0xE00002C2);
    public const int KIOReturnExclusiveAccess = unchecked((int)0xE00002C5);
    public const int KIOReturnInternalError = unchecked((int)0xE00002C9);
    public const int KIOReturnNotOpen = unchecked((int)0xE00002CD);
    public const int KIOReturnBusy = unchecked((int)0xE00002D5);
    public const int KIOReturnTimeout = unchecked((int)0xE00002D6);
    public const int KIOReturnNotPermitted = unchecked((int)0xE00002E2);
    public const int KIOReturnAborted = unchecked((int)0xE00002EB);
    public const int KIOReturnNotResponding = unchecked((int)0xE00002ED);

    /// <summary>
    /// Renders one IOReturn as its symbolic name plus code, so an open failure names the kernel's refusal instead of
    /// an opaque hex number.
    /// <para>把一个 IOReturn 渲染为符号名加代码，使打开失败写出内核的拒绝原因而不是不透明的十六进制数。</para>
    /// </summary>
    /// <param name="kr">The IOReturn value. <para>IOReturn 值。</para></param>
    /// <returns>Symbolic name and code, with a short explanation for the open-related codes.
    /// <para>符号名与代码；与打开相关的代码附带简短说明。</para></returns>
    internal static string DescribeIoReturn(int kr) => kr switch
    {
        KIOReturnSuccess => "kIOReturnSuccess (0x00000000)",
        KIOReturnError => "kIOReturnError (0xE00002BC)",
        KIOReturnExclusiveAccess =>
            "kIOReturnExclusiveAccess (0xE00002C5): another user client already owns this device",
        KIOReturnNotPrivileged =>
            "kIOReturnNotPrivileged (0xE00002C1): this process is not allowed to open the device",
        KIOReturnNotPermitted =>
            "kIOReturnNotPermitted (0xE00002E2): the device rejected the user client",
        KIOReturnInternalError =>
            "kIOReturnInternalError (0xE00002C9): the interface is owned by a system driver",
        KIOReturnNotOpen =>
            "kIOReturnNotOpen (0xE00002CD): the device user client was not open",
        KIOReturnBadArgument => "kIOReturnBadArgument (0xE00002C2)",
        KIOReturnBusy => "kIOReturnBusy (0xE00002D5)",
        KIOReturnNoDevice => "kIOReturnNoDevice (0xE00002C0): the device is gone",
        KIOReturnNotResponding => "kIOReturnNotResponding (0xE00002ED)",
        KIOReturnTimeout => "kIOReturnTimeout (0xE00002D6)",
        _ => $"IOReturn 0x{kr:X8}",
    };

    public const uint KIORegistryIterateRecursively = 0x00000001;
    public const uint KIORegistryIterateParents = 0x00000002;

    public const byte USB_DESCRIPTOR_TYPE_CONFIGURATION = 2;
    public const byte USB_DESCRIPTOR_TYPE_INTERFACE = 4;
    public const byte USB_DESCRIPTOR_TYPE_ENDPOINT = 5;

    public const string IOUSBDeviceClassName = "IOUSBDevice";

    /// <summary>
    /// Class name of the per-port services that parent every attached USB device, claimed
    /// or not. <c>IOUSBHostPort</c> is not a matchable class name on macOS 27 (it matches
    /// nothing); <c>AppleUSBHostPort</c> matches the ports.
    /// <para>承载每个已连接 USB 设备（无论是否有驱动认领）的端口服务类名。在 macOS 27 上
    /// <c>IOUSBHostPort</c> 不是可匹配的类名（匹配不到任何对象）；<c>AppleUSBHostPort</c>
    /// 才能匹配端口。</para>
    /// </summary>
    public const string AppleUsbHostPortClassName = "AppleUSBHostPort";

    public const string IOServicePlane = "IOService";

    public static readonly Guid KIOUSBDeviceUserClientTypeID = new("9d7d2100-ba54-11d4-8113-0005020c020c");
    public static readonly Guid KIOUSBDeviceInterfaceID = new("5c3a030d-27d1-11d4-9d10-0005020c020c");
    public static readonly Guid KIOCFPlugInInterfaceID = new("c244e858-109c-11d4-91d4-0050e4c6426f");
    public static readonly Guid KIOUSBInterfaceUserClientTypeID = new("2d9786c6-9ef3-11d4-ad51-000a27052861");
    public static readonly Guid KIOUSBInterfaceInterfaceID190 = new("d44fd2f8-002d-11d6-8e5e-000a27052861");

    public const int Ok = 0;

    public const ushort KIOUSBFindInterfaceDontCare = 0xFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct IOUSBFindInterfaceRequest
    {
        public ushort BInterfaceClass;
        public ushort BInterfaceSubClass;
        public ushort BInterfaceProtocol;
        public ushort BAlternateSetting;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int QueryInterfaceDelegate(IntPtr self, Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint ReleaseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBDeviceOpenDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBDeviceCloseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBDeviceResetDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBGetDeviceVendorDelegate(IntPtr self, out ushort devVendor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBGetDeviceProductDelegate(IntPtr self, out ushort devProduct);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBGetSerialNumberStringIndexDelegate(IntPtr self, out byte serialIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBDeviceCreateInterfaceIteratorDelegate(IntPtr self, ref IOUSBFindInterfaceRequest request, out IntPtr iterator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBGetConfigurationDelegate(IntPtr self, out byte configNumber);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBSetConfigurationDelegate(IntPtr self, byte configNumber);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int DeviceRequestDelegate(IntPtr self, ref IOUSBDeviceRequest request);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBInterfaceOpenDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int USBInterfaceCloseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetNumEndpointsDelegate(IntPtr self, out byte numEndpoints);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetPipePropertiesDelegate(IntPtr self, byte pipeRef, out byte direction, out byte number, out byte transferType, out ushort maxPacketSize, out byte interval);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ClearPipeStallBothEndsDelegate(IntPtr self, byte pipeRef);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ReadPipeTODelegate(IntPtr self, byte pipeRef, IntPtr data, ref uint size, uint noDataTimeout, uint completionTimeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WritePipeTODelegate(IntPtr self, byte pipeRef, IntPtr data, uint size, uint noDataTimeout, uint completionTimeout);

    public const int Offset_Plugin_QueryInterface = 1;
    public const int Offset_Plugin_Release = 3;

    public const int Offset_IUnknown_QueryInterface = 1;
    public const int Offset_IUnknown_Release = 3;

    public const int Offset_DeviceRequest = 7;
    public const int Offset_USBDeviceOpen = 14;
    public const int Offset_USBDeviceClose = 15;
    public const int Offset_USBGetDeviceVendor = 16;
    public const int Offset_USBGetDeviceProduct = 17;
    public const int Offset_USBGetSerialNumberStringIndex = 21;
    public const int Offset_USBGetConfiguration = 24;
    public const int Offset_USBSetConfiguration = 25;
    public const int Offset_USBDeviceCreateInterfaceIterator = 26;
    public const int Offset_USBDeviceReset = 27;

    public const int Offset_USBInterfaceOpen = 8;
    public const int Offset_USBInterfaceClose = 9;
    public const int Offset_GetNumEndpoints = 17;
    public const int Offset_GetPipeProperties = 18;
    public const int Offset_ClearPipeStallBothEnds = 25;
    public const int Offset_ReadPipe = 26;
    public const int Offset_WritePipe = 27;
    public const int Offset_ReadPipeTO = 28;
    public const int Offset_WritePipeTO = 29;

    public static T GetDelegate<T>(IntPtr self, int offset) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(self);
        var methodPtr = Marshal.ReadIntPtr(vtable, offset * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    [LibraryImport(IOKit)]
    public static partial IntPtr IORegistryEntryFromPath(IntPtr masterPort, [MarshalAs(UnmanagedType.LPStr)] string path);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IOUSBDeviceRequest
    {
        public byte BmRequestType;
        public byte BRequest;
        public ushort WValue;
        public ushort WIndex;
        public ushort WLength;
        public IntPtr PData;
        public uint WLenDone;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct UsbConfigurationDescriptor
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct UsbEndpointDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public byte BEndpointAddress;
        public byte BmAttributes;
        public ushort WMaxPacketSize;
        public byte BInterval;
    }
}