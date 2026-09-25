using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// libobjc runtime P/Invoke surface used to drive IOUSBHost.framework (Apple's official
/// user-space USB API) directly from C# without a native bridge library.
/// <para>libobjc 运行时 P/Invoke 表面，用于在无原生桥接库的情况下从 C# 直接驱动
/// IOUSBHost.framework（苹果官方用户空间 USB API）。</para>
/// </summary>
/// <remarks>
/// ABI constraints validated against the macOS 26 SDK headers (IOUSBHost.framework):
/// <list type="bullet">
/// <item>Every method invoked here returns a scalar (object pointer / BOOL / NSUInteger /
/// io_service_t) — never an ObjC struct. The x86-64 <c>objc_msgSend_stret</c> variant is
/// therefore not required.</item>
/// <item><c>IOUSBDeviceRequest</c> is an 8-byte packed struct (IOKit USBSpec.h); on both
/// arm64 and x86-64 SysV ABIs it is passed by value in a single INTEGER register, so a
/// blittable readonly struct parameter marshals correctly.</item>
/// <item><c>io_service_t</c> is a 32-bit mach port name (<c>natural_t</c>); declaring it as
/// <see cref="uint"/> matches the register ABI. Never widen it to <see cref="IntPtr"/> —
/// garbage in the high 4 bytes corrupts the call.</item>
/// <item>ObjC <c>BOOL</c> is a byte-sized value returned in w0/al; declared here as
/// <see cref="byte"/> and compared against zero to avoid arm64/x64 typedef differences.</item>
/// <item>The interestHandler / queue parameters accept nil; nil is passed as
/// <see cref="IntPtr.Zero"/> like any other object argument.</item>
/// </list>
/// <para>ABI 约束已对照 macOS 26 SDK 头文件（IOUSBHost.framework）核实：</para>
/// <list type="bullet">
/// <item>此处调用的所有方法均返回标量（对象指针 / BOOL / NSUInteger / io_service_t），
/// 绝不返回 ObjC 结构体，因此不需要 x86-64 的 <c>objc_msgSend_stret</c> 变体。</item>
/// <item><c>IOUSBDeviceRequest</c> 是 8 字节 packed 结构（IOKit USBSpec.h）；arm64 与
/// x86-64 SysV ABI 均按值在单个 INTEGER 寄存器中传递，blittable readonly struct 参数
/// 可正确封送。</item>
/// <item><c>io_service_t</c> 是 32 位 mach port 名（<c>natural_t</c>），声明为
/// <see cref="uint"/> 与寄存器 ABI 一致。切勿加宽为 <see cref="IntPtr"/>——高 4 字节
/// 的垃圾值会破坏调用。</item>
/// <item>ObjC <c>BOOL</c> 是字节大小，经 w0/al 返回；此处声明为 <see cref="byte"/> 并与
/// 零比较，避免 arm64/x64 的 typedef 差异。</item>
/// <item>interestHandler / queue 参数可为 nil；nil 与其他对象参数一样以
/// <see cref="IntPtr.Zero"/> 传入。</item>
/// </list>
/// </remarks>
internal static partial class ObjcRuntime
{
    /// <summary>
    /// Absolute path of the ObjC runtime. <c>/usr/lib/libobjc.A.dylib</c> always exists on
    /// every macOS release (plain <c>"libobjc"</c> is not resolvable by name for P/Invoke
    /// because the dylib lives in the shared cache).
    /// <para>ObjC 运行时的绝对路径。<c>/usr/lib/libobjc.A.dylib</c> 在每个 macOS 发行版
    /// 上都存在（由于 dylib 位于共享缓存中，P/Invoke 无法按 <c>"libobjc"</c> 名称解析）。</para>
    /// </summary>
    public const string LibObjc = "/usr/lib/libobjc.A.dylib";

