using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The self-contained native boundary for the vendor driver package operations: cfgmgr32 devnode
/// lookup, property and status reads, device id list queries, re-enumeration, and the setupapi
/// driver-store staging and removal APIs. Every declaration is private to this class so the shapes
/// cannot collide with the transport interop classes.
/// <para>Rockusb 驱动包操作的自包含原生边界：cfgmgr32 设备节点定位、属性读取与重枚举，以及 setupapi
/// 的驱动仓库暂存与移除 API。所有声明都是本类私有的，不会与传输层互操作类发生形状冲突。</para>
/// </summary>
internal static partial class WinUsbDriverNativeMethods
{
    internal const uint CrSuccess = 0x00000000;
    internal const uint CrBufferSmall = 0x0000001A;
    internal const uint CmLocateDevnodeNormal = 0x00000000;

    internal const uint DevPropTypeString = 0x00000012;

    /// <summary>The <c>SPOST_PATH</c> source type for <c>SetupCopyOEMInf</c>: the INF is staged from a file path.
    /// <para><c>SetupCopyOEMInf</c> 的 <c>SPOST_PATH</c> 来源类型：从文件路径暂存 INF。</para></summary>
    internal const uint SpOemSourcePath = 0x00000000;

    /// <summary>The <c>SUOI_FORCEDELETE</c> flag for <c>SetupUninstallOEMInf</c>: delete the staged package files.
    /// <para><c>SetupUninstallOEMInf</c> 的 <c>SUOI_FORCEDELETE</c> 标志：删除已暂存包的文件。</para></summary>
    internal const uint SuoiForceDelete = 0x00000001;

    /// <summary>The <c>CM_REENUMERATE_RETRY_INSTALLATION</c> flag for <c>CM_Reenumerate_DevNode</c>: retry driver
    /// installation for devnodes whose install previously failed, without waiting for it to finish.
    /// <para><c>CM_Reenumerate_DevNode</c> 的 <c>CM_REENUMERATE_RETRY_INSTALLATION</c> 标志：对此前安装失败的
    /// 设备节点重试驱动安装，且不等待其完成。</para></summary>
    internal const uint CmReenumerateRetryInstallation = 0x00000002;

    /// <summary>The <c>CM_GETIDLIST_FILTER_PRESENT</c> flag for <c>CM_Get_Device_ID_List_Size</c> and
    /// <c>CM_Get_Device_ID_List</c>: list only devnodes whose device is currently present.
    /// <para><c>CM_Get_Device_ID_List_Size</c> 与 <c>CM_Get_Device_ID_List</c> 的 <c>CM_GETIDLIST_FILTER_PRESENT</c>
    /// 标志：只列出设备当前在位的设备节点。</para></summary>
    internal const uint CmGetIdListFilterPresent = 0x00000001;

    /// <summary>The <c>DN_HAS_PROBLEM</c> status bit of <c>CM_Get_DevNode_Status</c>: the devnode reports a problem
    /// code.
    /// <para><c>CM_Get_DevNode_Status</c> 的 <c>DN_HAS_PROBLEM</c> 状态位：设备节点报告了问题码。</para></summary>
    internal const uint DnHasProblem = 0x00000400;

    /// <summary>The <c>CM_PROB_FAILED_INSTALL</c> problem code that marks a devnode without an installed driver.
    /// <para>标记设备节点未安装驱动的 <c>CM_PROB_FAILED_INSTALL</c> 问题码。</para></summary>
    internal const uint CmProbFailedInstall = 0x0000001C;

    /// <summary>The Win32 error <c>ERROR_FILE_NOT_FOUND</c>; <c>SetupUninstallOEMInf</c> reports it when the package is already gone.
    /// <para>Win32 错误 <c>ERROR_FILE_NOT_FOUND</c>；包已不存在时 <c>SetupUninstallOEMInf</c> 会返回它。</para></summary>
    internal const int ErrorFileNotFound = 2;

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

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Reenumerate_DevNode")]
    internal static partial uint CmReenumerateDevNode(uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    internal static partial uint CmGetParent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    internal static partial uint CmGetDevNodeStatus(out uint status, out uint problemNumber, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CmGetDeviceIdListSizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CmGetDeviceIdListW([Out] byte[] buffer, uint bufferLength, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Query_And_Remove_SubTreeW")]
    internal static partial uint CmQueryAndRemoveSubTree(uint devInst, IntPtr vetoType, IntPtr vetoName,
        uint vetoNameLength, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupCopyOEMInfW([MarshalAs(UnmanagedType.LPWStr)] string sourceInfFileName,
        IntPtr oemSourceMediaLocation, uint oemSourceMediaType, uint oemSourceCopyFlags,
        IntPtr destinationInfFileName, uint destinationInfFileNameSize,
        out uint requiredSize, IntPtr destinationInfFileNameComponent);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupUninstallOEMInfW([MarshalAs(UnmanagedType.LPWStr)] string catalogFileName,
        uint flags, IntPtr reserved);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Locate_DevNodeW(out uint devInst,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

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
}