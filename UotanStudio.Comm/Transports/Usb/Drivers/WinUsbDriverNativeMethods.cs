using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The self-contained native boundary for WinUSB driver package operations: newdev driver
/// installation, setupapi device-set plumbing, cfgmgr32 devnode lookup and property reads,
/// wintrust catalog administration, and mssign32 Authenticode signing. Every declaration is
/// private to this class so the shapes cannot collide with the transport interop classes.
/// <para>WinUSB 驱动包操作的自包含原生边界：newdev 驱动安装、setupapi 设备集通道、cfgmgr32
/// 设备节点定位与属性读取、wintrust 目录管理以及 mssign32 Authenticode 签名。所有声明都是
/// 本类私有的，不会与传输层互操作类发生形状冲突。</para>
/// </summary>
internal static partial class WinUsbDriverNativeMethods
{
    internal const uint CrSuccess = 0x00000000;
    internal const uint CrBufferSmall = 0x0000001A;
    internal const uint CmLocateDevnodeNormal = 0x00000000;

    internal const uint DevPropTypeString = 0x00000012;

    internal const uint DiirflagForceInf = 0x00000002;

    internal const uint AlgIdSha256 = 0x0000800C;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// The <c>DRIVER_ACTION_VERIFY</c> subsystem GUID (<c>F750E6C3-38EE-11D1-85E5-00C04FC295EE</c>,
    /// defined in <c>SoftPub.h</c>) that scopes a catalog administrator context to the system
    /// component and third-party driver catalog database.
    /// <para><c>DRIVER_ACTION_VERIFY</c> 子系统 GUID（<c>F750E6C3-38EE-11D1-85E5-00C04FC295EE</c>，
    /// 定义于 <c>SoftPub.h</c>），把目录管理上下文限定在系统组件与第三方驱动目录数据库。</para>
    /// </summary>
    internal static readonly Guid DriverActionVerify = new(0xf750e6c3, 0x38ee, 0x11d1, 0x85, 0xe5, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);

    /// <summary>
    /// The <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c> GUID (<c>00AAC56B-CD44-11D0-8CC2-00C04FC295EE</c>)
    /// that selects the Authenticode policy provider for <c>WinVerifyTrust</c>.
    /// <para><c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c> GUID（<c>00AAC56B-CD44-11D0-8CC2-00C04FC295EE</c>），
    /// 为 <c>WinVerifyTrust</c> 选择 Authenticode 策略提供者。</para>
    /// </summary>
    internal static readonly Guid WinTrustActionGenericVerifyV2 = new(0x00aac56b, 0xcd44, 0x11d0, 0x8c, 0xc2, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);

    /// <summary>The format GUID of the <c>DEVPKEY_Device_Driver*</c> property family.
    /// <para><c>DEVPKEY_Device_Driver*</c> 属性族的格式 GUID。</para></summary>
    private static readonly Guid DeviceDriverFmtid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    /// <summary>The format GUID of the <c>DEVPKEY_Device_*</c> property family.
    /// <para><c>DEVPKEY_Device_*</c> 属性族的格式 GUID。</para></summary>
    private static readonly Guid DeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    internal static readonly DevPropKey DriverInfPathKey = new(DeviceDriverFmtid, 5);
    internal static readonly DevPropKey DriverProviderKey = new(DeviceDriverFmtid, 9);
    internal static readonly DevPropKey DriverServiceKey = new(DeviceFmtid, 6);

