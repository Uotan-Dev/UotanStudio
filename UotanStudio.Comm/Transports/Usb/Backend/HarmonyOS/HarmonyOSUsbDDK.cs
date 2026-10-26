using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

internal static partial class HarmonyOSUsbDDK
{
    public const string LibUsbNdk = "libusb_ndk.z.so";

    public const int USB_DDK_NO_ERROR = 0;

    /// <summary>
    /// Legacy DDK generic failure (before API 16).
    /// <para>旧版（API 16 之前）DDK 通用失败。</para>
    /// </summary>
    public const int USB_DDK_FAILED = -1;

    /// <summary>
    /// Legacy DDK invalid-parameter error (before API 16).
    /// <para>旧版（API 16 之前）DDK 参数错误。</para>
    /// </summary>
    public const int USB_DDK_INVALID_PARAMETER = -2;

    /// <summary>
    /// Legacy DDK memory-allocation error (before API 16).
    /// <para>旧版（API 16 之前）DDK 内存分配错误。</para>
    /// </summary>
    public const int USB_DDK_MEMORY_ERROR = -3;

    /// <summary>
    /// Legacy DDK invalid-operation error (before API 16).
    /// <para>旧版（API 16 之前）DDK 非法操作错误。</para>
    /// </summary>
    public const int USB_DDK_INVALID_OPERATION = -4;

    /// <summary>
    /// Legacy DDK null-pointer error (before API 16).
    /// <para>旧版（API 16 之前）DDK 空指针错误。</para>
    /// </summary>
    public const int USB_DDK_NULL_PTR = -5;

    /// <summary>
    /// Legacy DDK device-busy error (before API 16).
    /// <para>旧版（API 16 之前）DDK 设备忙错误。</para>
    /// </summary>
    public const int USB_DDK_DEVICE_BUSY = -6;

    /// <summary>
    /// Legacy DDK timeout code (before API 16), kept under the historical name for existing callers.
    /// <para>旧版（API 16 之前）DDK 超时码，保留历史名称以兼容现有调用方。</para>
    /// Newer DDKs (API 16+) report no dedicated timeout code, so <see cref="IsTimeout"/> matches
    /// exactly this value; a new DDK never returns it, so the match stays unambiguous.
    /// <para>新版 DDK（API 16+）没有独立超时码，因此 <see cref="IsTimeout"/> 只匹配该值；
    /// 新版 DDK 不会返回它，匹配保持无歧义。</para>
    /// </summary>
    public const int USB_DDK_TIMEOUT = -7;

    /// <summary>
    /// New-layout DDK permission error (API 16+); classified with the parameter errors.
    /// <para>新版（API 16+）DDK 权限错误；归入参数错误一类。</para>
    /// </summary>
    public const int USB_DDK_NEW_PERMISSION_DENIED = 201;

    /// <summary>
    /// New-layout DDK parameter error (API 16+).
    /// <para>新版（API 16+）DDK 参数错误。</para>
    /// </summary>
    public const int USB_DDK_NEW_INVALID_PARAMETER = 401;

    /// <summary>
    /// Inclusive lower bound of the API 16+ fatal error range (27400001–27400004). The individual
    /// numbers in this range are not asserted beyond "fatal": all of them keep the fatal handling.
    /// <para>API 16+ 致命错误区间（27400001–27400004）的下界（含）。该区间内每个编号的含义不作断言，
    /// 全部按致命错误处理。</para>
    /// </summary>
    public const int USB_DDK_NEW_FATAL_ERROR_MIN = 27400001;

    /// <summary>
    /// Inclusive upper bound of the API 16+ fatal error range.
    /// <para>API 16+ 致命错误区间的上界（含）。</para>
    /// </summary>
    public const int USB_DDK_NEW_FATAL_ERROR_MAX = 27400004;

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

