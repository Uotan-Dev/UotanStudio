using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Text;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// <para>cfgmgr32 与设备属性读取的原生边界。这里集中所有 P/Invoke、<c>CR_*</c> 与
/// <c>DEVPROP_TYPE_*</c> 常量，以及从 <c>DEVINST</c> 解码单个属性的逻辑；任何单个属性读取失败都只会
/// 让对应字段降级为 <see langword="null"/>，绝不抛出，以保证拓扑枚举是只读且零干扰的。</para>
/// The native boundary for cfgmgr32 and device-property reads. All P/Invoke declarations, the
/// <c>CR_*</c> and <c>DEVPROP_TYPE_*</c> constants, and the decoding of one property from a
/// <c>DEVINST</c> live here; a failure on any single property only degrades that field to
/// <see langword="null"/> and never throws, keeping the topology enumeration read-only and
/// zero-interference.
/// </summary>
internal static partial class UsbPnpNative
{
    private const uint CrSuccess = 0x00000000;
    private const uint CrBufferSmall = 0x0000001A;
    private const uint CmGetIdListFilterPresent = 0x00000100;

    private const uint DevPropTypeUint32 = 0x00000007;
    private const uint DevPropTypeGuid = 0x0000000D;
    private const uint DevPropTypeString = 0x00000012;
    private const uint DevPropTypeStringList = DevPropTypeString | 0x00002000;

    private const int MaxDeviceIdChars = 4096;
    private const int MaxPropertyBytes = 64 * 1024;

