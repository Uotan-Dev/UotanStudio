using System.IO.Ports;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// When the WinUSB direct open of an EDL endpoint fails because the target is bound by a
/// QDLoader-style serial driver as a COM port, this resolves the owning serial port from the
/// endpoint's exact device path and opens it. Resolution is fail-closed: only a node exactly
/// associated with the endpoint device node (the node itself or one of its direct children) whose
/// class GUID is Ports (<c>{4d36e978-e325-11ce-bfc1-08002be10318}</c>) and whose <c>COMn</c> port
/// name can be resolved is accepted; the driver service name is deliberately not restricted — on
/// real machines the QDLoader binding may land on Microsoft's <c>usbser</c> or on Qualcomm's own
/// <c>qcwdfserial</c> serial service, and identity is guaranteed by the exact parent match rather
/// than by a driver name. A node bound to the WinUSB driver never qualifies: a WinUSB binding
/// exposes no serial port, so a leftover COM-port name or interface from a former serial driver
/// must not be opened. Any failed step returns <see langword="null"/> and the caller keeps the
/// original exception semantics. Every call re-resolves the current PnP state and never caches the
/// port name, since the QDLoader port number may change across re-enumerations.
/// <para>当 EDL 端点的 WinUSB 直开失败、且目标被 QDLoader 类串口驱动绑定为 COM 口时，从端点的
/// 精确设备路径解析所属串口并打开。解析始终 fail-closed：只有与端点设备节点精确关联（节点自身或
/// 其直接子节点）、类 GUID 为 Ports（<c>{4d36e978-e325-11ce-bfc1-08002be10318}</c>）且能解析出
/// <c>COMn</c> 端口名的节点才会被采纳；驱动服务名不作限定——真机上的 QDLoader 既可能落到
/// Microsoft <c>usbser</c>，也可能落到高通自己的 <c>qcwdfserial</c> 等串口服务，而身份保证来自
/// 父节点精确匹配而非驱动名。已绑定 WinUSB 驱动的节点一律不采纳：WinUSB 绑定不暴露串口，
/// 旧串口驱动残留的端口名或接口也不得被打开。任何一步失败都返回 <see langword="null"/>，
/// 由调用方维持原异常语义。每次调用都按当前 PnP 状态重新解析，不缓存端口名（QDLoader
/// 重枚举后端口号可能变化）。</para>
/// </summary>
internal static partial class EdlSerialPortFallback
{
    private const int FallbackBaudRate = 115200;

