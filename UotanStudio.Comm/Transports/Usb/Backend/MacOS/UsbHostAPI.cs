using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using static UotanStudio.Comm.Transports.Usb.Backend.MacOS.IOKitUsbAPI;
using static UotanStudio.Comm.Transports.Usb.Backend.MacOS.ObjcRuntime;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// Typed message layer over IOUSBHost.framework (Apple's official user-space USB API),
/// driven through <see cref="ObjcRuntime"/> without a native bridge library.
/// <para>基于 <see cref="ObjcRuntime"/> 的 IOUSBHost.framework（苹果官方用户空间 USB API）
/// 类型化消息层，无需原生桥接库。</para>
/// </summary>
/// <remarks>
/// Method shapes mirror the macOS 26 SDK headers exactly:
/// <list type="bullet">
/// <item><c>IOUSBHostObject.initWithIOService:options:queue:error:interestHandler:</c> —
/// initializing the user client is itself the exclusive open; there is no separate
/// <c>open</c> method. <c>destroy</c> releases the user client.</item>
/// <item><c>IOUSBHostDevice.sendDeviceRequest:data:bytesTransferred:completionTimeout:error:</c>
/// — synchronous control transfer on the default endpoint.</item>
/// <item><c>IOUSBHostInterface.copyPipeWithAddress:</c> — returns a +1 retained
/// <c>IOUSBHostPipe</c> or nil.</item>
/// <item><c>IOUSBHostPipe.sendIORequestWithData:bytesTransferred:completionTimeout:error:</c>
/// — synchronous bulk/interrupt transfer; <c>completionTimeout</c> is
/// <c>NSTimeInterval</c> (seconds), 0 = no timeout.</item>
/// </list>
/// Errors are reported as <c>NSError</c> whose <c>code</c> is the <c>IOReturn</c> value.
/// <para>方法形态与 macOS 26 SDK 头文件完全一致：初始化用户客户端即为独占打开
/// （无独立 open 方法），destroy 释放用户客户端；completionTimeout 为秒
/// （NSTimeInterval，0 表示永不超时）；错误以 NSError.code（IOReturn 值）上报。</para>
/// </remarks>
internal static partial class UsbHostAPI
{
    // ---- ObjC class names (IOUSBHost.framework exports these ObjC classes) ----
    public const string DeviceClassName = "IOUSBHostDevice";
    public const string InterfaceClassName = "IOUSBHostInterface";
    public const string NSMutableDataClassName = "NSMutableData";

    // ---- Selectors (verified against macOS 26 SDK headers) ----
    public const string SelAlloc = "alloc";
    public const string SelInitWithIOService = "initWithIOService:options:queue:error:interestHandler:";
    public const string SelDestroy = "destroy";
    public const string SelIoService = "ioService";
    public const string SelConfigurationDescriptor = "configurationDescriptor";
    public const string SelInterfaceDescriptor = "interfaceDescriptor";
    public const string SelSelectAlternateSetting = "selectAlternateSetting:error:";
    public const string SelCopyPipeWithAddress = "copyPipeWithAddress:error:";
    public const string SelSendDeviceRequest = "sendDeviceRequest:data:bytesTransferred:completionTimeout:error:";
    public const string SelSendControlRequest = "sendControlRequest:data:bytesTransferred:completionTimeout:error:";
    public const string SelSendIoRequest = "sendIORequestWithData:bytesTransferred:completionTimeout:error:";
    public const string SelReset = "resetWithError:";
    public const string SelAbort = "abortWithError:";
    public const string SelEndpointAddress = "endpointAddress";
    public const string SelDataWithLength = "dataWithLength:";
    public const string SelMutableBytes = "mutableBytes";
    public const string SelLength = "length";
    public const string SelErrorCode = "code";

