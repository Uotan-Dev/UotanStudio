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
internal static class IOKitUsbAPI
{
    public const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string Libc = "libc";

    // ---- Service matching / iteration ----
    // CFMutableDictionaryRef IOServiceMatching(const char *name);
    // Returns a +1 reference that IOServiceGetMatchingServices consumes.
    [DllImport(IOKit)]
    public static extern IntPtr IOServiceMatching(string name);

    // kern_return_t IOServiceGetMatchingServices(mach_port_t masterPort,
    //   CFDictionaryRef matching, io_iterator_t *existing);
    [DllImport(IOKit)]
    public static extern int IOServiceGetMatchingServices(IntPtr masterPort, IntPtr matching, out IntPtr existing);

    // kern_return_t IOServiceGetMatchingService(mach_port_t masterPort,
    //   CFDictionaryRef matching);
    // Consumes the matching dictionary. Returns the first match or 0.
    [DllImport(IOKit)]
    public static extern IntPtr IOServiceGetMatchingService(IntPtr masterPort, IntPtr matching);

    // io_object_t IOIteratorNext(io_iterator_t iterator);
    // io_object_t and io_iterator_t are both mach_port_t (unsigned int, 32-bit).
    // Using IntPtr (8 bytes on 64-bit) would pass garbage in the high 4 bytes
    // and the native function would read a wrong iterator value, returning 0.
    // <para>io_object_t 和 io_iterator_t 均为 mach_port_t（unsigned int，32 位）。
    // 用 IntPtr（64 位上 8 字节）会在高 4 字节传垃圾，native 函数读到错误迭代器值
    // 而返回 0。</para>
    [DllImport(IOKit)]
    public static extern uint IOIteratorNext(IntPtr iterator);

    // kern_return_t IOObjectRelease(io_object_t object);
    [DllImport(IOKit)]
    public static extern int IOObjectRelease(IntPtr obj);

    // kern_return_t IOObjectRetain(io_object_t object);
    [DllImport(IOKit)]
    public static extern int IOObjectRetain(IntPtr obj);

    // ---- Registry properties ----
    // CFTypeRef IORegistryEntryCreateCFProperty(io_registry_entry_t entry,
    //   CFStringRef key, CFAllocatorRef allocator, IOOptionBits options);
    // Returns a +1 reference (caller must CFRelease) or NULL.
    [DllImport(IOKit)]
    public static extern IntPtr IORegistryEntryCreateCFProperty(IntPtr entry, IntPtr key, IntPtr allocator, uint options);

    // kern_return_t IORegistryEntryCreateCFProperties(io_registry_entry_t entry,
    //   CFMutableDictionaryRef *properties, CFAllocatorRef allocator, IOOptionBits options);
    // Fills *properties with a +1 dictionary of all properties.
    [DllImport(IOKit)]
    public static extern int IORegistryEntryCreateCFProperties(IntPtr entry, out IntPtr properties, IntPtr allocator, uint options);

    // kern_return_t IORegistryEntryGetLocationInPlane(io_registry_entry_t entry,
    //   const io_name_t plane, io_name_t location);
    [DllImport(IOKit)]
    public static extern int IORegistryEntryGetLocationInPlane(IntPtr entry, string plane, byte[] location);

    // kern_return_t IORegistryEntryGetPath(io_registry_entry_t entry,
    //   const io_name_t plane, io_string_t path);
    [DllImport(IOKit)]
    public static extern int IORegistryEntryGetPath(IntPtr entry, string plane, byte[] path);

    // kern_return_t IORegistryEntryGetName(io_registry_entry_t entry, io_name_t name);
    [DllImport(IOKit)]
    public static extern int IORegistryEntryGetName(IntPtr entry, byte[] name);

    // uint64_t IORegistryEntryGetRegistryEntryID(io_registry_entry_t entry);
    // io_registry_entry_t is mach_port_t (unsigned int, 32-bit). Passing IntPtr
    // (8 bytes on 64-bit) would feed garbage in the high 4 bytes and the native
    // function would read a wrong entry handle, returning 0.
    // <para>io_registry_entry_t 为 mach_port_t（unsigned int，32 位）。传 IntPtr
    // （64 位上 8 字节）会在高 4 字节传垃圾，native 函数读到错误项句柄而返回 0。</para>
    [DllImport(IOKit)]
    public static extern ulong IORegistryEntryGetRegistryEntryID(uint entry);

    // ---- Plug-in interface (COM-vtable) ----
    // kern_return_t IOCreatePlugInInterfaceForService(io_service_t service,
    //   CFUUIDRef pluginType, CFUUIDRef interfaceType,
    //   IOCFPlugInInterface ***theInterface, SInt32 *theScore);
    // Returns a +1 plug-in interface in *theInterface.
    // The IntPtr form is kept for callers that already hold a CFUUIDRef; the
    // Guid overload below lets .NET marshal a Guid into a CFUUIDRef directly
    // (the exact calling shape SharpFastboot's macOS backend uses).
    // <para>以 Guid 形式调用时，.NET 将 Guid marshal 为 CFUUIDRef（SharpFastboot
    // 的 macOS 后端使用的正是该调用形）。IntPtr 形保留给已持有 CFUUIDRef 的调用方。</para>
    [DllImport(IOKit)]
    public static extern int IOCreatePlugInInterfaceForService(
        IntPtr service,
        IntPtr pluginType,
        IntPtr interfaceType,
        ref IntPtr theInterface,
        ref int theScore);