    private const int MaxInstanceIdChars = 200;

    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    [GeneratedRegex("^COM\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ComPortNamePattern();

    /// <summary>
    /// Normalizes a Windows device-interface path into a PnP device instance id. Endpoint keys
    /// carry the device-interface path (e.g. <c>\\?\usb#vid_05c6&amp;pid_9008#20000030#{a5dcbf10-…}</c>)
    /// while <see cref="UsbPnpNative.TryLocateDevNode"/> needs the instance id
    /// (e.g. <c>USB\VID_05C6&amp;PID_9008\20000030</c>). The conversion is pure string work and
    /// never touches system state.
    /// <para>把 Windows 设备接口路径归一化为 PnP 设备实例标识。端点键携带的是设备接口路径
    /// （如 <c>\\?\usb#vid_05c6&amp;pid_9008#20000030#{a5dcbf10-…}</c>），而
    /// <see cref="UsbPnpNative.TryLocateDevNode"/> 需要实例标识
    /// （如 <c>USB\VID_05C6&amp;PID_9008\20000030</c>）。转换是纯字符串操作，不触及系统状态。</para>
    /// </summary>
    /// <param name="devicePath">
    /// The device-interface path from a USB endpoint key. <para>USB 端点键中的设备接口路径。</para>
    /// </param>
    /// <returns>
    /// The PnP instance id, or <see langword="null"/> when the path is not a USB device-interface
    /// path of plausible length. <para>PnP 实例标识；路径不是长度合理的 USB 设备接口路径时为
    /// <see langword="null"/>。</para>
    /// </returns>
    internal static string? TryDeriveInstanceId(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        var path = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var guidMarker = path.IndexOf("#{", StringComparison.Ordinal);
        if (guidMarker >= 0)
        {
            path = path[..guidMarker];
        }

        path = path.Replace('#', '\\');
        if (path.Length == 0 || path.Length > MaxInstanceIdChars ||
            !path.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Resolves the QDLoader serial port owned by the endpoint device node and returns a connected
    /// transport. Candidates are walked "node itself first, then direct children": for a
    /// single-interface QDLoader device (the shape observed on real hardware) the Ports node is the
    /// endpoint device node itself, while for composite shapes the serial driver binds to the
    /// <c>&amp;MI_00</c> child obtained via <see cref="UsbPnpNative.TryGetChild"/>/<see cref="UsbPnpNative.TryGetSibling"/>,
    /// which by construction only covers direct descendants of the endpoint node. The port is
    /// opened at 115200 / 8N1 / no handshake, matching the host's existing EDL serial path.
    /// <para>解析端点设备节点所属的 QDLoader 串口，打开并返回已连接的传输。候选节点按
    /// 「节点自身优先、直接子节点其次」遍历：单接口 QDLoader 设备（真机证据形态）的 Ports
    /// 节点就是端点设备节点本身；复合设备形态下串口驱动绑定在 <c>&amp;MI_00</c> 子节点上，
    /// 子节点经 <see cref="UsbPnpNative.TryGetChild"/>/<see cref="UsbPnpNative.TryGetSibling"/>
    /// 取得，天然只覆盖端点节点的直接后代。打开参数为 115200 / 8N1 / 无流控，与宿主现有
    /// EDL 串口路径一致。</para>
    /// </summary>
    /// <param name="devicePath">
    /// The exact device-interface path of the endpoint. <para>端点的精确设备接口路径。</para>
    /// </param>
    /// <returns>
    /// A connected <see cref="SerialTransport"/> on the resolved COM port, or <see langword="null"/>
    /// when any resolution or open step fails. <para>解析并打开成功时返回已连接的
    /// <see cref="SerialTransport"/>；任一解析或打开步骤失败时为 <see langword="null"/>。</para>
    /// </returns>
    internal static SerialTransport? TryOpenConnected(string? devicePath)
    {
        if (OperatingSystem.IsLinux())
        {
            return TryOpenLinuxTty(devicePath);
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var instanceId = TryDeriveInstanceId(devicePath);
        if (instanceId is null || !UsbPnpNative.TryLocateDevNode(instanceId, out var devInst))
        {
            UsbTrace.LogFormatted($"EDL serial fallback: device node '{devicePath}' cannot be located.");
            return null;
        }

        var portName = TryReadSerialPortName(devInst) ?? ReadChildSerialPortName(devInst);
        if (portName is null)
        {
            UsbTrace.LogFormatted($"EDL serial fallback: no Ports-class child with a port name under '{instanceId}'.");
            return null;
        }

        var transport = new SerialTransport(portName, FallbackBaudRate, Parity.None, 8, StopBits.One, Handshake.None);
        try
        {
            transport.Connect();
        }
        catch (Exception ex)
        {
            transport.Dispose();
            UsbTrace.LogFormatted($"EDL serial fallback: opening '{portName}' failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        UsbTrace.LogFormatted($"EDL serial fallback: '{devicePath}' opened as '{portName}' at {FallbackBaudRate} baud.");
        return transport;
    }

    /// <summary>
    /// Resolves the QDLoader tty node Linux bound to the endpoint's usbfs path and opens it. The bus and device
    /// numbers in <c>/dev/bus/usb/BBB/DDD</c> identify exactly one USB device, and <c>/sys/bus/usb/devices</c> maps
    /// that device to the <c>ttyUSB*</c> interfaces the serial driver created for it, so identity comes from the
    /// kernel's own topology rather than from a port name that shifts across re-enumerations. A device with no tty
    /// interface returns <see langword="null"/>, which is what a WinUSB-bound node looks like.
    /// <para>解析 Linux 为端点 usbfs 路径绑定的 QDLoader tty 节点并打开。<c>/dev/bus/usb/BBB/DDD</c> 中的总线号与设备号
    /// 唯一确定一个 USB 设备，<c>/sys/bus/usb/devices</c> 把该设备映射到串口驱动为它创建的 <c>ttyUSB*</c> 接口，因此
    /// 身份来自内核自身的拓扑，而不是会在重枚举后改变端口号的名称。没有 tty 接口的设备返回
    /// <see langword="null"/>，这正是绑定了 WinUSB 的节点的形态。</para>
    /// </summary>
    /// <param name="devicePath">The usbfs path, such as <c>/dev/bus/usb/005/010</c>.
    /// <para>usbfs 路径，例如 <c>/dev/bus/usb/005/010</c>。</para></param>
    /// <returns>A connected transport on the resolved tty node, or <see langword="null"/>.
    /// <para>已连接的、位于解析出的 tty 节点上的传输；或 <see langword="null"/>。</para></returns>
    private static SerialTransport? TryOpenLinuxTty(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        // /dev/bus/usb/BBB/DDD carries the bus and device numbers the kernel assigned.
        var segments = devicePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !int.TryParse(segments[^2], out var bus)
            || !int.TryParse(segments[^1], out var device))
        {
            UsbTrace.LogFormatted($"EDL serial fallback: '{devicePath}' is not a usbfs path with bus/device numbers.");
            return null;
        }

        string? portName = null;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories("/sys/bus/usb/devices"))
            {
                var busNumber = ReadSysfsInt(candidate, "busnum");
                var deviceNumber = ReadSysfsInt(candidate, "devnum");
                if (busNumber != bus || deviceNumber != device)
                {
                    continue;
                }

                portName = FindTtyInterface(candidate);
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UsbTrace.LogFormatted($"EDL serial fallback: reading /sys/bus/usb/devices failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (portName is null)
        {
            UsbTrace.LogFormatted(
                $"EDL serial fallback: usbfs device {bus}/{device} exposes no tty interface; no serial port to open.");
            return null;
        }

        var linuxTransport = new SerialTransport(portName, FallbackBaudRate, Parity.None, 8, StopBits.One, Handshake.None);
        try
        {
            linuxTransport.Connect();
        }
        catch (Exception ex)
        {
            linuxTransport.Dispose();
            UsbTrace.LogFormatted($"EDL serial fallback: opening '{portName}' failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        UsbTrace.LogFormatted($"EDL serial fallback: '{devicePath}' opened as '{portName}' at {FallbackBaudRate} baud.");
        return linuxTransport;
    }

    /// <summary>
    /// Returns the <c>/dev/ttyUSB*</c> node one USB device directory owns, or <see langword="null"/> when it has no
    /// tty interface. The primary mapping walks the kernel's own <c>/sys/class/tty</c> registrations and accepts
    /// the class device whose <c>device</c> link resolves under the interface directory, then reads the node name
    /// from that class device's <c>uevent</c> export, so the port comes from the kernel's authoritative device
    /// registry instead of any name inference. Kernels whose bus-side device directory carries the tty attributes
    /// directly still resolve through the bus-side scan as the fallback.
    /// <para>返回某个 USB 设备目录所拥有的 <c>/dev/ttyUSB*</c> 节点；该设备没有 tty 接口时返回
    /// <see langword="null"/>。主映射遍历内核自身的 <c>/sys/class/tty</c> 注册表，接受 <c>device</c> 链接解析后落在
    /// 该接口目录之下的类设备，并从其 <c>uevent</c> 导出读取节点名——端口来自内核权威的设备注册表，不做任何
    /// 名字推测。总线侧设备目录直接携带 tty 属性的内核仍可经总线侧扫描作为回退解析。</para>
    /// </summary>
    /// <param name="deviceDirectory">A device directory under <c>/sys/bus/usb/devices</c>.
    /// <para><c>/sys/bus/usb/devices</c> 下的设备目录。</para></param>
    /// <returns>A device node path such as <c>/dev/ttyUSB0</c>, or <see langword="null"/>.
    /// <para>形如 <c>/dev/ttyUSB0</c> 的设备节点路径；或 <see langword="null"/>。</para></returns>
    private static string? FindTtyInterface(string deviceDirectory)
    {
        var classNode = FindClassTtyNode(deviceDirectory);
        if (classNode is not null)
        {
            return classNode;
        }

        string[] interfaces;
        try
        {
            interfaces = [.. Directory.GetDirectories(deviceDirectory).OrderBy(name => name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var interfaceDirectory in interfaces)
        {
            var ttyName = ReadTtyName(interfaceDirectory);
            if (ttyName is not null)
            {
                return ttyName;
            }

            string[] ttyEntries;
            try
            {
                ttyEntries = Directory.GetDirectories(interfaceDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var ttyEntry in ttyEntries)
            {
                if (ReadTtyName(ttyEntry) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the kernel's <c>/sys/class/tty</c> registrations and returns the <c>/dev</c> node of the class tty
    /// device whose <c>device</c> link resolves under the interface directory, reading the node name from the
    /// class device's own <c>uevent</c> export. This is the kernel's authoritative port mapping: the class entry
    /// exists exactly when the kernel registered the port, and its <c>DEVNAME</c> names the node udev created.
    /// <para>遍历内核的 <c>/sys/class/tty</c> 注册表，返回其 <c>device</c> 链接解析后落在该接口目录之下的类 tty
    /// 设备的 <c>/dev</c> 节点，节点名取自类设备自身的 <c>uevent</c> 导出。这是内核权威的端口映射：类条目存在
    /// 当且仅当内核注册了该端口，其 <c>DEVNAME</c> 命名 udev 创建的节点。</para>
    /// </summary>
    /// <param name="deviceDirectory">The USB device directory the port must belong to.
    /// <para>端口必须归属的 USB 设备目录。</para></param>
    /// <returns>A device node path such as <c>/dev/ttyUSB0</c>, or <see langword="null"/>.
    /// <para>形如 <c>/dev/ttyUSB0</c> 的设备节点路径；或 <see langword="null"/>。</para></returns>
    private static string? FindClassTtyNode(string deviceDirectory)
    {
        var interfacePath = ResolveSysfsPath(deviceDirectory);
        if (interfacePath is null)
        {
            return null;
        }

        try
        {
            foreach (var classDirectory in Directory.GetDirectories("/sys/class/tty")
                .OrderBy(name => name, StringComparer.Ordinal))
            {
                var deviceLink = Path.Combine(classDirectory, "device");
                if (!Directory.Exists(deviceLink))
                {
                    continue;
                }

                var devicePath = ResolveSysfsPath(deviceLink);
                if (devicePath is null ||
                    !devicePath.StartsWith(interfacePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                var node = DevNodeFor(classDirectory);
                if (node is not null)
                {
                    return node;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Follows a sysfs symlink chain to the real device directory it points at, so ownership checks compare
    /// canonical paths instead of the aliasing <c>/sys/bus/usb/devices</c> and <c>/sys/class</c> views.
    /// <para>沿 sysfs 符号链接链解析到其指向的真实设备目录，使归属检查比较规范路径，而不是
    /// <c>/sys/bus/usb/devices</c> 与 <c>/sys/class</c> 的别名视图。</para>
    /// </summary>
    /// <param name="path">The sysfs path to resolve. <para>要解析的 sysfs 路径。</para></param>
    /// <returns>The fully resolved directory path, or <see langword="null"/> when the chain does not settle.
    /// <para>完全解析后的目录路径；链接链未收敛时为 <see langword="null"/>。</para></returns>
    private static string? ResolveSysfsPath(string path)
    {
        try
        {
            var current = path;
            for (var hop = 0; hop < 8; hop++)
            {
                var dir = new DirectoryInfo(current);
                if (dir.LinkTarget is not { } target)
                {
                    return dir.FullName;
                }

                current = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir.FullName, target));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the <c>/dev/ttyUSB*</c> node a usb-serial interface directory owns, or <see langword="null"/> when it
    /// has no tty interface. A tty class device is recognized by its <c>dev</c> attribute and a <c>subsystem</c>
    /// link that points at the <c>tty</c> subsystem, which resolves both sysfs layouts kernels ship: the grouped
    /// <c>interface/tty/ttyUSBn</c> shape and the flat <c>interface/ttyUSBn</c> shape, so the port name always
    /// comes from the device directory the kernel actually registered.
    /// <para>返回 usb-serial 接口目录所拥有的 <c>/dev/ttyUSB*</c> 节点；该设备没有 tty 接口时返回
    /// <see langword="null"/>。tty 类设备由其 <c>dev</c> 属性与指向 <c>tty</c> 子系统的 <c>subsystem</c> 链接识别，
    /// 因此同时覆盖内核的两种 sysfs 布局：分组式 <c>接口/tty/ttyUSBn</c> 与扁平式 <c>接口/ttyUSBn</c>，
    /// 端口名始终取自内核实际注册的设备目录。</para>
    /// </summary>
    /// <param name="directory">An interface or tty directory under a USB device.
    /// <para>USB 设备下的接口或 tty 目录。</para></param>
    /// <returns>A device node path such as <c>/dev/ttyUSB0</c>, or <see langword="null"/>.
    /// <para>形如 <c>/dev/ttyUSB0</c> 的设备节点路径；或 <see langword="null"/>。</para></returns>
    private static string? ReadTtyName(string directory)
    {
        try
        {
            if (IsTtyClassDevice(directory))
            {
                return DevNodeFor(directory);
            }

            var ttyGroup = Path.Combine(directory, "tty");
            if (!Directory.Exists(ttyGroup))
            {
                return null;
            }

            foreach (var child in Directory.EnumerateDirectories(ttyGroup).OrderBy(name => name, StringComparer.Ordinal))
            {
                if (IsTtyClassDevice(child))
                {
                    return DevNodeFor(child);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports whether a sysfs directory is the class-device directory of a tty: it carries a <c>dev</c> attribute
    /// and a <c>subsystem</c> link naming the tty subsystem, which together distinguish a registered tty from any
    /// plain folder in the USB hierarchy.
    /// <para>报告一个 sysfs 目录是否为 tty 的类设备目录：它必须带有 <c>dev</c> 属性且 <c>subsystem</c> 链接指向
    /// tty 子系统，二者共同把已注册的 tty 与 USB 层级中的普通目录区分开。</para>
    /// </summary>
    /// <param name="directory">The sysfs directory to inspect. <para>要检查的 sysfs 目录。</para></param>
    /// <returns><see langword="true"/> when the directory registers a tty device.
    /// <para>目录注册的是 tty 设备时为 <see langword="true"/>。</para></returns>
    private static bool IsTtyClassDevice(string directory)
    {
        try
        {
            if (!File.Exists(Path.Combine(directory, "dev")))
            {
                return false;
            }

            var subsystemLink = Path.Combine(directory, "subsystem");
            var target = new DirectoryInfo(subsystemLink).LinkTarget;
            return target is not null &&
                string.Equals(Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    "tty", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps a tty class-device directory onto its <c>/dev</c> node by reading the kernel's own <c>uevent</c>
    /// export: <c>DEVNAME</c> names exactly the node udev created for the device, so the resolved path is the
    /// kernel's authoritative mapping rather than a name inferred from the directory hierarchy.
    /// <para>通过读取内核自身的 <c>uevent</c> 导出把 tty 类设备目录映射到其 <c>/dev</c> 节点：<c>DEVNAME</c>
    /// 给出的正是 udev 为该设备创建的节点名，因此解析出的路径是内核的权威映射，而不是从目录层级推测的名字。</para>
    /// </summary>
    /// <param name="ttyDirectory">A directory that <see cref="IsTtyClassDevice"/> accepted.
    /// <para>经 <see cref="IsTtyClassDevice"/> 判定的目录。</para></param>
    /// <returns>The <c>/dev/&lt;name&gt;</c> node path, or <see langword="null"/> when the kernel has not created the node.
    /// <para><c>/dev/&lt;name&gt;</c> 节点路径；内核尚未创建该节点时为 <see langword="null"/>。</para></returns>
    private static string? DevNodeFor(string ttyDirectory)
    {
        try
        {
            var uevent = Path.Combine(ttyDirectory, "uevent");
            if (!File.Exists(uevent))
            {
                return null;
            }

            foreach (var line in File.ReadLines(uevent))
            {
                if (!line.StartsWith("DEVNAME=", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = line["DEVNAME=".Length..].Trim();
                if (name.Length == 0)
                {
                    return null;
                }

                var node = Path.Combine("/dev", name);
                return File.Exists(node) ? node : null;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one integer sysfs attribute, returning <c>-1</c> when it is absent or malformed.
    /// <para>读取一个整数 sysfs 属性；缺失或格式非法时返回 <c>-1</c>。</para>
    /// </summary>
    /// <param name="directory">The directory holding the attribute.
    /// <para>持有该属性的目录。</para></param>
    /// <param name="attribute">The attribute file name. <para>属性文件名。</para></param>
    /// <returns>The value, or <c>-1</c>. <para>该值；或 <c>-1</c>。</para></returns>
    private static int ReadSysfsInt(string directory, string attribute)
    {
        try
        {
            var path = Path.Combine(directory, attribute);
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Walks the node's direct children and returns the first usable port name, or
    /// <see langword="null"/> when the node has no children or none of them qualifies.
    /// <para>遍历节点的直接子节点，返回第一个可用串口名；无子节点或都不可用时为
    /// <see langword="null"/>。</para>
    /// </summary>
    private static string? ReadChildSerialPortName(uint devInst)
    {
        if (!UsbPnpNative.TryGetChild(devInst, out var child))
        {
            return null;
        }

        do
        {
            if (TryReadSerialPortName(child) is { } portName)
            {
                return portName;
            }
        }
        while (UsbPnpNative.TryGetSibling(child, out child));

        return null;
    }

    /// <summary>
    /// Checks whether one device node is a Ports-class serial node and, if so, resolves its port
    /// name; otherwise returns <see langword="null"/>. A node carrying a WinUSB driver binding is
    /// refused before any port is opened. The port name prefers the authoritative
    /// <c>PortName</c> value under
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters</c> and falls back
    /// to parsing the <c>(COMx)</c> suffix of the friendly name when the value is absent.
    /// <para>判断单个设备节点是否为 Ports 类的串口节点，是则解析端口名；否则返回
    /// <see langword="null"/>。带 WinUSB 驱动绑定的节点在打开任何端口之前即被拒绝。端口名优先取注册表
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters</c> 下的
    /// <c>PortName</c>（权威来源），缺失时回退到从友好名称解析 <c>(COMx)</c> 的方式。</para>
    /// </summary>
    private static string? TryReadSerialPortName(uint devInst)
    {
        var instanceId = UsbPnpNative.GetDeviceId(devInst);
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        var properties = UsbPnpNative.ReadProperties(devInst, instanceId);
        if (IsWinUsbBoundNode(properties.DriverService, ReadRegistryDeviceInterfaceGuids(instanceId)))
        {
            UsbTrace.LogFormatted($"EDL serial fallback: device node '{instanceId}' is bound to WinUSB and exposes no serial port.");
            return null;
        }

        if (properties.ClassGuid is not { } classGuid || classGuid != PortsClassGuid)
        {
            return null;
        }

        var portName = ReadRegistryPortName(instanceId);
        if (portName is not null && !ComPortNamePattern().IsMatch(portName))
        {
            portName = null;
        }

        portName ??= ParsePortNameFromFriendlyName(properties.FriendlyName);
        return string.IsNullOrEmpty(portName) ? null : portName;
    }

    /// <summary>
    /// Reports whether a devnode is bound to WinUSB, either by its kernel service or by the
    /// generated INF's DeviceInterfaceGUIDs marker; such a node exposes no serial port.
    /// <para>判断设备节点是否已绑定 WinUSB（内核服务名或生成 INF 写入的 DeviceInterfaceGUIDs 标记）；
    /// 这类节点不暴露串口。</para>
    /// </summary>
    /// <param name="driverService">The devnode's kernel service name. <para>设备节点的内核服务名。</para></param>
    /// <param name="deviceInterfaceGuids">The node-level DeviceInterfaceGUIDs value.
    /// <para>节点级 DeviceInterfaceGUIDs 取值。</para></param>
    /// <returns><see langword="true"/> when a serial fallback must not be attempted.
    /// <para>不应尝试串口回退时为 <see langword="true"/>。</para></returns>
    internal static bool IsWinUsbBoundNode(string? driverService, object? deviceInterfaceGuids)
        => string.Equals(driverService, "WinUSB", StringComparison.OrdinalIgnoreCase) ||
            deviceInterfaceGuids is not null;

    private static object? ReadRegistryDeviceInterfaceGuids(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters");
            return key?.GetValue("DeviceInterfaceGUIDs");
        }
        catch (Exception ex)
        {
            UsbTrace.LogFormatted($"EDL serial fallback: DeviceInterfaceGUIDs read for '{instanceId}' failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? ReadRegistryPortName(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters");
            return key?.GetValue("PortName") as string;
        }
        catch (Exception ex)
        {
            UsbTrace.LogFormatted($"EDL serial fallback: PortName registry read for '{instanceId}' failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? ParsePortNameFromFriendlyName(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName))
        {
            return null;
        }

        var start = friendlyName.IndexOf("(COM", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = friendlyName.IndexOf(')', start + 4);
        if (end < 0)
        {
            return null;
        }

        var candidate = friendlyName[(start + 1)..end];
        return ComPortNamePattern().IsMatch(candidate) ? candidate : null;
    }
}