    private static readonly Guid DeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DeviceStatusFmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7");
    private static readonly Guid DeviceContainerFmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
    private static readonly Guid DeviceBusReportedFmtid = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
    private static readonly Guid DeviceDriverFmtid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    private static readonly DEVPROPKEY DeviceDescKey = new(DeviceFmtid, 2);
    private static readonly DEVPROPKEY HardwareIdsKey = new(DeviceFmtid, 3);
    private static readonly DEVPROPKEY CompatibleIdsKey = new(DeviceFmtid, 4);
    private static readonly DEVPROPKEY ServiceKey = new(DeviceFmtid, 6);
    private static readonly DEVPROPKEY ClassKey = new(DeviceFmtid, 9);
    private static readonly DEVPROPKEY ClassGuidKey = new(DeviceFmtid, 10);
    private static readonly DEVPROPKEY FriendlyNameKey = new(DeviceFmtid, 14);
    private static readonly DEVPROPKEY LocationPathsKey = new(DeviceFmtid, 37);
    private static readonly DEVPROPKEY ProblemCodeKey = new(DeviceStatusFmtid, 3);
    private static readonly DEVPROPKEY ContainerIdKey = new(DeviceContainerFmtid, 2);
    private static readonly DEVPROPKEY BusReportedDeviceDescKey = new(DeviceBusReportedFmtid, 4);
    private static readonly DEVPROPKEY DriverInfPathKey = new(DeviceDriverFmtid, 5);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length,
        [MarshalAs(UnmanagedType.LPWStr)] string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_ID_ListW([MarshalAs(UnmanagedType.LPWStr)] string? filter,
        IntPtr buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_IDW(uint devInst, IntPtr buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Child(out uint childDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Sibling(out uint siblingDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Locate_DevNodeW(out uint devInst,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

    /// <summary>
    /// <para>报告当前机器能否读到 USB 设备实例清单，作为只探测大小、不分配缓冲区的轻量可用性检查。</para>
    /// Reports whether this machine can read the USB device-instance list, as a lightweight
    /// availability probe that only queries the size and allocates no buffer.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the present-device id list size can be read.
    /// <para>能够读取在位设备清单大小时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool CanReadDeviceIdList()
        => CM_Get_Device_ID_List_SizeW(out _, "USB\\*", CmGetIdListFilterPresent) == CrSuccess;

    /// <summary>
    /// <para>枚举当前在位、实例标识以 <c>USB\</c> 或 <c>USB4\</c> 开头的设备标识，作为种子集合；主机控制器等
    /// 非 <c>USB\</c> 祖先由调用方经父链补齐。原生 <c>USB\*</c> 查询串在本机实测会放行非 USB 标识，因此这里
    /// 再以托管前缀判定为准。</para>
    /// Enumerates the present device instance ids that start with <c>USB\</c> or <c>USB4\</c> as the seed
    /// set; non-<c>USB\</c> ancestors such as host controllers are added by the caller through the parent
    /// chain. The native <c>USB\*</c> query string demonstrably admits non-USB ids on real machines, so the
    /// managed prefix test is the authoritative filter.
    /// </summary>
    /// <returns>
    /// The matching instance ids, or an empty list when the list cannot be read.
    /// <para>匹配到的实例标识；清单不可读时为空。</para>
    /// </returns>
    internal static IReadOnlyList<string> ReadPresentDeviceIds()
    {
        if (CM_Get_Device_ID_List_SizeW(out var length, "USB\\*", CmGetIdListFilterPresent) != CrSuccess ||
            length == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            unsafe
            {
                new Span<char>(buffer.ToPointer(), (int)length).Clear();
            }

            if (CM_Get_Device_ID_ListW("USB\\*", buffer, length, CmGetIdListFilterPresent) != CrSuccess)
            {
                return [];
            }

            var ids = new List<string>();
            var offset = 0;
            while (offset < length)
            {
                var id = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset * sizeof(char)));
                if (string.IsNullOrEmpty(id))
                {
                    break;
                }

                ids.Add(id);
                offset += id.Length + 1;
            }

            return [.. ids.Where(id => id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("USB4\\", StringComparison.OrdinalIgnoreCase))];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// <para>把设备实例标识解析为 <c>DEVINST</c> 句柄，供属性读取与父链上溯使用。</para>
    /// Resolves a device instance id into a <c>DEVINST</c> handle used for property reads and parent
    /// climbing.
    /// </summary>
    /// <param name="deviceId">
    /// The device instance id. <para>设备实例标识。</para>
    /// </param>
    /// <param name="devInst">
    /// The resolved handle when this returns <see langword="true"/>. <para>返回 <see langword="true"/> 时解析出的句柄。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the devnode exists. <para>设备节点存在时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool TryLocateDevNode(string deviceId, out uint devInst)
        => CM_Locate_DevNodeW(out devInst, deviceId, 0) == CrSuccess;

    /// <summary>
    /// <para>读取一个 <c>DEVINST</c> 的设备实例标识，按需倍增缓冲区，失败时返回
    /// <see langword="null"/>。</para>
    /// Reads the device instance id of a <c>DEVINST</c>, doubling the buffer as needed and returning
    /// <see langword="null"/> on failure.
    /// </summary>
    /// <param name="devInst">
    /// The devnode handle. <para>设备节点句柄。</para>
    /// </param>
    /// <returns>
    /// The instance id, or <see langword="null"/> when it cannot be read.
    /// <para>实例标识；不可读时为 <see langword="null"/>。</para>
    /// </returns>
    internal static string? GetDeviceId(uint devInst)
    {
        var capacity = 200u;
        while (capacity <= MaxDeviceIdChars)
        {
            var buffer = Marshal.AllocHGlobal((int)capacity * sizeof(char));
            try
            {
                var result = CM_Get_Device_IDW(devInst, buffer, capacity, 0);
                if (result == CrSuccess)
                {
                    return Marshal.PtrToStringUni(buffer);
                }

                if (result != CrBufferSmall)
                {
                    return null;
                }

                capacity *= 2;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// <para>取一个 <c>DEVINST</c> 的直接父节点；到达根或查询失败时返回
    /// <see langword="false"/>。</para>
    /// Takes the direct parent of a <c>DEVINST</c>; returns <see langword="false"/> at the root or when
    /// the lookup fails.
    /// </summary>
    /// <param name="devInst">
    /// The child devnode handle. <para>子设备节点句柄。</para>
    /// </param>
    /// <param name="parentDevInst">
    /// The parent handle when this returns <see langword="true"/>. <para>返回 <see langword="true"/> 时的父句柄。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a parent exists. <para>存在父节点时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool TryGetParent(uint devInst, out uint parentDevInst)
        => CM_Get_Parent(out parentDevInst, devInst, 0) == CrSuccess;

    /// <summary>
    /// <para>取一个 <c>DEVINST</c> 的第一个直接子节点；无子节点或查询失败时返回
    /// <see langword="false"/>。</para>
    /// Takes the first direct child of a <c>DEVINST</c>; returns <see langword="false"/> when the
    /// node has no children or the lookup fails.
    /// </summary>
    /// <param name="devInst">
    /// The parent devnode handle. <para>父设备节点句柄。</para>
    /// </param>
    /// <param name="childDevInst">
    /// The first child handle when this returns <see langword="true"/>. <para>返回 <see langword="true"/> 时的第一个子节点句柄。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a child exists. <para>存在子节点时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool TryGetChild(uint devInst, out uint childDevInst)
        => CM_Get_Child(out childDevInst, devInst, 0) == CrSuccess;

    /// <summary>
    /// <para>取同父节点下某 <c>DEVINST</c> 的下一个兄弟节点；没有更多兄弟或查询失败时返回
    /// <see langword="false"/>。</para>
    /// Takes the next sibling of a <c>DEVINST</c> under the same parent; returns
    /// <see langword="false"/> when no sibling remains or the lookup fails.
    /// </summary>
    /// <param name="devInst">
    /// The current sibling devnode handle. <para>当前兄弟设备节点句柄。</para>
    /// </param>
    /// <param name="siblingDevInst">
    /// The next sibling handle when this returns <see langword="true"/>. <para>返回 <see langword="true"/> 时的下一个兄弟节点句柄。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a sibling exists. <para>存在兄弟节点时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool TryGetSibling(uint devInst, out uint siblingDevInst)
        => CM_Get_Sibling(out siblingDevInst, devInst, 0) == CrSuccess;

    /// <summary>
    /// <para>读取一个设备节点的全部已定义属性，把无法读取的字段降级为
    /// <see langword="null"/> 或空集合，返回尚未包含父标识与设备接口 GUID 的原始记录。</para>
    /// Reads every defined property of a devnode, degrading unreadable fields to <see langword="null"/>
    /// or empty collections, and returns the raw record before the parent id and device-interface GUIDs
    /// are attached.
    /// </summary>
    /// <param name="devInst">
    /// The devnode handle. <para>设备节点句柄。</para>
    /// </param>
    /// <param name="instanceId">
    /// The already-known instance id of the node. <para>节点已知的实例标识。</para>
    /// </param>
    /// <returns>
    /// The raw property record for <see cref="WindowsUsbTopologyProvider.ShapeNode"/>.
    /// <para>供 <see cref="WindowsUsbTopologyProvider.ShapeNode"/> 使用的原始属性记录。</para>
    /// </returns>
    internal static UsbDevNodeRawRecord ReadProperties(uint devInst, string instanceId) => new()
    {
        InstanceId = instanceId,
        DeviceDescription = ReadString(devInst, DeviceDescKey),
        BusReportedDeviceDescription = ReadString(devInst, BusReportedDeviceDescKey),
        FriendlyName = ReadString(devInst, FriendlyNameKey),
        ClassName = ReadString(devInst, ClassKey),
        ClassGuid = ReadGuid(devInst, ClassGuidKey),
        DriverService = ReadString(devInst, ServiceKey),
        DriverInfPath = ReadString(devInst, DriverInfPathKey),
        ProblemCode = ReadUInt32(devInst, ProblemCodeKey),
        ContainerId = ReadGuid(devInst, ContainerIdKey),
        HardwareIds = ReadStringList(devInst, HardwareIdsKey),
        CompatibleIds = ReadStringList(devInst, CompatibleIdsKey),
        LocationPaths = ReadStringList(devInst, LocationPathsKey)
    };

    private static string? ReadString(uint devInst, in DEVPROPKEY key)
        => ReadProperty(devInst, key) is (DevPropTypeString, var data) ? DecodeString(data) : null;

    private static IReadOnlyList<string> ReadStringList(uint devInst, in DEVPROPKEY key)
        => ReadProperty(devInst, key) is (DevPropTypeStringList, var data)
            ? DecodeStringList(data)
            : [];

    private static Guid? ReadGuid(uint devInst, in DEVPROPKEY key)
        => ReadProperty(devInst, key) is (DevPropTypeGuid, byte[] data) && data.Length >= 16
            ? new Guid(data.AsSpan(0, 16))
            : null;

    private static uint? ReadUInt32(uint devInst, in DEVPROPKEY key)
        => ReadProperty(devInst, key) is (DevPropTypeUint32, byte[] data) && data.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : null;

    private static (uint Type, byte[]? Data)? ReadProperty(uint devInst, in DEVPROPKEY key)
    {
        var probe = key;
        var size = 0u;
        var result = CM_Get_DevNode_PropertyW(devInst, ref probe, out var type, null, ref size, 0);
        if (result == CrSuccess)
        {
            return (type, size == 0 ? [] : null);
        }

        if (result != CrBufferSmall || size == 0 || size > MaxPropertyBytes)
        {
            return null;
        }

        var buffer = new byte[size];
        var actual = size;
        var fill = key;
        if (CM_Get_DevNode_PropertyW(devInst, ref fill, out type, buffer, ref actual, 0) != CrSuccess)
        {
            return null;
        }

        if (actual == 0)
        {
            return (type, []);
        }

        if (actual > size)
        {
            return null;
        }

        if (actual < size)
        {
            var trimmed = new byte[actual];
            Array.Copy(buffer, trimmed, actual);
            return (type, trimmed);
        }

        return (type, buffer);
    }

    private static string? DecodeString(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        var text = Encoding.Unicode.GetStringTrimNul(data);
        return text.Length == 0 ? null : text;
    }

    private static IReadOnlyList<string> DecodeStringList(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return [];
        }

        return Encoding.Unicode.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// <para>原生 <c>DEVPROPKEY</c>：一个格式 GUID 加一个属性编号，与 <c>devpropdef.h</c> 的
    /// <c>DEVPROPKEY</c> 逐字节对齐。</para>
    /// The native <c>DEVPROPKEY</c>: a format GUID plus a property id, byte-for-byte matching the
    /// <c>DEVPROPKEY</c> in <c>devpropdef.h</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        /// <summary>The property category GUID. <para>属性类别 GUID。</para></summary>
        public Guid Fmtid;

        /// <summary>The property id within the category. <para>类别内的属性编号。</para></summary>
        public uint Pid;

        public DEVPROPKEY(Guid fmtid, uint pid)
        {
            Fmtid = fmtid;
            Pid = pid;
        }
    }
}