    [DllImport(IOKit)]
    public static extern int IOCreatePlugInInterfaceForService(
        IntPtr service,
        Guid pluginType,
        Guid interfaceType,
        out IntPtr theInterface,
        out int theScore);

    // kern_return_t IODestroyPlugInInterface(IOCFPlugInInterface **theInterface);
    [DllImport(IOKit)]
    public static extern int IODestroyPlugInInterface(ref IntPtr theInterface);

    // ---- CoreFoundation helpers ----
    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr obj);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFRetain(IntPtr obj);

    [DllImport(CoreFoundation)]
    public static extern long CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);

    // CFStringRef CFStringCreateWithCString(CFAllocatorRef alloc,
    //   const char *cStr, CFStringEncoding encoding);
    [DllImport(CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, int encoding);

    // CFTypeID CFGetTypeID(CFTypeRef cf);
    [DllImport(CoreFoundation)]
    public static extern IntPtr CFGetTypeID(IntPtr cf);

    // CFNumberRef CFNumberCreate(CFAllocatorRef alloc, CFNumberType theType, const void *valuePtr);
    [DllImport(CoreFoundation)]
    public static extern IntPtr CFNumberCreate(IntPtr alloc, int theType, IntPtr valuePtr);

    // CFPropertyListRef CFPropertyListCreateWithData(CFAllocatorRef alloc,
    //   CFDataRef data, CFOptionFlags options, CFPropertyListFormat *format, CFErrorRef *error);
    [DllImport(CoreFoundation)]
    public static extern IntPtr CFPropertyListCreateWithData(IntPtr alloc, IntPtr data, uint options, out int format, out IntPtr error);

    // void CFShow(CFTypeRef obj); // prints a CF object to stderr for debugging
    [DllImport(CoreFoundation)]
    public static extern void CFShow(IntPtr obj);

    // malloc/free for IORegistryEntryCreateCFProperty results
    [DllImport(Libc, EntryPoint = "free")]
    public static extern void Free(IntPtr ptr);

    // ---- Constants ----
    public const int kCFStringEncodingUTF8 = 0x08000100;
    public const int kCFStringEncodingMacRoman = 0;

    public const int kIOReturnSuccess = 0;
    public const int kIOReturnNoDevice = unchecked((int)0xE00002C0);
    public const int kIOReturnAborted = unchecked((int)0xE00002EB);
    public const int kIOReturnTimeout = unchecked((int)0xE00002D6);
    public const int kIOReturnNotResponding = unchecked((int)0xE00002ED);
    public const int kIOReturnExclusiveAccess = unchecked((int)0xE00002E5);
    public const int kIOReturnError = unchecked((int)0xE0000001);

    // IOOptionBits for IORegistryEntryCreateCFProperty / CreateCFProperties
    public const uint kIORegistryIterateRecursively = 0x00000001;
    public const uint kIORegistryIterateParents = 0x00000002;

    // USB descriptor types
    public const byte USB_DESCRIPTOR_TYPE_CONFIGURATION = 2;
    public const byte USB_DESCRIPTOR_TYPE_INTERFACE = 4;
    public const byte USB_DESCRIPTOR_TYPE_ENDPOINT = 5;

    // The IOUSBDevice service class name used with IOServiceMatching.
    public const string IOUSBDeviceClassName = "IOUSBDevice";

    // The IOService plane name passed to IORegistryEntryGetPath.
    public const string IOServicePlane = "IOService";

    // ---- COM UUIDs (verified in SharpFastboot's macOS backend) ----
    // <para>COM UUID（已在 SharpFastboot 的 macOS 后端验证）。</para>
    public static readonly Guid kIOUSBDeviceUserClientTypeID = new("9d7d2100-ba54-11d4-8113-0005020c020c");
    public static readonly Guid kIOUSBDeviceInterfaceID = new("5c3a030d-27d1-11d4-9d10-0005020c020c");
    public static readonly Guid kIOCFPlugInInterfaceID = new("c244e858-109c-11d4-91d4-0050e4c6426f");
    public static readonly Guid kIOUSBInterfaceUserClientTypeID = new("2d9786c6-9ef3-11d4-ad51-000a27052861");
    public static readonly Guid kIOUSBInterfaceInterfaceID190 = new("d44fd2f8-002d-11d6-8e5e-000a27052861");

    public const int S_OK = 0;

    // IOUSBFindInterfaceRequest b* fields use kIOUSBFindInterfaceDontCare (0xFFFF)
    // to match any value.
    // <para>IOUSBFindInterfaceRequest 的 b* 字段用 kIOUSBFindInterfaceDontCare
    // （0xFFFF）匹配任意值。</para>
    public const ushort kIOUSBFindInterfaceDontCare = 0xFFFF;

    // ---- IOUSBFindInterfaceRequest (used by USBDeviceCreateInterfaceIterator) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct IOUSBFindInterfaceRequest
    {
        public ushort bInterfaceClass;
        public ushort bInterfaceSubClass;
        public ushort bInterfaceProtocol;
        public ushort bAlternateSetting;
    }

    // ---- COM-vtable delegates (IOCFPlugInInterface / IOUSBDeviceInterface /
    // IOUSBInterfaceInterface), verified offsets from SharpFastboot ----
    // <para>COM-vtable 委托（IOCFPlugInInterface / IOUSBDeviceInterface /
    // IOUSBInterfaceInterface），已验证偏移取自 SharpFastboot。</para>

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

    // VTable offsets, matching the layout verified on current macOS by the
    // pre-rewrite backend (and SharpFastboot's macOS implementation). The
    // plug-in objects obtained from IOCreatePlugInInterfaceForService expose a
    // vtable whose slot 0 is NOT QueryInterface — the IUnknown methods sit at
    // offsets 1/3 (there is a leading pseudo-vtable slot), and the device /
    // interface methods follow at the offsets below. These values were
    // validated on real macOS hardware (USBDeviceOpen is reached and returns
    // kIOReturnNoSpace rather than crashing), so they MUST NOT be "normalized"
    // to a standard IUnknown 0/1/2 layout — doing so dereferences the wrong
    // slot and SIGSEGVs the process.
    // <para>vtable 偏移与重写前后端（及 SharpFastboot 的 macOS 实现）在当前 macOS
    // 上验证过的布局一致。IOCreatePlugInInterfaceForService 得到的插件对象暴露的
    // vtable 中槽位 0 并非 QueryInterface——IUnknown 方法位于偏移 1/3（前有一个
    // 伪 vtable 槽），设备/接口方法位于下列偏移。这些值已在真实 macOS 硬件上验证
    // （USBDeviceOpen 真实到达并返回 kIOReturnNoSpace 而非崩溃），因此绝不能
    // "规范化"为标准 IUnknown 0/1/2 布局——那样会解引用错误槽位并使进程 SIGSEGV。</para>

    // VTable offsets for IOCFPlugInInterface.
    public const int Offset_Plugin_QueryInterface = 1;
    public const int Offset_Plugin_Release = 3;

    // VTable offsets for common IUnknown (shared by interface & device COM objects).
    public const int Offset_IUnknown_QueryInterface = 1;
    public const int Offset_IUnknown_Release = 3;

    // VTable offsets for IOUSBDeviceInterface.
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

    // VTable offsets for IOUSBInterfaceInterface (190+).
    public const int Offset_USBInterfaceOpen = 8;
    public const int Offset_USBInterfaceClose = 9;
    public const int Offset_GetNumEndpoints = 17;
    public const int Offset_GetPipeProperties = 18;
    public const int Offset_ClearPipeStallBothEnds = 25;
    public const int Offset_ReadPipe = 26;
    public const int Offset_WritePipe = 27;
    public const int Offset_ReadPipeTO = 28;
    public const int Offset_WritePipeTO = 29;

    // Reads a vtable method pointer at `offset` (0-based slot) and wraps it into
    // the requested delegate type. The vtable itself is the first pointer pointed
    // to by `self`; each slot is one IntPtr wide. This double indirection is the
    // shape validated on macOS by the pre-rewrite backend — do not change it to a
    // single indirection based on the IOKit C headers alone.
    // <para>读取 `self` 指向的 vtable 在 `offset`（0 基槽位）处的方法指针并包装为
    // 请求的委托类型。vtable 本身是 `self` 指向的首个指针；每槽一个 IntPtr 宽。
    // 此双重解引用是重写前后端在 macOS 上验证过的调用形——请勿仅依据 IOKit C 头
    // 文件将其改为单层解引用。</para>
    public static T GetDelegate<T>(IntPtr self, int offset) where T : class
    {
        var vtable = Marshal.ReadIntPtr(self);
        var methodPtr = Marshal.ReadIntPtr(vtable, offset * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(methodPtr, typeof(T));
    }

    // io_service_t IORegistryEntryFromPath(mach_port_t mainPort, const io_string_t path);
    // Re-opens a device service from its IOService-plane path.
    // <para>从 IOService 平面路径重开设备服务。</para>
    [DllImport(IOKit)]
    public static extern IntPtr IORegistryEntryFromPath(IntPtr masterPort, string path);

    // ---- Structs ----
    [StructLayout(LayoutKind.Sequential)]
    public struct IOUSBDeviceRequest
    {
        public byte bmRequestType;
        public byte bRequest;
        public ushort wValue;
        public ushort wIndex;
        public ushort wLength;
        public IntPtr pData;
        public uint wLenDone;
    }

    // USB descriptor headers (USB 2.0 spec), used to walk the configuration descriptor.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UsbConfigurationDescriptor
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UsbEndpointDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bEndpointAddress;
        public byte bmAttributes;
        public ushort wMaxPacketSize;
        public byte bInterval;
    }
}