    private const int MaxPropertyBytes = 64 * 1024;

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DiInstallDriverW(IntPtr hwndParent,
        [MarshalAs(UnmanagedType.LPWStr)] string infPath, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DiUninstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    internal static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInstanceId, IntPtr hwndParent, uint openFlags,
        ref SpDevInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Locate_DevNodeW(out uint devInst,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminAcquireContext(out IntPtr catAdmin, in Guid actionVerify, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminCalcHashFromFileHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        ref uint hashBytes, byte[]? hash, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    internal static partial IntPtr CryptCATAdminAddCatalog(IntPtr catAdmin,
        [MarshalAs(UnmanagedType.LPWStr)] string catalogFile,
        [MarshalAs(UnmanagedType.LPWStr)] string? selectBaseName, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo catalogInfo, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = false)]
    internal static partial int WinVerifyTrust(IntPtr hwnd, in Guid actionId, ref WinTrustData data);

    [LibraryImport("mssign32.dll")]
    internal static partial int SignerSignEx(uint flags, IntPtr subjectInfo, IntPtr signerCert, IntPtr signatureInfo,
        IntPtr providerInfo, [MarshalAs(UnmanagedType.LPWStr)] string? httpTimeStamp, IntPtr request, IntPtr sipData,
        out IntPtr signerContext);

    [LibraryImport("mssign32.dll")]
    internal static partial int SignerFreeSignerContext(IntPtr signerContext);

    /// <summary>
    /// Reads one <c>DEVPROP_TYPE_STRING</c> property of a devnode, degrading to
    /// <see langword="null"/> when the property is absent or cannot be read.
    /// <para>读取设备节点的一个 <c>DEVPROP_TYPE_STRING</c> 属性；属性缺失或不可读时降级为
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <param name="devInst">The devnode handle. <para>设备节点句柄。</para></param>
    /// <param name="key">The property key. <para>属性键。</para></param>
    /// <returns>The decoded string, or <see langword="null"/> on any failure.
    /// <para>解码后的字符串；任何失败时为 <see langword="null"/>。</para></returns>
    internal static string? ReadDevNodeString(uint devInst, in DevPropKey key)
    {
        var probe = key;
        var size = 0u;
        var result = CM_Get_DevNode_PropertyW(devInst, ref probe, out var type, null, ref size, 0);
        byte[]? data;
        if (result == CrSuccess)
        {
            data = size == 0 ? [] : null;
        }
        else if ((result == CrBufferSmall) && (size is > 0 and <= MaxPropertyBytes))
        {
            var buffer = new byte[size];
            var actual = size;
            var fill = key;
            if ((CM_Get_DevNode_PropertyW(devInst, ref fill, out type, buffer, ref actual, 0) != CrSuccess) || (actual == 0))
            {
                return null;
            }

            data = actual == size ? buffer : buffer[..(int)actual];
        }
        else
        {
            return null;
        }

        if ((type != DevPropTypeString) || (data is not { Length: > 0 }))
        {
            return null;
        }

        if ((data.Length % 2) == 1)
        {
            data = data[..^1];
        }

        var text = Encoding.Unicode.GetString(data).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Formats the last Win32 error as a human-readable message.
    /// <para>把最近的 Win32 错误格式化为可读消息。</para>
    /// </summary>
    /// <param name="prefix">Optional context prepended to the message. <para>可选的上下文前缀。</para></param>
    /// <returns>The formatted message. <para>格式化后的消息。</para></returns>
    internal static string DescribeLastWin32Error(string? prefix = null)
    {
        var message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return string.IsNullOrEmpty(prefix) ? message : $"{prefix}: {message}";
    }

    /// <summary>
    /// The native <c>DEVPROPKEY</c>: a format GUID plus a property id, byte-for-byte matching
    /// <c>devpropdef.h</c>.
    /// <para>原生 <c>DEVPROPKEY</c>：格式 GUID 加属性编号，与 <c>devpropdef.h</c> 逐字节对齐。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        /// <summary>The property category GUID. <para>属性类别 GUID。</para></summary>
        public Guid Fmtid;

        /// <summary>The property id within the category. <para>类别内的属性编号。</para></summary>
        public uint Pid;

        public DevPropKey(Guid fmtid, uint pid)
        {
            Fmtid = fmtid;
            Pid = pid;
        }
    }

    /// <summary>
    /// The native <c>SP_DEVINFO_DATA</c> as consumed by setupapi and newdev device APIs.
    /// <para>setupapi 与 newdev 设备 API 使用的原生 <c>SP_DEVINFO_DATA</c>。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        /// <summary>Structure size; must be set before each call. <para>结构大小；每次调用前必须设置。</para></summary>
        public uint CbSize;

        /// <summary>The device setup class GUID. <para>设备安装类 GUID。</para></summary>
        public Guid ClassGuid;

        /// <summary>The device instance handle. <para>设备实例句柄。</para></summary>
        public uint DevInst;

        /// <summary>Reserved. <para>保留字段。</para></summary>
        public IntPtr Reserved;
    }

    /// <summary>
    /// The native <c>CATALOG_INFO</c> returned by <c>CryptCATCatalogInfoFromContext</c>; the
    /// fixed-size buffer carries the catalog file path inside the catalog database.
    /// <para><c>CryptCATCatalogInfoFromContext</c> 返回的原生 <c>CATALOG_INFO</c>；定长缓冲承载
    /// 目录数据库内目录文件的路径。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CatalogInfo
    {
        /// <summary>Structure size; must be set before the call. <para>结构大小；调用前必须设置。</para></summary>
        public uint CbSize;

        /// <summary>The catalog file path as a fixed UTF-16 buffer. <para>定长 UTF-16 缓冲形式的目录文件路径。</para></summary>
        public unsafe fixed char CatalogFile[260];
    }

    /// <summary>
    /// Reads the catalog file path out of a populated <see cref="CatalogInfo"/> as a managed
    /// string, stopping at the null padding.
    /// <para>从已填充的 <see cref="CatalogInfo"/> 读出目录文件路径并转为托管字符串，遇到 null 填充即止。</para>
    /// </summary>
    /// <param name="info">The structure filled by <c>CryptCATCatalogInfoFromContext</c>.
    /// <para>由 <c>CryptCATCatalogInfoFromContext</c> 填充的结构。</para></param>
    /// <returns>The catalog file path. <para>目录文件路径。</para></returns>
    internal static unsafe string ReadCatalogFile(ref CatalogInfo info)
    {
        fixed (char* path = info.CatalogFile)
        {
            return new string(path);
        }
    }

    /// <summary>
    /// The native <c>WINTRUST_FILE_INFO</c> selecting a plain file as the trust subject.
    /// <para>选择普通文件作为信任主体的原生 <c>WINTRUST_FILE_INFO</c>。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustFileInfo
    {
        /// <summary>Structure size. <para>结构大小。</para></summary>
        public uint CbSize;

        /// <summary>The file path; used when <see cref="File"/> is empty. <para>文件路径；<see cref="File"/> 为空时使用。</para></summary>
        public IntPtr FilePath;

        /// <summary>An optional open file handle; zero to use the path. <para>可选的已打开文件句柄；为零时使用路径。</para></summary>
        public IntPtr File;

        /// <summary>An optional known subject GUID. <para>可选的已知主体 GUID。</para></summary>
        public IntPtr KnownSubject;
    }

    /// <summary>
    /// The native <c>WINTRUST_DATA</c> flattened to the file-choice path: the union is carried as
    /// one pointer field because every alternative is pointer-sized.
    /// <para>按文件选择路径展平的原生 <c>WINTRUST_DATA</c>：联合体以单个指针字段承载，因为每个分支都是指针大小。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        /// <summary>Structure size. <para>结构大小。</para></summary>
        public uint CbStruct;

        /// <summary>Optional policy callback data. <para>可选的策略回调数据。</para></summary>
        public IntPtr PolicyCallbackData;

        /// <summary>Optional SIP client data. <para>可选的 SIP 客户端数据。</para></summary>
        public IntPtr SipClientData;

        /// <summary>UI choice; 2 suppresses all UI. <para>UI 选择；2 表示不显示任何 UI。</para></summary>
        public uint UiChoice;

        /// <summary>Revocation check policy; 0 checks nothing. <para>吊销检查策略；0 表示不检查。</para></summary>
        public uint RevocationChecks;

        /// <summary>Union choice; 1 selects the file info. <para>联合体选择；1 表示文件信息。</para></summary>
        public uint UnionChoice;

        /// <summary>The union payload as one pointer. <para>以单个指针表示的联合体负载。</para></summary>
        public IntPtr Union;

        /// <summary>State action; 1 verifies, 2 closes state. <para>状态动作；1 校验，2 关闭状态。</para></summary>
        public uint StateAction;

        /// <summary>State data handle returned after verification. <para>校验后返回的状态数据句柄。</para></summary>
        public IntPtr StateData;

        /// <summary>Optional URL reference. <para>可选的 URL 引用。</para></summary>
        public IntPtr UrlReference;

        /// <summary>Provider flags. <para>提供者标志。</para></summary>
        public uint ProvFlags;

        /// <summary>UI context. <para>UI 上下文。</para></summary>
        public uint UiContext;

        /// <summary>Optional signature settings. <para>可选的签名设置。</para></summary>
        public IntPtr SignatureSettings;
    }
}