    /// <summary>USB configuration descriptor bDescriptorType. <para>配置描述符类型。</para></summary>
    public const byte DescriptorTypeConfiguration = 2;
    /// <summary>USB interface descriptor bDescriptorType. <para>接口描述符类型。</para></summary>
    public const byte DescriptorTypeInterface = 4;
    /// <summary>USB endpoint descriptor bDescriptorType. <para>端点描述符类型。</para></summary>
    public const byte DescriptorTypeEndpoint = 5;

    // ---- Lazy class lookups ----

    // ObjC classes are only registered once their image is loaded. IOUSBHost's main
    // binary lives in the dyld shared cache and is NOT loaded as a side effect of
    // IOKit P/Invokes, so objc_getClass returns nil until we explicitly dlopen it.
    // <para>ObjC 类仅在其镜像加载后才注册。IOUSBHost 主二进制位于 dyld 共享缓存，
    // 且不会因 IOKit P/Invoke 而附带加载，因此必须先显式 dlopen，
    // 否则 objc_getClass 返回 nil。</para>
    private static readonly Lazy<bool> FrameworkLoaded = new(
        () => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
              NativeLibrary.TryLoad("/System/Library/Frameworks/IOUSBHost.framework/IOUSBHost", out _),
        isThreadSafe: true);

    private static IntPtr LoadClass(string name)
    {
        _ = FrameworkLoaded.Value;
        return objc_getClass(name);
    }

    private static readonly Lazy<IntPtr> DeviceClass = new(() => LoadClass(DeviceClassName), isThreadSafe: true);
    private static readonly Lazy<IntPtr> InterfaceClass = new(() => LoadClass(InterfaceClassName), isThreadSafe: true);
    private static readonly Lazy<IntPtr> NSMutableDataClass = new(() => LoadClass(NSMutableDataClassName), isThreadSafe: true);
    private static readonly Lazy<IntPtr> NSMutableDataAllocSel = new(() => sel_registerName(SelAlloc), isThreadSafe: true);
    private static readonly Lazy<IntPtr> AllocSel = new(() => sel_registerName(SelAlloc), isThreadSafe: true);
    private static readonly Lazy<IntPtr> DataWithLengthSel = new(() => sel_registerName(SelDataWithLength), isThreadSafe: true);
    private static readonly Lazy<IntPtr> MutableBytesSel = new(() => sel_registerName(SelMutableBytes), isThreadSafe: true);
    private static readonly Lazy<IntPtr> LengthSel = new(() => sel_registerName(SelLength), isThreadSafe: true);
    private static readonly Lazy<IntPtr> ErrorCodeSel = new(() => sel_registerName(SelErrorCode), isThreadSafe: true);
    private static readonly Lazy<IntPtr> InitWithIOServiceSel = new(() => sel_registerName(SelInitWithIOService), isThreadSafe: true);
    private static readonly Lazy<IntPtr> DestroySel = new(() => sel_registerName(SelDestroy), isThreadSafe: true);
    private static readonly Lazy<IntPtr> IoServiceSel = new(() => sel_registerName(SelIoService), isThreadSafe: true);
    private static readonly Lazy<IntPtr> ConfigDescriptorSel = new(() => sel_registerName(SelConfigurationDescriptor), isThreadSafe: true);
    private static readonly Lazy<IntPtr> InterfaceDescriptorSel = new(() => sel_registerName(SelInterfaceDescriptor), isThreadSafe: true);
    private static readonly Lazy<IntPtr> SelectAltSettingSel = new(() => sel_registerName(SelSelectAlternateSetting), isThreadSafe: true);
    private static readonly Lazy<IntPtr> CopyPipeSel = new(() => sel_registerName(SelCopyPipeWithAddress), isThreadSafe: true);
    private static readonly Lazy<IntPtr> SendDeviceRequestSel = new(() => sel_registerName(SelSendDeviceRequest), isThreadSafe: true);
    private static readonly Lazy<IntPtr> SendControlRequestSel = new(() => sel_registerName(SelSendControlRequest), isThreadSafe: true);
    private static readonly Lazy<IntPtr> SendIoRequestSel = new(() => sel_registerName(SelSendIoRequest), isThreadSafe: true);
    private static readonly Lazy<IntPtr> EndpointAddressSel = new(() => sel_registerName(SelEndpointAddress), isThreadSafe: true);
    private static readonly Lazy<IntPtr> ResetSel = new(() => sel_registerName(SelReset), isThreadSafe: true);
    private static readonly Lazy<IntPtr> AbortSel = new(() => sel_registerName(SelAbort), isThreadSafe: true);

