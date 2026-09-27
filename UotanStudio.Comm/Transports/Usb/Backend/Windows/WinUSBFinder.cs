using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBFinder
{
    private const string PreferWinUsbVidPid = "vid_18d1&pid_d00d";

    /// <summary>
    /// Gets whether the SetupDi all-classes enumeration handle opened successfully during
    /// the last enumeration. Lets upper layers distinguish "SetupDi failed" from
    /// "SetupDi ran but found no devices" on device-less CI.
    /// <para>获取上次枚举中 SetupDi 全类枚举句柄是否成功打开。让上层区分
    /// "SetupDi 失败"与"SetupDi 运行但未发现设备"（无设备 CI 场景）。</para>
    /// </summary>
    public static bool LastSetupDiSucceeded { get; private set; }

    /// <summary>
    /// Gets the number of USB device nodes (instance ids containing VID_) walked during
    /// the last enumeration. A non-zero value proves the scan loop actually ran.
    /// <para>获取上次枚举中遍历的 USB 设备节点数（含 VID_ 的实例 ID）。
    /// 非零值证明扫描循环确实执行过。</para>
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
    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null, UsbSessionOptions? options = null)
    {
        var devices = new List<UsbDevice>();
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LastScannedNodeCount = 0;
        LastMatchedDeviceCount = 0;

        var devInfoSet = Win32API.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
            Win32API.DIGCF_PRESENT | Win32API.DIGCF_ALLCLASSES);
        LastSetupDiSucceeded = devInfoSet != -1;
        if (devInfoSet == -1)
        {
            UsbTrace.Log($"WinUSBFinder: SetupDiGetClassDevsW(all classes) failed err={Marshal.GetLastWin32Error()}");
            return devices;
        }

        var lazyDeviceClassGuids = new Lazy<Dictionary<string, List<Guid>>>(BuildDeviceClassGuidMap);

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
                if (string.IsNullOrEmpty(instanceId) ||
                    instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                LastScannedNodeCount++;
                UsbTrace.Log($"WinUSBFinder: node [{instanceId}]");
                foreach (var guid in ReadDeviceInterfaceGuids(instanceId, lazyDeviceClassGuids))
                {
                    if (!seenGuids.Add(guid))
                    {
                        continue;
                    }

                    UsbTrace.Log($"WinUSBFinder:   interface GUID {guid}");
                    EnumerateInterfacesForGuid(guid, filter, options, devices, uniqueKeys);
                }
            }
        }
        finally
        {
            Win32API.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        LastMatchedDeviceCount = devices.Count;
        return devices;
    }

    private static void EnumerateInterfacesForGuid(
        Guid guid,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys)
    {
        var apiGuid = guid;
        var devInfo = Win32API.SetupDiGetClassDevsW(ref apiGuid, null, IntPtr.Zero,
            Win32API.DIGCF_PRESENT | Win32API.DIGCF_DEVICEINTERFACE);
        if (devInfo == -1)
        {
            UsbTrace.Log($"WinUSBFinder: SetupDiGetClassDevsW({guid}) failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            EnumerateInterfaces(devInfo, ref apiGuid, filter, options, devices, uniqueKeys);
        }
        finally
        {
            Win32API.SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    private delegate bool InterfaceEnumerator(uint index, ref Win32API.SpDeviceInterfaceData interfaceData);

    private static void EnumerateInterfaces(
        IntPtr devInfo,
        ref Guid interfaceClassGuid,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys)
    {
        var guid = interfaceClassGuid;
        bool Enumerate(uint index, ref Win32API.SpDeviceInterfaceData iface)
        {
            return Win32API.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, index, ref iface);
        }

        EnumerateInterfacesCore(devInfo, Enumerate, filter, options, devices, uniqueKeys);
    }

    private static void EnumerateInterfacesCore(
        IntPtr devInfo,
        InterfaceEnumerator enumerator,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys)
    {
        uint index = 0;
        var interfaceData = new Win32API.SpDeviceInterfaceData();
        interfaceData.CbSize = (uint)Marshal.SizeOf(interfaceData);

        while (enumerator(index++, ref interfaceData))
        {
            Win32API.SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, IntPtr.Zero, 0, out var detailSize, IntPtr.Zero);

            var detailBuffer = Marshal.AllocHGlobal((int)detailSize);
            try
            {
                var cbSize = IntPtr.Size == 8 ? 8 : 6;
                var pathOffset = 4;
                Marshal.WriteInt32(detailBuffer, cbSize);
                var node = new Win32API.SpDevInfoData { CbSize = (uint)Marshal.SizeOf<Win32API.SpDevInfoData>() };
                if (Win32API.SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, detailBuffer, detailSize, out _, ref node))
                {
                    var path = Marshal.PtrToStringUni(new IntPtr(detailBuffer.ToInt64() + pathOffset)) ?? "";
                    var lowerPath = path.ToLower();

                    if (!PathMatchesFilter(path, filter))
                    {
                        continue;
                    }

                    var identity = CreateMetadataOnlyDevice(path, TryParseVidPid(path).vid ?? 0, TryParseVidPid(path).pid ?? 0);
                    if (filter?.InterfaceClass is byte cls && identity.InterfaceClass != cls ||
                        filter?.InterfaceSubClass is byte sub && identity.InterfaceSubClass != sub ||
                        filter?.InterfaceProtocol is byte protocol && identity.InterfaceProtocol != protocol)
                        continue;
                    var isGoogleWinUsb = lowerPath.Contains(PreferWinUsbVidPid);
                    var instanceId = GetDeviceInstanceId(devInfo, ref node);
                    var separator = instanceId.LastIndexOf('\\');
                    var candidate = separator >= 0 ? instanceId[(separator + 1)..] : null;
                    var serial = candidate is not null && !candidate.Contains('&') ? candidate : null;

                    UsbDevice? device = null;
                    if (isGoogleWinUsb)
                    {
                        UsbTrace.Log($"Prefers WinUSB for Google device: {path}");
                        device = TryOpenWinUSB(path, options, serial);
                    }

                    device ??= ProbeDevice(path, options, serial);

                    if (device != null)
                    {
                        device.DeviceInstanceId = instanceId;
                        device.ContainerId = ReadContainerId(device.DeviceInstanceId);
                        var key = BuildDeviceKey(device);
                        if (uniqueKeys.Add(key))
                        {
                            UsbTrace.Log($"Confirmed device added: key={key} using {(device is WinUSBDevice ? "WinUSB" : "Legacy")}");
                            devices.Add(device);
                        }
                        else
                        {
                            device.Dispose();
                        }
                    }
                    else
                    {
                        UsbTrace.Log($"WinUSBFinder: ProbeDevice returned null for [{path}]");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        UsbTrace.Log($"WinUSBFinder: EnumerateInterfacesCore done, lastErr={Marshal.GetLastWin32Error()}, devices={devices.Count}");
    }

}