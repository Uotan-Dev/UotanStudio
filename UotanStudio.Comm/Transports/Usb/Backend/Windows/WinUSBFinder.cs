using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBFinder
{
    private const string PreferWinUsbVidPid = "vid_18d1&pid_d00d";

    /// <summary>Backend tag recorded for an interface opened through WinUSB.
    /// <para>经 WinUSB 打开的接口所记录的后端标记。</para></summary>
    internal const string BackendWinUsb = "WinUSB";

    /// <summary>Backend tag recorded for an interface served by <c>rockusb.sys</c>.
    /// <para>由 <c>rockusb.sys</c> 服务的接口所记录的后端标记。</para></summary>
    internal const string BackendRockusb = "Rockusb";

    /// <summary>Backend tag recorded for an interface opened through the legacy driver.
    /// <para>经 legacy 驱动打开的接口所记录的后端标记。</para></summary>
    internal const string BackendLegacy = "Legacy";
    private const int InterfaceDetailCbSizeX64 = 8;
    private const int InterfaceDetailCbSizeX86 = 6;
    private const int InterfaceDetailPathOffset = 4;
    private const int StackInterfaceDetailBytes = 1024;
    private const uint MinimumInterfaceDetailBytes = 6;

    /// <summary>
    /// Gets whether the SetupDi all-classes enumeration handle opened successfully during
    /// the last enumeration. Lets upper layers distinguish "SetupDi failed" from
    /// "SetupDi ran but found no devices" on device-less CI.
    /// <para>获取上次枚举中 SetupDi 全类枚举句柄是否成功打开。让上层区分
    /// "SetupDi 失败"与"SetupDi 运行但未发现设备"（无设备 CI 场景）。</para>
    /// </summary>
    public static bool LastSetupDiSucceeded { get; private set; }

    /// <summary>
    /// Gets the number of potential USB device nodes walked during the last enumeration: nodes whose
    /// instance id uses the USB enumerator or (for compatibility) still contains <c>VID_</c>. A non-zero
    /// value proves the scan loop actually ran.
    /// <para>获取上次枚举中遍历的潜在 USB 设备节点数：实例 ID 使用 USB 枚举器、或（为兼容）
    /// 仍包含 <c>VID_</c> 的节点。非零值证明扫描循环确实执行过。</para>
    /// </summary>
    public static int LastScannedNodeCount { get; private set; }

    /// <summary>
    /// Gets the number of devices matched during the last enumeration.
    /// <para>获取上次枚举中匹配到的设备数。</para>
    /// </summary>
    public static int LastMatchedDeviceCount { get; private set; }

    /// <summary>
    /// Enumerates WinUSB/Legacy devices, optionally applying the filter and session options.
    /// <para>枚举 WinUSB/Legacy 设备，可选地应用过滤器与会话选项。</para>
    /// Session options (timeouts, RAW_IO) are assigned to each newly created
    /// <see cref="WinUSBDevice"/> BEFORE its handle is created, so the pipe policies
    /// take effect for the session.
    /// <para>会话选项（超时、RAW_IO）会在句柄创建前赋给每个新建的
    /// <see cref="WinUSBDevice"/>，使管道策略对会话生效。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <returns>The discovered backend devices. <para>发现的后端设备。</para></returns>
    /// <remarks>
    /// Every pass walks every present device class, because a USB node can carry a vendor-renamed
    /// instance id and restricting the walk would drop it from later passes. What a warm pass skips is
    /// the per-interface re-read: the interface triple, the container id and the device-interface GUIDs
    /// come from the inventory cache instead of the registry, and the DeviceClasses reverse map is reused
    /// across passes instead of walking 200+ registry keys each time.
    /// <para>每一轮遍历都会走过当前存在的全部设备类，因为 USB 节点可能携带厂商重命名的实例 ID，限制
    /// 遍历范围会让它在后续轮次中漏掉。暖轮省去的是每个接口的重读：接口三元组、容器 ID 与设备接口 GUID
    /// 改由清单缓存提供而不是注册表，DeviceClasses 反向映射也在各轮之间复用，而不是每轮遍历 200 多个
    /// 注册表键。</para>
    /// </remarks>
    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null, UsbSessionOptions? options = null)
    {
        var devices = new List<UsbDevice>();
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LastScannedNodeCount = 0;
        LastMatchedDeviceCount = 0;

        var pass = new ScanPass(filter);

        var devInfoSet = Win32API.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
            Win32API.DIGCF_PRESENT | Win32API.DIGCF_ALLCLASSES);
        LastSetupDiSucceeded = devInfoSet != -1;
        if (devInfoSet == -1)
        {
            UsbTrace.LogFormatted($"WinUSBFinder: SetupDiGetClassDevsW(all classes) failed err={Marshal.GetLastWin32Error()}");
            return devices;
        }

        var lazyDeviceClassGuids = new Lazy<Dictionary<string, List<Guid>>>(CachedDeviceClassGuidMap);

        try
        {
            var seenGuids = new HashSet<Guid>();
            uint memberIndex = 0;
            while (true)
            {
                var devInfoData = new Win32API.SpDevInfoData();
                devInfoData.CbSize = (uint)Marshal.SizeOf(devInfoData);
                if (!Win32API.SetupDiEnumDeviceInfo(devInfoSet, memberIndex++, ref devInfoData))
                {
                    break;
                }

                var instanceId = GetDeviceInstanceId(devInfoSet, ref devInfoData);
                if (!IsPotentialUsbInstanceId(instanceId))
                {
                    continue;
                }

                LastScannedNodeCount++;
                UsbTrace.LogFormatted($"WinUSBFinder: node [{instanceId}]");
                foreach (var guid in ReadDeviceInterfaceGuids(instanceId, lazyDeviceClassGuids, pass))
                {
                    if (!seenGuids.Add(guid))
                    {
                        continue;
                    }

                    UsbTrace.LogFormatted($"WinUSBFinder:   interface GUID {guid}");
                    EnumerateInterfacesForGuid(guid, filter, options, devices, uniqueKeys, pass);
                }
            }
        }
        finally
        {
            Win32API.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        pass.Finish();
        LastMatchedDeviceCount = devices.Count;
        return devices;
    }

    /// <summary>
    /// One enumeration pass: whether it may answer from the inventory cache, and which instances it observed
    /// so the cache can be pruned against reality at the end.
    /// <para>一次枚举遍历：是否可以用清单缓存作答，以及观测到了哪些实例，以便结束时按真实现场裁剪缓存。</para>
    /// </summary>
    internal sealed class ScanPass
    {
        private readonly HashSet<string> _observedInstanceIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a pass over the supplied filter.
        /// <para>按给定过滤器初始化一轮遍历。</para>
        /// </summary>
        /// <param name="filter">The device filter, or <see langword="null"/> for a whole-tree pass.
        /// <para>设备过滤器；为 <see langword="null"/> 表示遍历整棵树。</para></param>
        internal ScanPass(UsbDeviceFilter? filter)
        {
            Unfiltered = string.IsNullOrEmpty(filter?.DevicePathEquals);
            Incremental = Unfiltered && WinUsbInventoryCache.HasCompletedFullPass;
        }

        /// <summary>Whether the pass searched the whole tree instead of one exact path.
        /// <para>本轮是遍历整棵树，还是只查找一条精确路径。</para></summary>
        internal bool Unfiltered { get; }

        /// <summary>Whether cached interface records may stand in for unread registry evidence.
        /// <para>是否可由缓存的接口记录代替未读的注册表证据。</para></summary>
        internal bool Incremental { get; }

        /// <summary>Notes that this pass observed one instance.
        /// <para>记录本轮观测到了某个实例。</para></summary>
        /// <param name="instanceId">The observed instance id. <para>被观测到的实例 ID。</para></param>
        internal void Observe(string? instanceId)
        {
            if (Unfiltered && !string.IsNullOrWhiteSpace(instanceId))
            {
                _observedInstanceIds.Add(instanceId!);
            }
        }

        /// <summary>Prunes instances this pass did not see and marks the inventory as warm.
        /// <para>裁剪本轮未看到的实例，并把清单标记为已变暖。</para></summary>
        internal void Finish()
        {
            if (!Unfiltered)
            {
                return;
            }

            WinUsbInventoryCache.PruneUnobserved(_observedInstanceIds);
            WinUsbInventoryCache.CompleteFullPass();
        }
    }

    private static void EnumerateInterfacesForGuid(
        Guid guid,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys,
        ScanPass pass)
    {
        var apiGuid = guid;
        var devInfo = Win32API.SetupDiGetClassDevsW(ref apiGuid, null, IntPtr.Zero,
            Win32API.DIGCF_PRESENT | Win32API.DIGCF_DEVICEINTERFACE);
        if (devInfo == -1)
        {
            UsbTrace.LogFormatted($"WinUSBFinder: SetupDiGetClassDevsW({guid}) failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            EnumerateInterfaces(devInfo, ref apiGuid, filter, options, devices, uniqueKeys, pass);
        }
        finally
        {
            Win32API.SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    internal delegate bool InterfaceEnumerator(uint index, ref Win32API.SpDeviceInterfaceData interfaceData);

    private static void EnumerateInterfaces(
        IntPtr devInfo,
        ref Guid interfaceClassGuid,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys,
        ScanPass pass)
    {
        var guid = interfaceClassGuid;
        bool Enumerate(uint index, ref Win32API.SpDeviceInterfaceData iface)
        {
            return Win32API.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, index, ref iface);
        }

        EnumerateInterfacesCore(devInfo, Enumerate, filter, options, devices, uniqueKeys, pass);
    }

    /// <summary>
    /// Walks the device interfaces of an open SetupDi information set and opens or
    /// metadata-materializes the ones matching the filter.
    /// <para>遍历已打开 SetupDi 信息集中的设备接口，打开或按元数据构造匹配过滤器的接口。</para>
    /// Internal for regression tests that drive it with a synthetic enumerator and an invalid
    /// set handle to prove a failed size probe skips the interface instead of allocating from
    /// an unvalidated reported size.
    /// <para>为回归测试保留 internal：合成枚举器配合无效集合句柄，证明 size 探测失败时跳过该接口，
    /// 而不是按未校验的上报长度分配。</para>
    /// </summary>
    /// <param name="devInfo">SetupDi information set handle. <para>SetupDi 信息集句柄。</para></param>
    /// <param name="enumerator">Callback producing the next interface for the given index. <para>按索引产出下一个接口的回调。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <param name="devices">Collected devices on return. <para>返回时已收集的设备。</para></param>
    /// <param name="uniqueKeys">Dedup keys shared across GUID walks. <para>跨 GUID 遍历共享的去重键。</para></param>
    /// <param name="pass">The pass context carrying the incremental decision and observed instances.
    /// <para>携带增量判定与已观测实例的遍历上下文。</para></param>
    internal static void EnumerateInterfacesCore(
        IntPtr devInfo,
        InterfaceEnumerator enumerator,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys,
        ScanPass pass)
    {
        uint index = 0;
        var interfaceData = new Win32API.SpDeviceInterfaceData();
        interfaceData.CbSize = (uint)Marshal.SizeOf(interfaceData);

        while (enumerator(index++, ref interfaceData))
        {
            ProcessInterface(devInfo, ref interfaceData, filter, options, devices, uniqueKeys, pass);
        }

        UsbTrace.LogFormatted($"WinUSBFinder: EnumerateInterfacesCore done, lastErr={Marshal.GetLastWin32Error()}, devices={devices.Count}");
    }

    /// <summary>
    /// Returns whether an instance id can belong to a USB node: USB enumerator instance ids start with
    /// <c>USB\</c>, and a <c>VID_</c> substring also qualifies for vendor-specific or renamed
    /// instances.
    /// <para>判断实例 ID 是否可能属于 USB 节点：USB 枚举器实例 ID 以 <c>USB\</c> 开头；
    /// 含 <c>VID_</c> 子串的厂商自定义或重命名实例同样成立。</para>
    /// </summary>
    private static bool IsPotentialUsbInstanceId(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return false;
        }

        return instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
            instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Probes one interface's detail size, materializes the detail in a stack buffer when it fits and
    /// falls back to the heap above that threshold, then opens or metadata-materializes the matching
    /// device. VID/PID are parsed from the interface path exactly once and passed to the filter and the
    /// metadata device.
    /// <para>探测单个接口的详情长度，能在栈缓冲区容纳时在栈上获取详情，超过阈值才退回堆分配，
    /// 然后打开或按元数据构造匹配设备。VID/PID 只从接口路径解析一次，并传给过滤器与元数据设备。</para>
    /// </summary>
    /// <param name="devInfo">SetupDi information set handle. <para>SetupDi 信息集句柄。</para></param>
    /// <param name="interfaceData">The interface being processed. <para>正在处理的接口。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <param name="devices">Collected devices on return. <para>返回时已收集的设备。</para></param>
    /// <param name="uniqueKeys">Dedup keys shared across GUID walks. <para>跨 GUID 遍历共享的去重键。</para></param>
    /// <param name="pass">The pass context carrying the incremental decision and observed instances.
    /// <para>携带增量判定与已观测实例的遍历上下文。</para></param>
    /// <remarks>
    /// The interface path carries the PnP instance id, so a warm pass derives it for free and lets the
    /// inventory cache answer for the interface triple, the container id and the opened backend instead
    /// of re-reading CompatibleIDs, the node instance id and ContainerID from the registry.
    /// <para>接口路径本身携带 PnP 实例 ID，暖轮因此可以免费推导它，并让清单缓存代答接口三元组、容器 ID
    /// 与已打开的后端，而不是从注册表重读 CompatibleIDs、节点实例 ID 与 ContainerID。</para>
    /// A class GUID collected from a USB node is also registered by that class's non-USB devices
    /// (RAS ports, volumes, root hubs), and the per-GUID walk returns them all. Requiring the
    /// hardware ids a firmware device always carries keeps those out of discovery instead of
    /// projecting every machine-wide interface as a candidate device.
    /// <para>从 USB 节点收集的类 GUID 也会被该类的非 USB 设备（RAS 端口、卷、根集线器）注册，按 GUID
    /// 的遍历会把它们全部返回。要求具备固件设备必然携带的硬件 ID，就能把它们挡在发现结果之外，而不是把
    /// 全机每个接口都投影成候选设备。</para>
    /// An unopened pass learns no backend, so it must not erase a backend an opened pass recorded.
    /// <para>未打开设备的轮次学不到任何后端，因此不得抹除已打开轮次记录的后端。</para>
    /// </remarks>
    private static unsafe void ProcessInterface(
        IntPtr devInfo,
        ref Win32API.SpDeviceInterfaceData interfaceData,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys,
        ScanPass pass)
    {
        if (!Win32API.SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, IntPtr.Zero, 0, out var detailSize, IntPtr.Zero))
        {
            var probeError = Marshal.GetLastWin32Error();
            if (probeError != Win32API.ERROR_INSUFFICIENT_BUFFER)
            {
                UsbTrace.LogError($"WinUSBFinder: interface detail size probe failed err={probeError}; skipping interface.");
                return;
            }
        }

        if (detailSize is < MinimumInterfaceDetailBytes or > int.MaxValue)
        {
            UsbTrace.LogError($"WinUSBFinder: interface detail size probe reported unusable size {detailSize}; skipping interface.");
            return;
        }

        var byteCount = (int)detailSize;
        byte* buffer = stackalloc byte[StackInterfaceDetailBytes];
        var heapBuffer = IntPtr.Zero;
        if (byteCount > StackInterfaceDetailBytes)
        {
            heapBuffer = Marshal.AllocHGlobal(byteCount);
            buffer = (byte*)heapBuffer.ToPointer();
        }

        try
        {
            var cbSize = IntPtr.Size == 8 ? InterfaceDetailCbSizeX64 : InterfaceDetailCbSizeX86;
            Marshal.WriteInt32((IntPtr)buffer, cbSize);
            var node = new Win32API.SpDevInfoData { CbSize = (uint)Marshal.SizeOf<Win32API.SpDevInfoData>() };
            if (!Win32API.SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, (IntPtr)buffer, detailSize, out _, ref node))
            {
                return;
            }

            var path = Marshal.PtrToStringUni(new IntPtr(buffer + InterfaceDetailPathOffset)) ?? string.Empty;
            var (vid, pid) = TryParseVidPid(path);
            if (!PathMatchesFilter(path, filter, vid, pid))
            {
                return;
            }

            var derivedInstanceId = TryDeriveInstanceId(path);
            if (derivedInstanceId is null)
            {
                return;
            }

            string? cachedInstanceId = null;
            WinUsbInventoryCache.Record? cached = null;
            if (pass.Incremental && derivedInstanceId is { Length: > 0 } instanceKey &&
                WinUsbInventoryCache.TryGet(instanceKey, out var remembered))
            {
                cached = remembered;
                cachedInstanceId = instanceKey;
            }

            pass.Observe(derivedInstanceId);

            var identity = CreateCachedMetadataDevice(vid ?? 0, pid ?? 0, cached)
                ?? CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
            var interfaceFilterMismatch =
                (filter?.InterfaceClass is byte cls && identity.InterfaceClass != cls) ||
                (filter?.InterfaceSubClass is byte sub && identity.InterfaceSubClass != sub) ||
                (filter?.InterfaceProtocol is byte protocol && identity.InterfaceProtocol != protocol);
            if (interfaceFilterMismatch)
            {
                return;
            }

            var isGoogleWinUsb = path.Contains(PreferWinUsbVidPid, StringComparison.OrdinalIgnoreCase);
            var instanceId = cachedInstanceId ?? GetDeviceInstanceId(devInfo, ref node);
            var separator = instanceId.LastIndexOf('\\');
            var candidate = separator >= 0 ? instanceId[(separator + 1)..] : null;
            var serial = ResolveEnumeratedSerial(path, instanceId, candidate, identity);

            UsbDevice? device = null;
            if (isGoogleWinUsb)
            {
                UsbTrace.LogFormatted($"Prefers WinUSB for Google device: {path}");
                device = TryOpenWinUSB(path, options, serial);
            }

            device ??= ProbeDevice(path, options, serial, cached);

            if (device != null)
            {
                device.DeviceInstanceId = instanceId;
                var containerId = cached is { ContainerRead: true } ? cached.ContainerId : ReadContainerId(instanceId);
                device.ContainerId = containerId;
                if (!string.IsNullOrWhiteSpace(device.SerialNumber))
                {
                    UsbSerialCache.Remember(path, instanceId, device.SerialNumber, Environment.TickCount64);
                }

                if (device.VendorId == RockchipVendorId || device is RockusbDevice)
                {
                    AttachObservedDescriptor(device);
                }

                var key = BuildDeviceKey(device);
                if (uniqueKeys.Add(key))
                {
                    UsbTrace.LogFormatted($"Confirmed device added: key={key} using {AttachedBackendName(device)}");
                    devices.Add(device);
                }
                else
                {
                    device.Dispose();
                }

                WinUsbInventoryCache.RememberInterfaceMetadata(
                    instanceId,
                    vid ?? 0,
                    pid ?? 0,
                    device.InterfaceClass ?? identity.InterfaceClass,
                    device.InterfaceSubClass ?? identity.InterfaceSubClass,
                    device.InterfaceProtocol ?? identity.InterfaceProtocol,
                    device.InterfaceMetadataObserved || identity.InterfaceMetadataObserved,
                    containerId,
                    options?.MetadataOnly == true ? string.Empty : AttachedBackendName(device));
            }
            else
            {
                UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice returned null for [{path}]");
            }
        }
        finally
        {
            if (heapBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(heapBuffer);
            }
        }
    }

}