    /// <summary>
    /// Whether the IOUSBHost ObjC classes are loadable on this machine.
    /// Result is not cached negatively: a later call re-checks after a framework or
    /// environment change.
    /// <para>本机是否可加载 IOUSBHost ObjC 类。不缓存负结果：环境变化后再次调用会重新检查。</para>
    /// </summary>
    public static bool IsAvailable =>
        DeviceClass.Value != IntPtr.Zero && InterfaceClass.Value != IntPtr.Zero;

    /// <summary>Resolves the +1 retained <c>IOUSBHostDevice</c> class pointer.</summary>
    public static IntPtr GetDeviceClass() => DeviceClass.Value;

    /// <summary>Resolves the +1 retained <c>IOUSBHostInterface</c> class pointer.</summary>
    public static IntPtr GetInterfaceClass() => InterfaceClass.Value;

    // ---- Object lifecycle ----

    /// <summary>
    /// Creates an IOUSBHostDevice or IOUSBHostInterface user client for an io_service_t.
    /// This IS the exclusive open: on success the caller owns the device/interface until
    /// <see cref="Destroy"/>. Returns Zero and reports the IOReturn code on failure
    /// (e.g. kIOReturnExclusiveAccess when another client holds the service).
    /// <para>为 io_service_t 创建 IOUSBHostDevice / IOUSBHostInterface 用户客户端。
    /// 该调用本身即独占打开：成功后调用方持有对象直至 <see cref="Destroy"/>。
    /// 失败返回 Zero 并给出 IOReturn 码（如被其他客户端占用时为 kIOReturnExclusiveAccess）。</para>
    /// </summary>
    public static IntPtr CreateObject(IntPtr objcClass, uint ioService, out int ioReturn)
    {
        ioReturn = Ok;
        var alloc = ObjcRuntime.MsgSendObject(objcClass, AllocSel.Value);
        if (alloc == IntPtr.Zero)
        {
            ioReturn = KIOReturnError;
            return IntPtr.Zero;
        }

        var obj = ObjcRuntime.MsgSendObject_Init(
            alloc, InitWithIOServiceSel.Value,
            ioService, options: 0, queue: IntPtr.Zero,
            out var error, interestHandler: IntPtr.Zero);
        if (obj == IntPtr.Zero)
        {
            ioReturn = ReadErrorCode(error);
        }

        return obj;
    }

