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

    /// <summary>
    /// Creates a plug-in interface for a service using CoreFoundation UUID references.
    /// <para>使用 CoreFoundation UUID 引用为服务创建插件接口。</para>
    /// </summary>
    /// <remarks>
    /// <c>IOCreatePlugInInterfaceForService</c> takes <c>CFUUIDRef</c> — a pointer to a CoreFoundation
    /// object — not a by-value 128-bit UUID. Marshalling <see cref="Guid"/> by value hands the kernel
    /// a stack address where it expects an object, and the call faults the process; both arguments must
    /// therefore be real <c>CFUUIDRef</c> values created by <see cref="CreateCoreFoundationUuid"/>.
    /// <para><c>IOCreatePlugInInterfaceForService</c> 接收的是 <c>CFUUIDRef</c>（CoreFoundation 对象指针），
    /// 而不是按值传递的 128 位 UUID。按值封送 <see cref="Guid"/> 会把栈地址当作对象交给内核，
    /// 该调用随即让进程崩溃；因此两个参数都必须是由 <see cref="CreateCoreFoundationUuid"/> 创建的真实
    /// <c>CFUUIDRef</c>。</para>
    /// </remarks>
    [LibraryImport(IOKit, EntryPoint = "IOCreatePlugInInterfaceForService")]
    public static partial int IOCreatePlugInInterfaceForServiceWithUuid(
        IntPtr service,
        IntPtr pluginType,
        IntPtr interfaceType,
        out IntPtr theInterface,
        out int theScore);

    /// <summary>
    /// Creates a <c>CFUUIDRef</c> from the textual form of <paramref name="uuid"/>; the caller releases it.
    /// <para>由 <paramref name="uuid"/> 的文本形式创建 <c>CFUUIDRef</c>；调用方负责释放。</para>
    /// </summary>
    /// <param name="uuid">The UUID to convert. <para>要转换的 UUID。</para></param>
    /// <returns>The CoreFoundation UUID reference, or <see cref="IntPtr.Zero"/> when creation fails.
    /// <para>CoreFoundation UUID 引用；创建失败时为 <see cref="IntPtr.Zero"/>。</para></returns>
    public static IntPtr CreateCoreFoundationUuid(Guid uuid)
    {
        // Build the 16 raw bytes directly. A round trip through the textual form is not equivalent:
        // CFUUIDCreateFromString normalizes the string, and the resulting CFUUIDBytes do not compare
        // equal to the constants the IOKit headers declare, which makes the kernel refuse the plug-in
        // with kIOReturnUnsupported. Marshalling the same 16 bytes by value is equally wrong — the
        // function takes a CFUUIDRef pointer, not a struct.
        var bytes = new byte[16];
        if (!uuid.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            return IntPtr.Zero;
        }

        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                return CFUUIDCreateWithBytes(IntPtr.Zero, pointer[0], pointer[1], pointer[2], pointer[3], pointer[4],
                    pointer[5], pointer[6], pointer[7], pointer[8], pointer[9], pointer[10], pointer[11], pointer[12],
                    pointer[13], pointer[14], pointer[15]);
            }
        }
    }

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFUUIDCreateWithBytes(
        IntPtr allocator,
        byte byte0, byte byte1, byte byte2, byte byte3,
        byte byte4, byte byte5, byte byte6, byte byte7,
        byte byte8, byte byte9, byte byte10, byte byte11,
        byte byte12, byte byte13, byte byte14, byte byte15);

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

    /// <summary>
    /// <c>kIOReturnUnsupported</c>: the service answered but cannot grant this user client right now.
    /// <para><c>kIOReturnUnsupported</c>：服务已应答，但当前无法授予该用户客户端。</para>
    /// </summary>
    /// <remarks>
    /// A bootrom node that is still settling after an interrupted configuration answers this instead of
    /// <c>kIOReturnExclusiveAccess</c>; it clears on its own within a fraction of a second.
    /// <para>在配置被中断后仍在稳定的 bootrom 节点会返回该码而非 <c>kIOReturnExclusiveAccess</c>；
    /// 它会在不到一秒内自行清除。</para>
    /// </remarks>
    public const int KIOReturnUnsupported = unchecked((int)0xE00002C7);

    /// <summary>
    /// <c>kIOUSBPipeStalled</c>: the OUT transfer reached the device and the device answered the
    /// handshake with a stall, which Qualcomm programmers do after consuming a command.
    /// <para><c>kIOUSBPipeStalled</c>：OUT 传输已到达设备，设备以 stall 作为握手应答，高通 programmer
    /// 在消费完一条命令后就是这样回应的。</para>
    /// </summary>
    /// <remarks>
    /// The bytes are transferred before the stall is reported, so this code means "written and the
    /// target already responded", not "write failed". Treating it as a failure is what made the macOS
    /// classic backend abort a configure that the device had actually accepted.
    /// <para>stall 被上报之前字节已经传输完成，因此该码表示"已写入且目标已应答"，而不是"写入失败"。
    /// 把它当作失败，正是 macOS 经典后端中止一个设备实际已接受 configure 的原因。</para>
    /// </remarks>
    public const int KIOUSBPipeStalled = unchecked((int)0xE0004051);
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
        KIOReturnUnsupported =>
            "kIOReturnUnsupported (0xE00002C7): the service cannot grant this user client yet",
        KIOUSBPipeStalled =>
            "kIOUSBPipeStalled (0xE0004051): the transfer completed and the device answered with a stall",
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

    public static readonly Guid KIOUSBDeviceUserClientTypeID = new("9dc7b780-9ec0-11d4-a54f-000a27052861");
    public static readonly Guid KIOUSBDeviceInterfaceID = new("5c8187d0-9ef3-11d4-8b45-000a27052861");
    public static readonly Guid KIOCFPlugInInterfaceID = new("c244e858-109c-11d4-91d4-0050e4c6426f");
    public static readonly Guid KIOUSBInterfaceUserClientTypeID = new("2d9786c6-9ef3-11d4-ad51-000a27052861");
    public static readonly Guid KIOUSBInterfaceInterfaceID190 = new("73c97ae8-9ef3-11d4-b1d0-000a27052861");

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

    /// <summary>
    /// <c>IUnknown::QueryInterface</c> as exposed by an IOKit plug-in interface.
    /// <para>IOKit 插件接口暴露的 <c>IUnknown::QueryInterface</c>。</para>
    /// </summary>
    /// <remarks>
    /// <paramref name="iid"/> is a <c>CFUUIDRef</c> pointer, not a by-value UUID: the plug-in dereferences
    /// it as an object. Passing a <see cref="Guid"/> here faults the process.
    /// <para><paramref name="iid"/> 是 <c>CFUUIDRef</c> 指针而非按值 UUID：插件会把它当对象解引用。
    /// 此处传 <see cref="Guid"/> 会让进程崩溃。</para>
    /// </remarks>
    /// <summary>
    /// <c>IUnknown::QueryInterface</c> as exposed by an IOKit plug-in interface.
    /// <para>IOKit 插件接口暴露的 <c>IUnknown::QueryInterface</c>。</para>
    /// </summary>
    /// <remarks>
    /// The interface ID is taken as a <c>CFUUIDBytes</c> value, not a <c>CFUUIDRef</c>: COM declares the
    /// parameter as <c>REFIID</c>, which IOKit implements as a 16-byte <c>CFUUIDBytes</c> passed by value.
    /// Passing a pointer here makes the plug-in read the struct from the wrong place, and taking the
    /// address of the reference value is what faulted the process.
    /// <para>接口 ID 按 <c>CFUUIDBytes</c> 值传递，而不是 <c>CFUUIDRef</c>：COM 把该参数声明为
    /// <c>REFIID</c>，IOKit 将其实现为按值传递的 16 字节 <c>CFUUIDBytes</c>。此处传指针会让插件从错误
    /// 位置读取结构体，而这正是先前取引用值地址导致进程崩溃的原因。</para>
    /// </remarks>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int QueryInterfaceDelegate(IntPtr self, CfUuidBytes iid, out IntPtr ppv);

    /// <summary>
    /// The 16-byte value form of a CoreFoundation UUID (<c>CFUUIDBytes</c>), laid out as
    /// <c>byte0..byte15</c> in big-endian UUID order.
    /// <para>CoreFoundation UUID 的 16 字节值形式（<c>CFUUIDBytes</c>），按大端 UUID 顺序排列为
    /// <c>byte0..byte15</c>。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CfUuidBytes
    {
        /// <summary>Creates the value form from a <see cref="Guid"/>.
        /// <para>由 <see cref="Guid"/> 创建值形式。</para></summary>
        public CfUuidBytes(Guid uuid)
        {
            var bytes = new byte[16];
            _ = uuid.TryWriteBytes(bytes, bigEndian: true, out _);
            Byte0 = bytes[0];
            Byte1 = bytes[1];
            Byte2 = bytes[2];
            Byte3 = bytes[3];
            Byte4 = bytes[4];
            Byte5 = bytes[5];
            Byte6 = bytes[6];
            Byte7 = bytes[7];
            Byte8 = bytes[8];
            Byte9 = bytes[9];
            Byte10 = bytes[10];
            Byte11 = bytes[11];
            Byte12 = bytes[12];
            Byte13 = bytes[13];
            Byte14 = bytes[14];
            Byte15 = bytes[15];
        }

        public readonly byte Byte0;
        public readonly byte Byte1;
        public readonly byte Byte2;
        public readonly byte Byte3;
        public readonly byte Byte4;
        public readonly byte Byte5;
        public readonly byte Byte6;
        public readonly byte Byte7;
        public readonly byte Byte8;
        public readonly byte Byte9;
        public readonly byte Byte10;
        public readonly byte Byte11;
        public readonly byte Byte12;
        public readonly byte Byte13;
        public readonly byte Byte14;
        public readonly byte Byte15;
    }

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

    // Offsets verified on macOS 27 (arm64e) by locating, for each method, the vtable slot whose
    // pointer-auth-stripped value equals the address the public header yields for that method. The
    // header's own macros are the only authoritative mapping: a slot index guessed from the header's
    // declaration order is wrong, and calling the wrong slot faults the process rather than failing.
    public const int Offset_DeviceRequest = 17;
    public const int Offset_USBDeviceOpen = 8;
    public const int Offset_USBDeviceClose = 9;
    public const int Offset_USBGetDeviceVendor = 13;
    public const int Offset_USBGetDeviceProduct = 14;
    public const int Offset_USBGetSerialNumberStringIndex = 20;
    public const int Offset_USBGetConfiguration = 22;
    public const int Offset_USBSetConfiguration = 23;
    public const int Offset_USBDeviceCreateInterfaceIterator = 28;
    public const int Offset_USBDeviceReset = 25;
    public const int Offset_USBDeviceReEnumerate = 37;

    public const int Offset_USBInterfaceOpen = 8;
    public const int Offset_USBInterfaceClose = 9;
    public const int Offset_GetNumEndpoints = 19;
    public const int Offset_GetPipeProperties = 26;
    public const int Offset_ClearPipeStallBothEnds = 45;
    public const int Offset_ReadPipe = 37;
    public const int Offset_WritePipe = 38;
    public const int Offset_ReadPipeTO = 39;
    public const int Offset_WritePipeTO = 40;

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