using System.Runtime.Versioning;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// A serial port owned by a USB boot endpoint, with the protocol the canonical classifier assigned from its
/// hardware id. <para>由 USB 启动端点占用的串口，附带通用分类器依据硬件 ID 判定的协议。</para>
/// </summary>
/// <param name="PortName">The COM/TTY name to open. <para>要打开的 COM/TTY 名称。</para></param>
/// <param name="VendorId">USB vendor id from the hardware id. <para>硬件 ID 中的 USB 厂商 ID。</para></param>
/// <param name="ProductId">USB product id from the hardware id. <para>硬件 ID 中的 USB 产品 ID。</para></param>
/// <param name="Protocol">Protocol token from <see cref="UsbProtocolClassifier"/>. <para>通用分类器给出的协议名。</para></param>
public sealed record FastBootPort(string PortName, ushort VendorId, ushort ProductId, string Protocol);

/// <summary>
/// Enumerates serial ports bound to USB boot endpoints that are already present when the application starts,
/// using the canonical <see cref="UsbProtocolClassifier"/> as the only vendor/protocol authority. Vendor VCOM
/// bindings hide these endpoints from the USB inventory, so the read-only PnP hardware ids are the only way to
/// recognise them without probing unrelated serial devices.
/// <para>枚举应用启动时已存在的、绑定到 USB 启动端点的串口，厂商与协议的唯一判定入口是通用的
/// <see cref="UsbProtocolClassifier"/>。厂商 VCOM 绑定使这些端点不出现在 USB 清单中，因此只读 PnP 硬件 ID
/// 是识别它们而不探测无关串口的唯一方式。</para>
/// </summary>
public static class FastBootPortEnumerator
{
    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    /// <summary>
    /// Lists every present serial port whose hardware id names a protocol known to the canonical classifier.
    /// Non-Windows platforms and any PnP read failure degrade to an empty list; nothing here opens or changes a device.
    /// <para>列出硬件 ID 能被通用分类器识别为已知协议的所有在位串口。非 Windows 平台或 PnP 读取失败时降级为
    /// 空列表；本方法从不打开或修改设备。</para>
    /// </summary>
    /// <returns>The boot ports in enumeration order. <para>启动端口列表，按枚举顺序。</para></returns>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<FastBootPort> Enumerate()
    {
        // Non-Windows hosts have no PnP tree here, but their USB backend already reports VID/PID on every
        // enumerated device. Derive the same boot-port set from that inventory: the canonical classifier stays
        // the only protocol authority, and the platform's own serial-node resolver names the port when the boot
        // gadget is bound to a serial driver. A device with no serial node still appears with an empty port name so
        // the caller can open it by its exact device path over the native backend.
        // 非 Windows 主机没有这里的 PnP 树，但其 USB 后端在枚举设备上已给出 VID/PID。由该清单派生同样的启动端口集合：
        // 通用分类器仍是协议唯一判定者；启动设备绑定串口驱动时由平台自身的串口节点解析器给出端口名。
        // 没有串口节点的设备仍以空端口名出现，使调用方可按其精确设备路径经原生后端打开。
        if (!OperatingSystem.IsWindows())
        {
            return EnumerateFromUsbInventory();
        }

        var result = new List<FastBootPort>();
        try
        {
            foreach (var instanceId in UsbPnpNative.ReadPresentDeviceIds())
            {
                if (!UsbPnpNative.TryLocateDevNode(instanceId, out var devInst))
                {
                    continue;
                }

                var properties = UsbPnpNative.ReadProperties(devInst, instanceId);
                if (properties.ClassGuid is not { } classGuid || classGuid != PortsClassGuid)
                {
                    continue;
                }

                var portName = ReadPortName(properties, instanceId);
                if (portName is null)
                {
                    continue;
                }

                foreach (var hardwareId in properties.HardwareIds)
                {
                    if (!TryParseVidPid(hardwareId, out var vendorId, out var productId))
                    {
                        continue;
                    }

                    // The classifier is the single source of truth. An unknown pair stays unknown, so a random
                    // COM port is never mislabelled as a boot device.
                    if (UsbProtocolClassifier.Classify(vendorId, productId, null, null, null) is { } protocol)
                    {
                        result.Add(new FastBootPort(portName, vendorId, productId, protocol));
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            return [];
        }

        return result;
    }

    private static IReadOnlyList<FastBootPort> EnumerateFromUsbInventory()
    {
        var result = new List<FastBootPort>();
        try
        {
            var inventory = EnumerateUsb();
            foreach (var device in inventory)
            {
                if (UsbProtocolClassifier.Classify(device) is not { } token)
                {
                    continue;
                }

                var portName = ResolveSerialNode(device);
                result.Add(new FastBootPort(portName ?? device.DevicePath, device.VendorId, device.ProductId, token));
            }
        }
        catch (Exception)
        {
            return [];
        }

        return result;
    }

    private static IReadOnlyList<UsbDeviceInfo> EnumerateUsb()
    {
        try
        {
            return CommKit.EnumerateDevices();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? ResolveSerialNode(UsbDeviceInfo device)
    {
        if (string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return null;
        }

        if (OperatingSystem.IsOSPlatform("OSX"))
        {
            return MacIoRegistryNative.TryFindSerialDeviceNode(device.DevicePath);
        }

        // Linux: the kernel registers a tty under the USB device directory when a usb-serial driver claimed it.
        // Reading only the sysfs topology keeps this fallback passive; opening is left to the arrival work.
        if (OperatingSystem.IsLinux())
        {
            return FindLinuxTtyNode(device.DevicePath);
        }

        return null;
    }

    private static string? FindLinuxTtyNode(string devicePath)
    {
        // The EDL fallback already owns the authoritative sysfs tty mapping. Reuse it by path shape: a usbfs
        // path identifies one bus/device pair, whose tty node is owned by that same USB device.
        if (!devicePath.StartsWith("/dev/bus/usb/", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = devicePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !int.TryParse(segments[^2], out var bus) ||
            !int.TryParse(segments[^1], out var deviceNumber))
        {
            return null;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories("/sys/bus/usb/devices"))
            {
                if (ReadSysfsInt(candidate, "busnum") != bus ||
                    ReadSysfsInt(candidate, "devnum") != deviceNumber)
                {
                    continue;
                }

                foreach (var tty in Directory.EnumerateDirectories("/sys/class/tty"))
                {
                    var link = Path.Combine(tty, "device");
                    if (!Directory.Exists(link))
                    {
                        continue;
                    }

                    var resolved = Path.GetFullPath(link);
                    var root = Path.GetFullPath(candidate);
                    if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var node = Path.Combine("/dev", Path.GetFileName(tty));
                    if (File.Exists(node))
                    {
                        return node;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static int? ReadSysfsInt(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : null;
    }

    /// <summary>
    /// Reports the protocol the canonical classifier assigns to one serial port, or <see langword="null"/> when the
    /// port is not bound to a known boot endpoint. Fast watchers call this before submitting a new port so a
    /// vendor-specific download gadget keeps its own protocol instead of being claimed by the first watcher.
    /// <para>返回通用分类器为某个串口判定的协议；端口未绑定到已知启动端点时为 <see langword="null"/>。
    /// 快速监视器在提交新端口前调用它，使厂商下载设备保留自己的协议，而不会被第一个监视器抢占。</para>
    /// </summary>
    /// <param name="portName">COM port name. <para>COM 口名称。</para></param>
    /// <returns>The classified protocol token, or <see langword="null"/>. <para>判定出的协议名，或 <see langword="null"/>。</para></returns>
    [SupportedOSPlatform("windows")]
    public static string? ClassifyPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        foreach (var port in Enumerate())
        {
            if (string.Equals(port.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return port.Protocol;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadPortName(UsbDevNodeRawRecord properties, string instanceId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters");
            if (key?.GetValue("PortName") is string registered && !string.IsNullOrWhiteSpace(registered))
            {
                return registered;
            }
        }
        catch (Exception)
        {
        }

        if (string.IsNullOrEmpty(properties.FriendlyName))
        {
            return null;
        }

        var start = properties.FriendlyName.IndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = properties.FriendlyName.IndexOf(')', start + 4);
        return end > start ? properties.FriendlyName[(start + 1)..end] : null;
    }

    /// <summary>Parses a USB vendor/product pair from a PnP hardware id without naming any vendor.</summary>
    internal static bool TryParseVidPid(string? hardwareId, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return false;
        }

        var vid = hardwareId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pid = hardwareId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vid < 0 || pid < 0 || vid + 8 > hardwareId.Length || pid + 8 > hardwareId.Length ||
            !ushort.TryParse(hardwareId.AsSpan(vid + 4, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out vendorId) ||
            !ushort.TryParse(hardwareId.AsSpan(pid + 4, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out productId))
        {
            vendorId = 0;
            productId = 0;
            return false;
        }

        return true;
    }
}