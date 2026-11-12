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

    /// <summary>The <c>DEVPROP_TYPE_STRING_LIST</c> property type of multi-string properties such as the hardware ids.
    /// <para>多字符串属性（如硬件 ID 列表）的 <c>DEVPROP_TYPE_STRING_LIST</c> 属性类型。</para></summary>
    internal const uint DevPropTypeStringList = 0x00000013;

    /// <summary>The <c>WAIT_OBJECT_0</c> result of <c>CMP_WaitNoPendingInstallEvents</c>: no device installation is pending.
    /// <para><c>CMP_WaitNoPendingInstallEvents</c> 的 <c>WAIT_OBJECT_0</c> 结果：当前没有挂起的设备安装。</para></summary>
    internal const uint WaitObject0 = 0x00000000;

    /// <summary>The <c>WAIT_TIMEOUT</c> result of <c>CMP_WaitNoPendingInstallEvents</c>: a device installation is pending.
    /// <para><c>CMP_WaitNoPendingInstallEvents</c> 的 <c>WAIT_TIMEOUT</c> 结果：仍有挂起的设备安装。</para></summary>
    internal const uint WaitTimeout = 0x00000102;

    /// <summary>The <c>SPOST_PATH</c> source type for <c>SetupCopyOEMInf</c>: the INF is staged from a file path.
    /// <para><c>SetupCopyOEMInf</c> 的 <c>SPOST_PATH</c> 来源类型：从文件路径暂存 INF。</para></summary>
    internal const uint SpOemSourcePath = 0x00000000;

    /// <summary>The <c>SUOI_FORCEDELETE</c> flag for <c>SetupUninstallOEMInf</c>: delete the staged package files.
    /// <para><c>SetupUninstallOEMInf</c> 的 <c>SUOI_FORCEDELETE</c> 标志：删除已暂存包的文件。</para></summary>
    internal const uint SuoiForceDelete = 0x00000001;

    /// <summary>The <c>INSTALLFLAG_FORCE</c> flag for <c>UpdateDriverForPlugAndPlayDevices</c>: install the
    /// package even when another driver also matches the device.
    /// <para><c>UpdateDriverForPlugAndPlayDevices</c> 的 <c>INSTALLFLAG_FORCE</c> 标志：即使还有别的驱动匹配该
    /// 设备也安装该包。</para></summary>
    internal const uint InstallFlagForce = 0x00000001;

    /// <summary>The <c>INSTALLFLAG_NONINTERACTIVE</c> flag for <c>UpdateDriverForPlugAndPlayDevices</c>: never
    /// show installation UI.
    /// <para><c>UpdateDriverForPlugAndPlayDevices</c> 的 <c>INSTALLFLAG_NONINTERACTIVE</c> 标志：不显示任何
    /// 安装界面。</para></summary>
    internal const uint InstallFlagNonInteractive = 0x00000004;

    /// <summary>The <c>CM_REENUMERATE_RETRY_INSTALLATION</c> flag for <c>CM_Reenumerate_DevNode</c>: retry driver
    /// installation for devnodes whose install previously failed. Combine it with
    /// <see cref="CmReenumerateAsynchronous"/>, otherwise the call waits for the install to finish.
    /// <para><c>CM_Reenumerate_DevNode</c> 的 <c>CM_REENUMERATE_RETRY_INSTALLATION</c> 标志：对此前安装失败的
    /// 设备节点重试驱动安装。必须与 <see cref="CmReenumerateAsynchronous"/> 组合，否则调用会等待安装完成。</para></summary>
    internal const uint CmReenumerateRetryInstallation = 0x00000002;

    /// <summary>The <c>CM_REENUMERATE_ASYNCHRONOUS</c> flag for <c>CM_Reenumerate_DevNode</c>: return as soon as the
    /// re-enumeration is queued instead of waiting for the PnP install, which a stalled device node can hold
    /// open indefinitely.
    /// <para><c>CM_Reenumerate_DevNode</c> 的 <c>CM_REENUMERATE_ASYNCHRONOUS</c> 标志：重枚举排队后立即返回，
    /// 不等待 PnP 安装，因为卡住的设备节点可能让其无限期挂起。</para></summary>
    internal const uint CmReenumerateAsynchronous = 0x00000004;

    /// <summary>The <c>INVALID_HANDLE_VALUE</c> a failed <c>SetupDiCreateDeviceInfoList</c> returns.
    /// <para><c>SetupDiCreateDeviceInfoList</c> 失败时返回的 <c>INVALID_HANDLE_VALUE</c>。</para></summary>
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>The <c>CM_GETIDLIST_FILTER_PRESENT</c> flag for <c>CM_Get_Device_ID_List_Size</c> and
    /// <c>CM_Get_Device_ID_List</c>: list only devnodes whose device is currently present.
    /// <para><c>CM_Get_Device_ID_List_Size</c> 与 <c>CM_Get_Device_ID_List</c> 的 <c>CM_GETIDLIST_FILTER_PRESENT</c>
    /// 标志：只列出设备当前在位的设备节点。</para></summary>
    internal const uint CmGetIdListFilterPresent = 0x00000001;

    /// <summary>The <c>DN_HAS_PROBLEM</c> status bit of <c>CM_Get_DevNode_Status</c>: the devnode reports a problem
    /// code.
    /// <para><c>CM_Get_DevNode_Status</c> 的 <c>DN_HAS_PROBLEM</c> 状态位：设备节点报告了问题码。</para></summary>
    internal const uint DnHasProblem = 0x00000400;

    /// <summary>The <c>DN_QUERY_REMOVE_PENDING</c> status bit of <c>CM_Get_DevNode_Status</c>: a remove
    /// request is pending for the devnode, so no driver operation can start on it.
    /// <para><c>CM_Get_DevNode_Status</c> 的 <c>DN_QUERY_REMOVE_PENDING</c> 状态位：设备节点上有挂起的
    /// 移除请求，其上无法开始驱动操作。</para></summary>
    internal const uint DnQueryRemovePending = 0x00010000;

    /// <summary>The <c>DN_QUERY_REMOVE_ACTIVE</c> status bit: the devnode is inside a remove query.
    /// <para><c>DN_QUERY_REMOVE_ACTIVE</c> 状态位：设备节点正处于移除查询中。</para></summary>
    internal const uint DnQueryRemoveActive = 0x00020000;

    /// <summary>The <c>DN_WILL_BE_REMOVED</c> status bit: the devnode is queued for removal, so PnP cannot
    /// install a driver on it.
    /// <para><c>DN_WILL_BE_REMOVED</c> 状态位：设备节点已排队等待移除，PnP 无法在其上安装驱动。</para></summary>
    internal const uint DnWillBeRemoved = 0x00040000;

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

    /// <summary>The <c>DEVPKEY_Device_HardwareIds</c> multi-string property.
    /// <para><c>DEVPKEY_Device_HardwareIds</c> 多字符串属性。</para></summary>
    internal static readonly DevPropKey DeviceHardwareIdsKey = new(DeviceFmtid, 3);

    private const int MaxPropertyBytes = 64 * 1024;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Reenumerate_DevNode")]
    internal static partial uint CmReenumerateDevNode(uint devInst, uint flags);

    /// <summary>
    /// Returns the parent devnode of one devnode, used to re-detect a driverless USB device at its hub port.
    /// <para>返回一个设备节点的父节点，用于在集线器端口上重新检测无驱动的 USB 设备。</para>
    /// </summary>
    /// <param name="parentDevInst">Receives the parent devnode handle. <para>接收父设备节点句柄。</para></param>
    /// <param name="devInst">The child devnode handle. <para>子设备节点句柄。</para></param>
    /// <param name="flags">Reserved; must be zero. <para>保留；必须为零。</para></param>
    /// <returns>A <c>CONFIGRET</c> status code. <para><c>CONFIGRET</c> 状态码。</para></returns>
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

    /// <summary>
    /// Waits until the PnP manager has no pending device installation, or the timeout expires.
    /// <para>等待 PnP 管理器没有挂起的设备安装，或超时返回。</para>
    /// </summary>
    /// <param name="timeoutMilliseconds">Wait bound in milliseconds; 0 polls once.
    /// <para>等待上限（毫秒）；0 表示只探测一次。</para></param>
    /// <returns><see cref="WaitObject0"/> when the queue is drained, <see cref="WaitTimeout"/> while an
    /// installation is pending.
    /// <para>队列已排空时为 <see cref="WaitObject0"/>；仍有挂起安装时为 <see cref="WaitTimeout"/>。</para></returns>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CMP_WaitNoPendingInstallEvents")]
    internal static partial uint CmpWaitNoPendingInstallEvents(uint timeoutMilliseconds);

    /// <summary>
    /// Installs a staged package on every present device matching one hardware id and lets PnP start those
    /// devices in place, without removing and re-enumerating their devnodes.
    /// <para>把已暂存的包装到所有匹配该硬件 ID 的在位设备上，并让 PnP 就地启动这些设备，不会移除并重枚举其
    /// 设备节点。</para>
    /// </summary>
    /// <param name="hwndParent">The parent window handle, or <see cref="IntPtr.Zero"/> for none.
    /// <para>父窗口句柄；无父窗口时为 <see cref="IntPtr.Zero"/>。</para></param>
    /// <param name="hardwareId">The hardware id to match.<para>要匹配的硬件 ID。</para></param>
    /// <param name="fullInfPath">The absolute INF path.<para>INF 的绝对路径。</para></param>
    /// <param name="installFlags"><see cref="InstallFlagForce"/> and <see cref="InstallFlagNonInteractive"/>.
    /// <para><see cref="InstallFlagForce"/> 与 <see cref="InstallFlagNonInteractive"/>。</para></param>
    /// <param name="rebootRequired">Non-zero when the caller must reboot for the install to take effect.
    /// <para>非零表示需要重启系统才能生效。</para></param>
    /// <returns><see langword="true"/> when the package was installed on the matching devices.
    /// <para>包已安装到匹配设备上时为 <see langword="true"/>。</para></returns>
    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent,
        [MarshalAs(UnmanagedType.LPWStr)] string hardwareId,
        [MarshalAs(UnmanagedType.LPWStr)] string fullInfPath, uint installFlags, out int rebootRequired);

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

    /// <summary>
    /// Removes one driver package from every device using it, installing another matching package or the
    /// null driver in their place, and then removes the package from the driver store.
    /// <para>从使用该驱动包的所有设备上移除该包，改用其它匹配包或空驱动，随后把该包从驱动仓库中移除。</para>
    /// </summary>
    /// <param name="hwndParent">Parent window handle, or <see cref="IntPtr.Zero"/> for none.
    /// <para>父窗口句柄；无父窗口时为 <see cref="IntPtr.Zero"/>。</para></param>
    /// <param name="infPath">Absolute path of the staged INF. <para>已暂存 INF 的绝对路径。</para></param>
    /// <param name="flags">Zero to remove the package from the driver store.
    /// <para>为零时同时从驱动仓库移除该包。</para></param>
    /// <param name="rebootRequired">Non-zero when a system restart is needed to finish the removal.
    /// <para>非零表示需要重启系统才能完成移除。</para></param>
    /// <returns><see langword="true"/> when the package was uninstalled.
    /// <para>驱动包已卸载时为 <see langword="true"/>。</para></returns>
    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DiUninstallDriverW(IntPtr hwndParent,
        [MarshalAs(UnmanagedType.LPWStr)] string infPath, uint flags, out int rebootRequired);

    /// <summary>
    /// Uninstalls one device node so PnP installs a fresh driver when the device is enumerated again.
    /// <para>卸载一个设备节点，使设备重新枚举时由 PnP 重新安装驱动。</para>
    /// </summary>
    /// <param name="hwndParent">Parent window handle, or <see cref="IntPtr.Zero"/> for none.
    /// <para>父窗口句柄；无父窗口时为 <see cref="IntPtr.Zero"/>。</para></param>
    /// <param name="deviceInfoSet">The device information set holding the node.
    /// <para>承载该设备节点的设备信息集。</para></param>
    /// <param name="deviceInfoData">The device information element. <para>设备信息元素。</para></param>
    /// <param name="flags">Reserved; must be zero. <para>保留；必须为零。</para></param>
    /// <param name="rebootRequired">Non-zero when a system restart is needed to finish the removal.
    /// <para>非零表示需要重启系统才能完成移除。</para></param>
    /// <returns><see langword="true"/> when the device node was removed.
    /// <para>设备节点已移除时为 <see langword="true"/>。</para></returns>
    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DiUninstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData, uint flags, out int rebootRequired);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    internal static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInstanceId, IntPtr hwndParent, uint openFlags,
        ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

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
        => ReadDevNodeProperty(devInst, key, DevPropTypeString) is { Length: > 0 } data ? DecodeString(data) : null;

    /// <summary>
    /// Reads one <c>DEVPROP_TYPE_STRING_LIST</c> property of a devnode, degrading to an empty list when
    /// the property is absent or cannot be read.
    /// <para>读取设备节点的一个 <c>DEVPROP_TYPE_STRING_LIST</c> 属性；属性缺失或不可读时降级为空列表。</para>
    /// </summary>
    /// <param name="devInst">The devnode handle. <para>设备节点句柄。</para></param>
    /// <param name="key">The property key. <para>属性键。</para></param>
    /// <returns>The decoded strings, empty on any failure.
    /// <para>解码后的字符串列表；任何失败时为空。</para></returns>
    internal static string[] ReadDevNodeStringList(uint devInst, in DevPropKey key)
        => ReadDevNodeProperty(devInst, key, DevPropTypeStringList) is { Length: > 0 } data
            ? DecodeStringList(data)
            : [];

    /// <summary>
    /// Reads one devnode property of the expected type, degrading to <see langword="null"/> when the
    /// property is absent, oversized or typed differently.
    /// <para>按预期类型读取一个设备节点属性；属性缺失、超限或类型不符时降级为 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="devInst">The devnode handle. <para>设备节点句柄。</para></param>
    /// <param name="key">The property key. <para>属性键。</para></param>
    /// <param name="expectedType">The <c>DEVPROP</c> type the payload must carry.
    /// <para>负载必须携带的 <c>DEVPROP</c> 类型。</para></param>
    /// <returns>The raw property bytes, or <see langword="null"/> on any failure.
    /// <para>属性原始字节；任何失败时为 <see langword="null"/>。</para></returns>
    private static byte[]? ReadDevNodeProperty(uint devInst, in DevPropKey key, uint expectedType)
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

        return type == expectedType ? data : null;
    }

    private static string? DecodeString(byte[] data)
    {
        if ((data.Length % 2) == 1)
        {
            data = data[..^1];
        }

        var text = Encoding.Unicode.GetString(data).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static string[] DecodeStringList(byte[] data)
    {
        if ((data.Length % 2) == 1)
        {
            data = data[..^1];
        }

        return Encoding.Unicode.GetString(data).Split('\0',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Waits a bounded time for the PnP installation queue to drain. Queuing more device work onto a busy
    /// queue worsens the blockage every device installer shares, so callers gate their own install behind it.
    /// <para>在有限时间内等待 PnP 安装队列排空。向繁忙的队列继续排入设备工作会加剧所有安装器共享的堵塞，
    /// 因此调用方用它给自己的安装把关。</para>
    /// </summary>
    /// <param name="timeout">The wait bound; a zero span polls once.<para>等待上限；零表示只探测一次。</para></param>
    /// <returns><see langword="true"/> when no installation is pending or the API is unavailable;
    /// <see langword="false"/> while an installation is pending.
    /// <para>没有挂起安装或 API 不可用时为 <see langword="true"/>；仍有挂起安装时为
    /// <see langword="false"/>。</para></returns>
    internal static bool WaitForNoPendingInstallation(TimeSpan timeout)
    {
        var milliseconds = Math.Clamp((long)timeout.TotalMilliseconds, 0, int.MaxValue);
        return CmpWaitNoPendingInstallEvents((uint)milliseconds) != WaitTimeout;
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

    /// <summary>The <c>SP_DEVINFO_DATA</c> layout: size, class GUID, devnode handle and a reserved pointer.
    /// <para><c>SP_DEVINFO_DATA</c> 的布局：大小、类 GUID、设备节点句柄与保留指针。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        /// <summary>The structure size in bytes. <para>结构字节数。</para></summary>
        public uint Size;

        /// <summary>The device setup class GUID. <para>设备安装类 GUID。</para></summary>
        public Guid ClassGuid;

        /// <summary>The devnode handle. <para>设备节点句柄。</para></summary>
        public uint DevInst;

        /// <summary>Reserved; always zero. <para>保留；恒为零。</para></summary>
        public IntPtr Reserved;
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