    /// <summary>
    /// id objc_getClass(const char *name); returns nil (Zero) when the class is absent.
    /// <para>id objc_getClass(const char *name)；类不存在时返回 nil（Zero）。</para>
    /// </summary>
    [LibraryImport(LibObjc)]
    public static partial IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>
    /// SEL sel_registerName(const char *name); registers or looks up a selector.
    /// <para>SEL sel_registerName(const char *name)；注册或查找选择器。</para>
    /// </summary>
    [LibraryImport(LibObjc)]
    public static partial IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(LibObjc)]
    public static partial IntPtr objc_retain(IntPtr obj);

    [LibraryImport(LibObjc)]
    public static partial void objc_release(IntPtr obj);

    /// <summary>-(void)method — e.g. destroy, close. <para>-(void)method——如 destroy。</para></summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr sel);

    /// <summary>-(id / void *)method — object or raw pointer getters.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendObject(IntPtr receiver, IntPtr sel);

    /// <summary>-(NSUInteger)method — NSUInteger getters.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial ulong MsgSendUlong(IntPtr receiver, IntPtr sel);

    /// <summary>-(io_service_t)method — 32-bit mach port getters (e.g. ioService).</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial uint MsgSendUint(IntPtr receiver, IntPtr sel);

    /// <summary>-(void)method:(NSUInteger)value — e.g. setLength:.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid_Ulong(IntPtr receiver, IntPtr sel, ulong arg);

    /// <summary>-(id)method:(NSUInteger)value — e.g. dataWithCapacity:.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendObject_Ulong(IntPtr receiver, IntPtr sel, ulong arg);

    /// <summary>-(id)method:(NSUInteger)value error:(NSError**) — e.g. copyPipeWithAddress:error:.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendObject_Ulong_Error(IntPtr receiver, IntPtr sel, ulong arg, out IntPtr error);

    /// <summary>-(BOOL)methodWithError:(NSError**) — e.g. resetWithError:.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial byte MsgSendBool_OutErr(IntPtr receiver, IntPtr sel, out IntPtr error);

    /// <summary>-(BOOL)selectAlternateSetting:(NSUInteger)alternateSetting error:(NSError**).</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial byte MsgSendBool_Ulong_OutErr(IntPtr receiver, IntPtr sel, ulong arg, out IntPtr error);

    /// <summary>-(BOOL)configureWithValue:(NSUInteger)value matchInterfaces:(BOOL)matchInterfaces error:(NSError**).</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial byte MsgSendBool_Ulong_Bool_OutErr(IntPtr receiver, IntPtr sel, ulong value, byte matchInterfaces, out IntPtr error);

    /// <summary>-(id)initWithIOService:(uint)ioService options:(NSUInteger)options queue:(dispatch_queue_t)queue error:(NSError**)error interestHandler:(id)interestHandler.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendObject_Init(IntPtr receiver, IntPtr sel, uint ioService, ulong options, IntPtr queue, out IntPtr error, IntPtr interestHandler);

    /// <summary>-(BOOL)sendIORequestWithData:(NSMutableData*)data bytesTransferred:(NSUInteger*)bytesTransferred completionTimeout:(double)completionTimeout error:(NSError**)error.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial byte MsgSendBool_IO(IntPtr receiver, IntPtr sel, IntPtr data, ref ulong bytesTransferred, double completionTimeout, out IntPtr error);

    /// <summary>-(BOOL)sendControlRequest:(IOUSBDeviceRequest)request data:(NSMutableData*)data bytesTransferred:(NSUInteger*)bytesTransferred completionTimeout:(double)completionTimeout error:(NSError**)error.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial byte MsgSendBool_Control(IntPtr receiver, IntPtr sel, UsbHostDeviceRequest request, IntPtr data, ref ulong bytesTransferred, double completionTimeout, out IntPtr error);

    /// <summary>-(BOOL)sendDeviceRequest:(IOUSBDeviceRequest)request data:(NSMutableData*)data bytesTransferred:(NSUInteger*)bytesTransferred completionTimeout:(double)completionTimeout error:(NSError**)error.</summary>
    [LibraryImport(LibObjc, EntryPoint = "objc_msgSend")]
    public static partial byte MsgSendBool_DeviceRequest(IntPtr receiver, IntPtr sel, UsbHostDeviceRequest request, IntPtr data, ref ulong bytesTransferred, double completionTimeout, out IntPtr error);

    /// <summary>
    /// IOUSBDeviceRequest — the 8-byte packed setup packet (IOKit USBSpec.h) passed by
    /// value to sendControlRequest / sendDeviceRequest.
    /// <para>IOUSBDeviceRequest——8 字节 packed 建立包（IOKit USBSpec.h），按值传给
    /// sendControlRequest / sendDeviceRequest。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly struct UsbHostDeviceRequest
    {
        /// <summary>bmRequestType — direction/type/recipient byte. <para>bmRequestType。</para></summary>
        public readonly byte BmRequestType;

        /// <summary>bRequest — request code. <para>bRequest——请求码。</para></summary>
        public readonly byte BRequest;

        /// <summary>wValue — little-endian 16-bit value. <para>wValue。</para></summary>
        public readonly ushort WValue;

        /// <summary>wIndex — little-endian 16-bit index. <para>wIndex。</para></summary>
        public readonly ushort WIndex;

        /// <summary>wLength — data stage length. <para>wLength——数据阶段长度。</para></summary>
        public readonly ushort WLength;

        /// <summary>
        /// Builds a setup packet from USB 2.0 §9.3 fields.
        /// <para>由 USB 2.0 §9.3 字段构造建立包。</para>
        /// </summary>
        public UsbHostDeviceRequest(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, ushort wLength)
        {
            BmRequestType = bmRequestType;
            BRequest = bRequest;
            WValue = wValue;
            WIndex = wIndex;
            WLength = wLength;
        }
    }
}