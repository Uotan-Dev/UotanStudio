using System.Globalization;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal static class LinuxUsbFinder
{
    private const int O_RDONLY = 0;
    private const string DefaultSysfsDevicesRoot = "/sys/bus/usb/devices";
    private static int _permissionDeniedCount;
    private static int _busyCount;

    /// <summary>
    /// Gets the number of /dev/bus/usb devices skipped because the process lacked
    /// permission (EACCES) during the last enumeration. Lets upper layers distinguish
    /// "no device present" from "no permission" (e.g. missing udev rules).
    /// <para>获取上次枚举中因权限不足（EACCES）而跳过的设备数。让上层区分
    /// "没有设备"与"没有权限"（例如缺少 udev 规则）。</para>
    /// </summary>
    public static int PermissionDeniedCount => Volatile.Read(ref _permissionDeniedCount);

    /// <summary>
    /// Gets whether any /dev/bus/usb device was skipped due to missing permissions.
    /// <para>获取是否有设备因权限不足被跳过。</para>
    /// </summary>
    public static bool HasPermissionIssues => PermissionDeniedCount > 0;

    /// <summary>
    /// Gets the number of devices skipped because the interface could not be claimed
    /// (EBUSY - another process, e.g. an adb server, already claimed it).
    /// <para>获取因接口无法声明（EBUSY——其他进程，例如 adb server，已占用）而跳过的设备数。</para>
    /// </summary>
    public static int BusyCount => Volatile.Read(ref _busyCount);

    /// <summary>
    /// Gets whether any device was skipped because its interface is busy.
    /// <para>获取是否有设备因接口被占用而跳过。</para>
    /// </summary>
    public static bool HasBusyIssues => BusyCount > 0;

    /// <summary>
    /// Gets whether the usbfs root (<c>/dev/bus/usb</c>) existed during the last
    /// enumeration. Lets upper layers distinguish "no USB filesystem mounted" from
    /// "filesystem present but no devices" on device-less CI.
    /// <para>获取上次枚举时 usbfs 根目录（<c>/dev/bus/usb</c>）是否存在。让上层区分
    /// "未挂载 USB 文件系统"与"文件系统存在但无设备"（无设备 CI 场景）。</para>
    /// </summary>
    public static bool LastUsbfsRootExists { get; private set; }

    /// <summary>
    /// Gets the number of device nodes scanned during the last enumeration (regardless of
    /// whether each open succeeded). A non-zero value proves the scan loop actually ran.
    /// <para>获取上次枚举中扫描的设备节点数（无论每个节点是否打开成功）。
    /// 非零值证明扫描循环确实执行过。</para>
    /// </summary>
    public static int LastScannedNodes { get; private set; }

    /// <summary>
    /// Gets the number of devices matched during the last enumeration.
    /// <para>获取上次枚举中匹配到的设备数。</para>
    /// </summary>
    public static int LastMatchedDeviceCount { get; private set; }

    /// <summary>
    /// Records a permission-denied open attempt and logs a udev hint. Used by the finder
    /// and by <see cref="LinuxUsbDevice.CreateHandle"/>.
    /// <para>记录一次权限不足的打开尝试并记录 udev 提示。由查找器与
    /// <see cref="LinuxUsbDevice.CreateHandle"/> 使用。</para>
    /// </summary>
    /// <param name="path">The device node path. <para>设备节点路径。</para></param>
    internal static void ReportPermissionDenied(string path)
    {
        Interlocked.Increment(ref _permissionDeniedCount);
        UsbTrace.Log($"LinuxUsbFinder: permission denied opening '{path}' (EACCES) - ensure udev rules grant access to the current user/group.");
    }

    /// <summary>
    /// Records an interface-claim failure (EBUSY) and logs a hint. Used by
    /// <see cref="LinuxUsbDevice.CreateHandle"/>.
    /// <para>记录一次接口声明失败（EBUSY）并记录提示。由
    /// <see cref="LinuxUsbDevice.CreateHandle"/> 使用。</para>
    /// </summary>
    /// <param name="path">The device node path. <para>设备节点路径。</para></param>
    internal static void ReportBusy(string path)
    {
        Interlocked.Increment(ref _busyCount);
        UsbTrace.Log($"LinuxUsbFinder: interface busy on '{path}' (EBUSY) - another process (e.g. adb server) may have claimed it.");
    }

    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null, string? usbfsRoot = null)
    {
        var devices = new List<UsbDevice>();
        Volatile.Write(ref _permissionDeniedCount, 0);
        Volatile.Write(ref _busyCount, 0);
        const string default_base_path = "/dev/bus/usb";
        var base_path = usbfsRoot ?? default_base_path;
        LastUsbfsRootExists = Directory.Exists(base_path);
        LastScannedNodes = 0;
        LastMatchedDeviceCount = 0;
        if (!LastUsbfsRootExists)
        {
            return devices;
        }

        Dictionary<(int Bus, int Dev), string>? sysfsDeviceIndex = null;
        foreach (var bus_dir in Directory.GetDirectories(base_path))
        {
            foreach (var dev_path in Directory.GetFiles(bus_dir))
            {
                LastScannedNodes++;
                var fd = open(dev_path, O_RDONLY | O_CLOEXEC);
                if (fd < 0)
                {
                    if (Marshal.GetLastWin32Error() == EACCES)
                    {
                        ReportPermissionDenied(dev_path);
                    }

                    continue;
                }

                try
                {
                    var desc = LinuxUsbDescriptors.Read(fd);
                    if (desc is null)
                    {
                        continue;
                    }

                    var info = TryParseDescriptor(desc, desc.Length, filter);
                    if (info == null)
                    {
                        continue;
                    }

                    sysfsDeviceIndex ??= BuildSysfsDeviceIndex(DefaultSysfsDevicesRoot);
                    var dev = new LinuxUsbDevice
                    {
                        DevicePath = dev_path,
                        PhysicalPortPath = LinuxUsbPortIdentity.Observe(fd, dev_path),
                        VendorId = info.VendorId,
                        ProductId = info.ProductId,
                        BcdUsb = info.BcdUsb,
                        InterfaceClass = info.InterfaceClass,
                        InterfaceSubClass = info.InterfaceSubClass,
                        InterfaceProtocol = info.InterfaceProtocol,
                        InterfaceMetadataObserved = true,
                        Speed = ResolveSpeed(dev_path, info.BcdUsb, sysfsDeviceIndex),
                        Interfaces = info.Interfaces,
                        Ep_in = info.EndpointIn,
                        Ep_out = info.EndpointOut,
                        InterfaceId = info.InterfaceId,
                        ClaimedInterfaceIds = filter?.InterfaceNumbers?.ToArray() ?? [],
                        ISerialNumber = info.ISerialNumber,
                        UsbDeviceType = UsbDeviceType.Linux,
                        SerialNumber = null
                    };

                    devices.Add(dev);
                }
                catch (Exception ex)
                {
                    UsbTrace.Log($"LinuxUsbFinder failed for path '{dev_path}': {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (fd >= 0)
                    {
                        close(fd);
                    }

                    fd = -1;
                }
            }
        }
        LastMatchedDeviceCount = devices.Count;
        return devices;
    }

    /// <summary>
    /// Parses a raw usbfs device + configuration descriptor into device metadata and the
    /// matched interface/endpoint pair, applying the optional filter. Pure function with no
    /// I/O — testable without hardware by feeding constructed descriptor bytes.
    /// <para>将原始 usbfs 设备+配置描述符解析为设备元数据及匹配的接口/端点对，
    /// 并应用可选过滤器。纯函数、无 I/O——可通过构造描述符字节在无硬件时测试。</para>
    /// </summary>
    /// <param name="desc">The raw descriptor bytes read from the device node. <para>从设备节点读取的原始描述符字节。</para></param>
    /// <param name="length">The number of valid bytes in <paramref name="desc"/>. <para><paramref name="desc"/> 中的有效字节数。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The parsed metadata when a matching interface was found; otherwise <c>null</c>. <para>找到匹配接口时返回解析元数据；否则返回 <c>null</c>。</para></returns>
    internal static LinuxUsbDescriptorInfo? TryParseDescriptor(byte[] desc, int length, UsbDeviceFilter? filter)
    {
        if (desc == null || length < 18 || length > desc.Length || desc[0] != 18 || desc[1] != 1)
        {
            return null;
        }

        var idVendor = (ushort)(desc[8] | (desc[9] << 8));
        var idProduct = (ushort)(desc[10] | (desc[11] << 8));
        var iSerialNumber = desc[16];
        var bcdUsb = (ushort)(desc[2] | (desc[3] << 8));

        if (filter?.VendorId is ushort filterVid && idVendor != filterVid)
        {
            return null;
        }

        if (filter?.ProductId is ushort filterPid && idProduct != filterPid)
        {
            return null;
        }

        var interfaces = new List<UsbInterfaceInfo>();

        int pos = desc[0];
        while (pos < length - 1)
        {
            int len = desc[pos];
            if (len < 2 || pos + len > length)
            {
                break;
            }

            var type = desc[pos + 1];

            if (type == 0x04 && len >= 9)
            {
                var ifcClass = desc[pos + 5];
                var ifcSubClass = desc[pos + 6];
                var ifcProtocol = desc[pos + 7];
                var ifcId = desc[pos + 2];
                var numEpts = desc[pos + 4];

                var iface = new UsbInterfaceInfo
                {
                    InterfaceNumber = ifcId,
                    Class = ifcClass,
                    SubClass = ifcSubClass,
                    Protocol = ifcProtocol
                };
                var endpoints = new List<UsbEndpointInfo>();

                var ept_pos = pos + len;
                var checked_epts = 0;
                while (ept_pos < length - 1 && checked_epts < numEpts)
                {
                    int ept_len = desc[ept_pos];
                    if (ept_len < 2 || ept_pos + ept_len > length)
                    {
                        break;
                    }

                    var ept_type = desc[ept_pos + 1];
                    if (ept_type is 0x04 or 0x02)
                    {
                        break;
                    }

                    if (ept_type == 0x05 && ept_len >= 7)
                    {
                        var addr = desc[ept_pos + 2];
                        var attr = desc[ept_pos + 3];
                        var maxPacket = (ushort)(desc[ept_pos + 4] | (desc[ept_pos + 5] << 8));
                        endpoints.Add(new UsbEndpointInfo
                        {
                            EndpointAddress = addr,
                            Attributes = attr,
                            MaxPacketSize = maxPacket,
                            Interval = desc[ept_pos + 6]
                        });
                        checked_epts++;
                    }
                    ept_pos += ept_len;
                }
                iface.Endpoints = endpoints;
                interfaces.Add(iface);
            }
            pos += len;
        }

        byte epIn = 0, epOut = 0;
        byte matchedIfcClass = 0, matchedIfcSubClass = 0, matchedIfcProtocol = 0, matchedIfcId = 0;
        var matched = false;
        foreach (var iface in interfaces)
        {
            epIn = 0;
            epOut = 0;
            var ifcClass = iface.Class;
            var ifcSubClass = iface.SubClass;
            var ifcProtocol = iface.Protocol;
            var ifcId = iface.InterfaceNumber;
            var endpoints = iface.Endpoints;

            var matchesFilter = InterfaceMatchesFilter(ifcClass, ifcSubClass, ifcProtocol, filter) &&
                (filter?.InterfaceNumber is not byte fn || ifcId == fn);
            if (!matchesFilter)
            {
                continue;
            }

            foreach (var ep in endpoints)
            {
                var epType = ep.Attributes & 0x03;
                if (epType is not 0x02 and not 0x03)
                {
                    continue;
                }

                var isIn = (ep.EndpointAddress & 0x80) != 0;
                if (isIn)
                {
                    if (epIn == 0)
                    {
                        epIn = ep.EndpointAddress;
                    }

                    if (filter?.EndpointAddressIn == ep.EndpointAddress)
                    {
                        epIn = ep.EndpointAddress;
                    }
                }
                else
                {
                    if (epOut == 0)
                    {
                        epOut = ep.EndpointAddress;
                    }

                    if (filter?.EndpointAddressOut == ep.EndpointAddress)
                    {
                        epOut = ep.EndpointAddress;
                    }
                }
            }

            var inOk = filter?.EndpointAddressIn is not byte reqIn ||
                endpoints.Any(e => (e.EndpointAddress & 0x80) != 0 && e.EndpointAddress == reqIn);
            var outOk = filter?.EndpointAddressOut is not byte reqOut ||
                endpoints.Any(e => (e.EndpointAddress & 0x80) == 0 && e.EndpointAddress == reqOut);
            if (inOk && outOk)
            {
                epIn = filter?.EndpointAddressIn ?? epIn;
                epOut = filter?.EndpointAddressOut ?? epOut;
            }
            else
            {
                epIn = 0;
                epOut = 0;
                continue;
            }

            var pairOk = epIn != 0 && epOut != 0;
            var inOnlyOk = epIn != 0 &&
                filter?.EndpointAddressIn is byte &&
                filter?.EndpointAddressOut == null;
            if (pairOk || inOnlyOk)
            {
                matchedIfcClass = ifcClass;
                matchedIfcSubClass = ifcSubClass;
                matchedIfcProtocol = ifcProtocol;
                matchedIfcId = ifcId;
                matched = true;
                break;
            }
        }

        return !matched
            ? null
            : new LinuxUsbDescriptorInfo
            {
                VendorId = idVendor,
                ProductId = idProduct,
                ISerialNumber = iSerialNumber,
                BcdUsb = bcdUsb,
                Interfaces = interfaces,
                EndpointIn = epIn,
                EndpointOut = epOut,
                InterfaceClass = matchedIfcClass,
                InterfaceSubClass = matchedIfcSubClass,
                InterfaceProtocol = matchedIfcProtocol,
                InterfaceId = matchedIfcId
            };
    }

    /// <summary>
    /// Parsed usbfs descriptor metadata: device ids plus the matched interface/endpoint pair.
    /// <para>解析出的 usbfs 描述符元数据：设备 ID 及匹配的接口/端点对。</para>
    /// </summary>
    internal sealed class LinuxUsbDescriptorInfo
    {
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public byte ISerialNumber { get; set; }
        public ushort BcdUsb { get; set; }
        public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; set; } = [];
        public byte EndpointIn { get; set; }
        public byte EndpointOut { get; set; }
        public byte InterfaceClass { get; set; }
        public byte InterfaceSubClass { get; set; }
        public byte InterfaceProtocol { get; set; }
        public byte InterfaceId { get; set; }
    }

    private static bool InterfaceMatchesFilter(byte interfaceClass, byte interfaceSubClass, byte interfaceProtocol, UsbDeviceFilter? filter)
        => (filter?.InterfaceClass is not byte c || interfaceClass == c) && (filter?.InterfaceSubClass is not byte s || interfaceSubClass == s) && (filter?.InterfaceProtocol is not byte p || interfaceProtocol == p);

    /// <summary>
    /// Approximates the USB speed from the device descriptor's BcdUSB version.
    /// <para>根据设备描述符的 BcdUSB 版本近似推断 USB 速度。</para>
    /// This reflects the device's declared USB spec version rather than the negotiated
    /// link speed; good enough for discovery hints (e.g. EDL USB3 vs USB2 paths).
    /// <para>反映设备声明的 USB 规范版本而非协商链路速度；作为发现提示足够
    /// （例如 EDL 区分 USB3/USB2 路径）。</para>
    /// </summary>
    private static UsbDeviceSpeed InferSpeed(ushort bcdUsb)
        => UsbSpeedInference.FromBcdUsb(bcdUsb);

    /// <summary>
    /// Resolves the negotiated link speed: prefer the sysfs <c>speed</c> file of the
    /// device matched by bus/device number, falling back to BcdUSB inference.
    /// <para>解析协商链路速度：优先读取按总线/设备号匹配到的 sysfs <c>speed</c> 文件，
    /// 失败时回退到 BcdUSB 推断。</para>
    /// sysfs names devices by <c>bus-port</c> (e.g. <c>1-1</c>), not by device number,
    /// so the directory is located via the <c>busnum</c>/<c>devnum</c> attributes.
    /// <para>sysfs 按 <c>bus-port</c>（如 <c>1-1</c>）而非设备号命名设备目录，
    /// 因此通过 <c>busnum</c>/<c>devnum</c> 属性定位目录。</para>
    /// The sysfs root is injectable for tests; production uses the real path. This overload builds
    /// a single-pass index for a standalone resolution; whole-directory enumeration shares one index.
    /// <para>sysfs 根目录可注入以便测试；生产环境使用真实路径。本重载为独立解析建立一次单趟
    /// 索引；整目录枚举则共享同一份索引。</para>
    /// </summary>
    /// <param name="devPath">The usbfs device node path. <para>usbfs 设备节点路径。</para></param>
    /// <param name="bcdUsb">The device descriptor's BcdUSB field. <para>设备描述符的 BcdUSB 字段。</para></param>
    /// <param name="sysfsDevicesRoot">The sysfs USB devices root. <para>sysfs USB 设备根目录。</para></param>
    /// <returns>The resolved speed. <para>解析出的速度。</para></returns>
    internal static UsbDeviceSpeed ResolveSpeed(
        string devPath,
        ushort bcdUsb,
        string sysfsDevicesRoot = DefaultSysfsDevicesRoot)
        => ResolveSpeed(devPath, bcdUsb, BuildSysfsDeviceIndex(sysfsDevicesRoot));

    /// <summary>
    /// Resolves the speed from an index built once per enumeration instead of rescanning the whole
    /// sysfs directory for every device.
    /// <para>使用单轮枚举建立一次的索引解析速度，避免为每个设备重扫整个 sysfs 目录。</para>
    /// </summary>
    /// <param name="devPath">The usbfs device node path. <para>usbfs 设备节点路径。</para></param>
    /// <param name="bcdUsb">The device descriptor's BcdUSB field. <para>设备描述符的 BcdUSB 字段。</para></param>
    /// <param name="sysfsDeviceIndex">Bus/device number to sysfs directory map. <para>总线/设备号到 sysfs 目录的映射。</para></param>
    /// <returns>The resolved speed. <para>解析出的速度。</para></returns>
    private static UsbDeviceSpeed ResolveSpeed(
        string devPath,
        ushort bcdUsb,
        IReadOnlyDictionary<(int Bus, int Dev), string> sysfsDeviceIndex)
    {
        try
        {
            var busPart = Path.GetFileName(Path.GetDirectoryName(devPath));
            var devPart = Path.GetFileName(devPath);
            if (int.TryParse(busPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var busNum) &&
                int.TryParse(devPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var devNum) &&
                sysfsDeviceIndex.TryGetValue((busNum, devNum), out var deviceDir))
            {
                var sysfsSpeed = Path.Combine(deviceDir, "speed");
                if (double.TryParse(File.ReadAllText(sysfsSpeed).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps))
                {
                    return UsbSpeedInference.FromMbps(mbps);
                }
            }
        }
        catch
        {
        }

        return InferSpeed(bcdUsb);
    }

    /// <summary>
    /// Builds a bus/device-number to sysfs directory index in one pass over the sysfs USB devices
    /// root. The index is valid only for the enumeration that built it: no cross-call cache is kept,
    /// so a hotplug between two scans is always visible.
    /// <para>对 sysfs USB 设备根目录做一次遍历，建立总线/设备号到目录的索引。索引仅在建立它的
    /// 那一轮枚举内有效：不保留跨调用缓存，因此两次扫描之间的热插拔总会被观察到。</para>
    /// </summary>
    /// <param name="sysfsDevicesRoot">The sysfs USB devices root. <para>sysfs USB 设备根目录。</para></param>
    /// <returns>Bus/device number to directory map; empty when the root is unreadable.
    /// <para>总线/设备号到目录的映射；根目录不可读时为空。</para></returns>
    private static Dictionary<(int Bus, int Dev), string> BuildSysfsDeviceIndex(string sysfsDevicesRoot)
    {
        var index = new Dictionary<(int Bus, int Dev), string>();
        try
        {
            foreach (var deviceDir in Directory.EnumerateDirectories(sysfsDevicesRoot))
            {
                if (TryReadSysfsInt(Path.Combine(deviceDir, "busnum"), out var busNum) &&
                    TryReadSysfsInt(Path.Combine(deviceDir, "devnum"), out var devNum))
                {
                    index[(busNum, devNum)] = deviceDir;
                }
            }
        }
        catch
        {
        }

        return index;
    }

    private static bool TryReadSysfsInt(string path, out int value)
    {
        value = 0;
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch
        {
            return false;
        }
    }

}