    /// <summary>
    /// Destroys the user client and releases the object (safe to call on Zero).
    /// <para>销毁用户客户端并释放对象（对 Zero 安全）。</para>
    /// </summary>
    public static void Destroy(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            ObjcRuntime.MsgSendVoid(obj, DestroySel.Value);
            ObjcRuntime.objc_release(obj);
        }
    }

    /// <summary>Reads the io_service_t backing an IOUSBHostObject. <para>读取对象背后的 io_service_t。</para></summary>
    public static uint GetIoService(IntPtr obj) => ObjcRuntime.MsgSendUint(obj, IoServiceSel.Value);

    /// <summary>
    /// Reads the IOReturn code out of an NSError (0 when nil).
    /// <para>读取 NSError 中的 IOReturn 码（nil 时为 0）。</para>
    /// </summary>
    public static int ReadErrorCode(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return KIOReturnError;
        }

        var code = ObjcRuntime.MsgSendUlong(error, ErrorCodeSel.Value);
        return unchecked((int)code);
    }

    // ---- Device-level control transfer ----

    /// <summary>
    /// Synchronous control transfer on the device's default endpoint
    /// (<c>sendDeviceRequest:data:bytesTransferred:completionTimeout:error:</c>).
    /// <para>设备默认端点上的同步控制传输。</para>
    /// </summary>
    /// <param name="device">IOUSBHostDevice instance. <para>IOUSBHostDevice 实例。</para></param>
    /// <param name="request">Setup packet. <para>建立包。</para></param>
    /// <param name="buffer">
    /// Data stage buffer; null for zero-length requests.
    /// <para>数据阶段缓冲；零长度请求为 null。</para>
    /// </param>
    /// <param name="timeoutMs">Completion timeout in ms (0 = wait forever). <para>完成超时（毫秒，0 表示无限等待）。</para></param>
    /// <param name="transferred">Actual data-stage byte count. <para>实际传输字节数。</para></param>
    /// <returns>IOReturn code (0 on success). <para>IOReturn 码（成功为 0）。</para></returns>
    public static int SendDeviceRequest(IntPtr device, ObjcRuntime.UsbHostDeviceRequest request, byte[]? buffer, int timeoutMs, out int transferred)
    {
        transferred = 0;
        var data = buffer is { Length: > 0 } ? CreateMutableData(buffer) : IntPtr.Zero;
        try
        {
            ulong bytesTransferred = 0;
            var ok = ObjcRuntime.MsgSendBool_DeviceRequest(
                device, SendDeviceRequestSel.Value, request, data,
                ref bytesTransferred, timeoutMs / 1000.0, out var error);
            if (ok != 0)
            {
                transferred = checked((int)bytesTransferred);
                return Ok;
            }

            return ReadErrorCode(error);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                ObjcRuntime.objc_release(data);
            }
        }
    }

    // ---- Interface / pipes ----

    /// <summary>
    /// Returns a +1 retained IOUSBHostPipe for an endpoint address, or Zero.
    /// <para>返回 +1 持有的 IOUSBHostPipe，失败为 Zero。</para>
    /// </summary>
    public static IntPtr CopyPipe(IntPtr usbInterface, ulong endpointAddress)
    {
        var pipe = ObjcRuntime.MsgSendObject_Ulong_Error(usbInterface, CopyPipeSel.Value, endpointAddress, out var error);
        _ = error;
        return pipe;
    }

    /// <summary>Releases a pipe obtained from <see cref="CopyPipe"/>. <para>释放 <see cref="CopyPipe"/> 得到的管道。</para></summary>
    public static void ReleasePipe(IntPtr pipe)
    {
        if (pipe != IntPtr.Zero)
        {
            ObjcRuntime.objc_release(pipe);
        }
    }

    /// <summary>Reads an IOUSBHostIOSource (pipe) endpoint address. <para>读取管道的端点地址。</para></summary>
    public static ulong GetPipeEndpointAddress(IntPtr pipe) => ObjcRuntime.MsgSendUlong(pipe, EndpointAddressSel.Value);

    /// <summary>
    /// Synchronous bulk/interrupt transfer
    /// (<c>sendIORequestWithData:bytesTransferred:completionTimeout:error:</c>).
    /// <para>同步批量/中断传输。</para>
    /// </summary>
    /// <param name="pipe">IOUSBHostPipe instance. <para>IOUSBHostPipe 实例。</para></param>
    /// <param name="buffer">Transfer buffer. <para>传输缓冲。</para></param>
    /// <param name="length">Byte count to move. <para>传输字节数。</para></param>
    /// <param name="isWrite">true = OUT (host to device). <para>true 表示 OUT 方向。</para></param>
    /// <param name="timeoutMs">Completion timeout in ms. <para>完成超时（毫秒）。</para></param>
    /// <param name="transferred">Actual byte count. <para>实际传输字节数。</para></param>
    /// <returns>IOReturn code (0 on success). <para>IOReturn 码（成功为 0）。</para></returns>
    public static int SendIoRequest(IntPtr pipe, byte[] buffer, int length, bool isWrite, int timeoutMs, out int transferred)
    {
        transferred = 0;
        var data = CreateMutableData(buffer, length, isWrite);
        try
        {
            ulong bytesTransferred = 0;
            var ok = ObjcRuntime.MsgSendBool_IO(
                pipe, SendIoRequestSel.Value, data,
                ref bytesTransferred, timeoutMs / 1000.0, out var error);
            if (ok != 0)
            {
                transferred = checked((int)bytesTransferred);
                if (!isWrite && transferred > 0)
                {
                    CopyMutableDataBack(data, buffer, transferred);
                }

                return Ok;
            }

            return ReadErrorCode(error);
        }
        finally
        {
            ObjcRuntime.objc_release(data);
        }
    }

    /// <summary>
    /// Resets the USB device (<c>resetWithError:</c> — port-level reset that
    /// re-enumerates the device and invalidates the session).
    /// <para>重置 USB 设备（<c>resetWithError:</c>——端口级复位，设备会重新枚举，
    /// 会话随之失效）。</para>
    /// </summary>
    public static int ResetDevice(IntPtr device)
    {
        var ok = ObjcRuntime.MsgSendBool_OutErr(device, ResetSel.Value, out var error);
        return ok != 0 ? Ok : ReadErrorCode(error);
    }

    /// <summary>
    /// Aborts all in-flight I/O on a pipe (<c>abortWithError:</c>). Blocked
    /// synchronous <c>sendIORequest</c> calls return immediately with
    /// <c>kIOReturnAborted</c>, so a disposing owner can wait for callers to leave
    /// the transfer path before releasing the pipe.
    /// <para>中止管道上所有在飞 I/O（<c>abortWithError:</c>）。阻塞中的同步
    /// <c>sendIORequest</c> 立即以 <c>kIOReturnAborted</c> 返回，因此释放管道前
    /// 可等待调用方退出传输路径。</para>
    /// </summary>
    public static void AbortPipe(IntPtr pipe)
    {
        if (pipe != IntPtr.Zero)
        {
            _ = ObjcRuntime.MsgSendBool_OutErr(pipe, AbortSel.Value, out _);
        }
    }

    /// <summary>
    /// Sends a zero-length packet (nil data) on a pipe — the USB zero-length-packet write.
    /// <para>经管道发送零长度包（nil data），即 USB 的 ZLP 写。</para>
    /// </summary>
    public static int SendIoRequestZero(IntPtr pipe, int timeoutMs)
    {
        ulong unused = 0;
        var ok = ObjcRuntime.MsgSendBool_IO(
            pipe, SendIoRequestSel.Value, IntPtr.Zero,
            ref unused, timeoutMs / 1000.0, out var error);
        return ok != 0 ? Ok : ReadErrorCode(error);
    }

    /// <summary>
    /// Synchronous bulk/interrupt transfer reading directly from a native buffer
    /// (no managed copy). Used by the chunked read/write paths.
    /// <para>直接读写原生缓冲的同步批量/中断传输（无托管拷贝），供分块读写路径使用。</para>
    /// </summary>
    /// <param name="pipe">IOUSBHostPipe instance. <para>IOUSBHostPipe 实例。</para></param>
    /// <param name="buffer">Native buffer pointer. <para>原生缓冲指针。</para></param>
    /// <param name="length">Byte count to move. <para>传输字节数。</para></param>
    /// <param name="isWrite">true = OUT direction. <para>true 表示 OUT 方向。</para></param>
    /// <param name="timeoutMs">Completion timeout in ms. <para>完成超时（毫秒）。</para></param>
    /// <param name="transferred">Actual byte count. <para>实际传输字节数。</para></param>
    /// <returns>IOReturn code (0 on success). <para>IOReturn 码（成功为 0）。</para></returns>
    public static unsafe int SendIoRequestRaw(IntPtr pipe, IntPtr buffer, int length, bool isWrite, int timeoutMs, out int transferred)
    {
        transferred = 0;
        var data = CreateMutableDataRaw(buffer, length, isWrite);
        try
        {
            ulong bytesTransferred = 0;
            var ok = ObjcRuntime.MsgSendBool_IO(
                pipe, SendIoRequestSel.Value, data,
                ref bytesTransferred, timeoutMs / 1000.0, out var error);
            if (ok != 0)
            {
                transferred = checked((int)bytesTransferred);
                if (!isWrite && transferred > 0)
                {
                    Buffer.MemoryCopy((void*)ObjcRuntime.MsgSendObject(data, MutableBytesSel.Value), (void*)buffer, transferred, transferred);
                }

                return Ok;
            }

            return ReadErrorCode(error);
        }
        finally
        {
            ObjcRuntime.objc_release(data);
        }
    }

    private static IntPtr CreateMutableDataRaw(IntPtr source, int length, bool isWrite)
    {
        var data = CreateMutableData(length);
        if (isWrite && length > 0)
        {
            var dst = ObjcRuntime.MsgSendObject(data, MutableBytesSel.Value);
            unsafe
            {
                Buffer.MemoryCopy((void*)source, (void*)dst, length, length);
            }
        }

        return data;
    }

    /// <summary>
    /// Synchronous control transfer through an interface pipe
    /// (<c>sendControlRequest:data:bytesTransferred:completionTimeout:error:</c>).
    /// <para>经接口管道的同步控制传输。</para>
    /// </summary>
    public static int SendControlRequest(IntPtr pipe, ObjcRuntime.UsbHostDeviceRequest request, byte[]? buffer, int timeoutMs, out int transferred)
    {
        transferred = 0;
        var data = buffer is { Length: > 0 } ? CreateMutableData(buffer) : IntPtr.Zero;
        try
        {
            ulong bytesTransferred = 0;
            var ok = ObjcRuntime.MsgSendBool_Control(
                pipe, SendControlRequestSel.Value, request, data,
                ref bytesTransferred, timeoutMs / 1000.0, out var error);
            if (ok != 0)
            {
                transferred = checked((int)bytesTransferred);
                if (transferred > 0 && data != IntPtr.Zero)
                {
                    CopyMutableDataBack(data, buffer!, transferred);
                }

                return Ok;
            }

            return ReadErrorCode(error);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                ObjcRuntime.objc_release(data);
            }
        }
    }

    /// <summary>Selects an interface alternate setting. <para>选择接口备用设置。</para></summary>
    public static int SelectAlternateSetting(IntPtr usbInterface, ulong alternateSetting)
    {
        var ok = ObjcRuntime.MsgSendBool_Ulong_OutErr(usbInterface, SelectAltSettingSel.Value, alternateSetting, out var error);
        return ok != 0 ? Ok : ReadErrorCode(error);
    }

    /// <summary>
    /// Pointer to the interface's packed IOUSBInterfaceDescriptor (+0 retained; valid
    /// while the interface object is alive).
    /// <para>接口 packed 描述符指针（随接口对象生存期有效）。</para>
    /// </summary>
    public static IntPtr GetInterfaceDescriptor(IntPtr usbInterface) =>
        ObjcRuntime.MsgSendObject(usbInterface, InterfaceDescriptorSel.Value);

    /// <summary>
    /// Pointer to the device/interface's packed configuration descriptor.
    /// <para>配置描述符指针。</para>
    /// </summary>
    public static IntPtr GetConfigurationDescriptor(IntPtr receiver) =>
        ObjcRuntime.MsgSendObject(receiver, ConfigDescriptorSel.Value);

    // ---- NSMutableData helpers ----

    /// <summary>Creates an NSMutableData of the given length (+1 retained). <para>创建指定长度的 NSMutableData（+1 持有）。</para></summary>
    public static IntPtr CreateMutableData(int length)
    {
        var autoreleased = ObjcRuntime.MsgSendObject_Ulong(NSMutableDataClass.Value, DataWithLengthSel.Value, (ulong)length);
        return autoreleased == IntPtr.Zero ? IntPtr.Zero : ObjcRuntime.objc_retain(autoreleased);
    }

    private static IntPtr CreateMutableData(byte[] source)
    {
        var data = CreateMutableData(source.Length);
        CopyFromManaged(data, source, source.Length);
        return data;
    }

    private static IntPtr CreateMutableData(byte[] source, int length, bool isWrite)
    {
        var data = CreateMutableData(length);
        if (isWrite)
        {
            CopyFromManaged(data, source, length);
        }

        return data;
    }

    private static unsafe void CopyFromManaged(IntPtr data, byte[] source, int length)
    {
        var dst = ObjcRuntime.MsgSendObject(data, MutableBytesSel.Value);
        fixed (byte* src = source)
        {
            Buffer.MemoryCopy(src, (void*)dst, length, length);
        }
    }

    private static unsafe void CopyMutableDataBack(IntPtr data, byte[] target, int length)
    {
        var src = ObjcRuntime.MsgSendObject(data, MutableBytesSel.Value);
        fixed (byte* dst = target)
        {
            Buffer.MemoryCopy((void*)src, dst, length, length);
        }
    }

    // ---- IOKit child enumeration (interfaces under a device service) ----

    // kern_return_t IORegistryEntryGetChildEntry(io_registry_entry_t entry,
    //   const io_name_t plane, io_iterator_t *child);
    [LibraryImport(IOKit)]
    private static partial int IORegistryEntryGetChildEntry(uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane, out IntPtr child);

    // kern_return_t IORegistryEntryGetName(io_registry_entry_t entry, io_name_t name);
    [LibraryImport(IOKit)]
    private static partial int IORegistryEntryGetName(uint entry, IntPtr name);

    /// <summary>
    /// Enumerates the IOUSBHostInterface children of a device service, identified by
    /// the presence of the <c>bInterfaceNumber</c> property (registry names are
    /// product labels, not class names).
    /// <para>枚举设备服务下的 IOUSBHostInterface 子项，以 <c>bInterfaceNumber</c>
    /// 属性存在为准识别（注册表名是产品标签，不是类名）。</para>
    /// </summary>
    /// <returns>Interface child io_service_t values. <para>接口子 io_service_t 列表。</para></returns>
    // kern_return_t IORegistryEntryCreateIterator(io_registry_entry_t entry,
    //   const io_name_t plane, uint32_t options, io_iterator_t *iterator);
    [LibraryImport(IOKit)]
    private static partial int IORegistryEntryCreateIterator(uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane, uint options, out IntPtr iterator);

    // CFTypeRef IORegistryEntryCreateCFProperty(...) — reused for property probing.
    [LibraryImport(IOKit)]
    private static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    /// <summary>
    /// Enumerates the IOUSBHostInterface children of a device service. Interface
    /// services are identified by the presence of the <c>bInterfaceNumber</c>
    /// property — their registry entry NAMES are product/interface labels (e.g.
    /// "ADB Interface"), not the class name.
    /// <para>枚举设备服务下的 IOUSBHostInterface 子项。接口子项以
    /// <c>bInterfaceNumber</c> 属性存在为准识别——其注册表名是产品/接口标签
    /// （如 "ADB Interface"），不是类名。</para>
    /// </summary>
    public static List<uint> GetInterfaceServices(uint parentService)
    {
        var result = new List<uint>();
        var rc = IORegistryEntryCreateIterator(parentService, IOServicePlane, 0, out var iterator);
        if (rc != Ok)
        {
            return result;
        }

        try
        {
            uint child;
            while ((child = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    if (HasProperty(child, "bInterfaceNumber"))
                    {
                        result.Add(child);
                        continue;
                    }
                }
                catch
                {
                    IOObjectRelease((IntPtr)child);
                    throw;
                }

                IOObjectRelease((IntPtr)child);
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        return result;
    }

    private static bool HasProperty(uint entry, string propertyName)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, propertyName, KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var value = IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0);
            if (value != IntPtr.Zero)
            {
                CFRelease(value);
                return true;
            }

            return false;
        }
        finally
        {
            CFRelease(key);
        }
    }
}