    /// <summary>
    /// Memory-mapped transfer buffer layout returned by <c>OH_Usb_CreateDeviceMemMap</c> on SDK
    /// versions from API 16 on: no device id, an explicit buffer address, and separate request
    /// (<see cref="BufferLength"/>) and transferred (<see cref="TransferedLength"/>) lengths.
    /// <para>SDK 版本自 API 16 起 <c>OH_Usb_CreateDeviceMemMap</c> 返回的内存映射传输缓冲区布局：
    /// 无设备 ID、显式缓冲区地址，请求长度（<see cref="BufferLength"/>）与传输长度
    /// （<see cref="TransferedLength"/>）分开。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDeviceMemMapV2
    {
        public IntPtr Address;
        public UIntPtr Size;
        public uint Offset;
        public uint BufferLength;
        public uint TransferedLength;
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

    /// <summary>
    /// Sends a pipe (bulk/interrupt) request using the API 16+ memory-map layout; the transferred
    /// length is reported back in <see cref="UsbDeviceMemMapV2.TransferedLength"/>.
    /// <para>以 API 16+ 内存映射布局发送管道（bulk/interrupt）请求；传输长度通过
    /// <see cref="UsbDeviceMemMapV2.TransferedLength"/> 回读。</para>
    /// </summary>
    /// <param name="pipe">The request pipe. <para>请求管道。</para></param>
    /// <param name="devMmap">The memory map in the new layout. <para>新版布局的内存映射。</para></param>
    /// <returns>USB_DDK_NO_ERROR on success, otherwise a DDK error code.
    /// <para>成功返回 USB_DDK_NO_ERROR；否则返回 DDK 错误码。</para></returns>
    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_SendPipeRequest")]
    public static partial int OH_Usb_SendPipeRequest(ref UsbRequestPipe pipe, ref UsbDeviceMemMapV2 devMmap);

    /// <summary>
    /// Queries the SDK version of the loaded USB DDK (<c>OH_Usb_GetSdkVersion</c>); a nonzero
    /// return means the query failed.
    /// <para>查询已加载 USB DDK 的 SDK 版本（<c>OH_Usb_GetSdkVersion</c>）；非零返回值表示查询失败。</para>
    /// The native parameter is an unsigned-int pointer, marshaled here as <c>ref uint</c>, which
    /// keeps the same ABI without widening the unsafe surface.
    /// <para>原生参数为无符号整数指针，此处以 <c>ref uint</c> 编组，ABI 相同且不扩大 unsafe 范围。</para>
    /// </summary>
    /// <param name="sdkVersion">Receives the SDK version on success. <para>成功时接收 SDK 版本。</para></param>
    /// <returns>USB_DDK_NO_ERROR on success, otherwise a DDK error code.
    /// <para>成功返回 USB_DDK_NO_ERROR；否则返回 DDK 错误码。</para></returns>
    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetSdkVersion")]
    public static partial int OH_Usb_GetSdkVersion(ref uint sdkVersion);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_CreateDeviceMemMap")]
    public static partial int OH_Usb_CreateDeviceMemMap(ulong DeviceId, UIntPtr Size, ref IntPtr devMmap);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_DestroyDeviceMemMap")]
    public static partial int OH_Usb_DestroyDeviceMemMap(IntPtr devMmap);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetDescriptor")]
    public static partial int OH_Usb_GetDescriptor(ulong DeviceId, byte descType, byte descIndex, IntPtr Data, ref uint dataLen);

    [LibraryImport(LibUsbNdk, EntryPoint = "OH_Usb_GetInterfaceSetting")]
    public static partial int OH_Usb_GetInterfaceSetting(ulong DeviceId, byte configIndex, byte interfaceIndex, ref byte Altsetting);

    /// <summary>
    /// Classifies a DDK return code as a transfer timeout.
    /// <para>将 DDK 返回码分类为传输超时。</para>
    /// Only the legacy (before API 16) code <see cref="USB_DDK_TIMEOUT"/> (-7) is a timeout; newer
    /// DDKs report no dedicated timeout code, so unknown codes keep the existing fatal handling.
    /// <para>只有旧版（API 16 之前）的 <see cref="USB_DDK_TIMEOUT"/>（-7）表示超时；新版 DDK 没有独立
    /// 超时码，未知错误码保持现有致命错误处理。</para>
    /// </summary>
    /// <param name="ret">The DDK return code. <para>DDK 返回码。</para></param>
    /// <returns><c>true</c> when the code denotes a timeout; otherwise <c>false</c>.
    /// <para>表示超时返回 <c>true</c>；否则返回 <c>false</c>。</para></returns>
    internal static bool IsTimeout(int ret) => ret == USB_DDK_TIMEOUT;

    /// <summary>
    /// Determines whether the <c>OH_Usb_CreateDeviceMemMap</c>/<c>OH_Usb_SendPipeRequest</c> memory
    /// map follows the new layout introduced with API 16.
    /// <para>判断 <c>OH_Usb_CreateDeviceMemMap</c>/<c>OH_Usb_SendPipeRequest</c> 的内存映射是否采用
    /// API 16 引入的新版布局。</para>
    /// A <c>null</c> version (query failed or unsupported) keeps the legacy layout.
    /// <para>版本为 <c>null</c>（查询失败或不支持）时保持旧版布局。</para>
    /// </summary>
    /// <param name="sdkVersion">The SDK version reported by the DDK, or <c>null</c> when unknown.
    /// <para>DDK 报告的 SDK 版本；未知时为 <c>null</c>。</para></param>
    /// <returns><c>true</c> for SDK versions from API 16 on; otherwise <c>false</c>.
    /// <para>SDK 版本不低于 API 16 时返回 <c>true</c>；否则返回 <c>false</c>。</para></returns>
    internal static bool UsesNewMemMapLayout(uint? sdkVersion) => sdkVersion is { } version && version >= 16;

    /// <summary>
    /// Best-effort query of the loaded USB DDK's SDK version, used to select the memmap layout.
    /// <para>尽力查询已加载 USB DDK 的 SDK 版本，用于选择内存映射布局。</para>
    /// Returns <c>null</c> when the DDK is too old to export <c>OH_Usb_GetSdkVersion</c> or the
    /// query fails; callers then fall back to the legacy layout.
    /// <para>DDK 过旧未导出 <c>OH_Usb_GetSdkVersion</c> 或查询失败时返回 <c>null</c>；调用方随后回退到旧版布局。</para>
    /// </summary>
    /// <returns>The SDK version, or <c>null</c> when unknown. <para>SDK 版本；未知时为 <c>null</c>。</para></returns>
    internal static uint? TryGetSdkVersion()
    {
        try
        {
            var version = 0u;
            return OH_Usb_GetSdkVersion(ref version) == USB_DDK_NO_ERROR ? version : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static string GetErrorMessage(int errorCode)
    {
        return errorCode switch
        {
            USB_DDK_NO_ERROR => "Success",
            USB_DDK_FAILED => "Failed",
            USB_DDK_INVALID_PARAMETER => "Invalid parameter",
            USB_DDK_MEMORY_ERROR => "Memory error",
            USB_DDK_INVALID_OPERATION => "Invalid operation",
            USB_DDK_NULL_PTR => "Null pointer",
            USB_DDK_DEVICE_BUSY => "Device busy",
            USB_DDK_TIMEOUT => "Timeout",
            USB_DDK_NEW_PERMISSION_DENIED => "Permission denied",
            USB_DDK_NEW_INVALID_PARAMETER => "Invalid parameter",
            >= USB_DDK_NEW_FATAL_ERROR_MIN and <= USB_DDK_NEW_FATAL_ERROR_MAX => "Fatal error",
            _ => $"Unknown error ({errorCode})",
        };
    }
}