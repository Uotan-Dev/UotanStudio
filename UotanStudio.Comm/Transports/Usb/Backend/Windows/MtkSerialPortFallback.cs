using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Resolves the serial port a MediaTek boot endpoint is bound to when the target exposes no openable USB
/// interface at all. The MediaTek preloader publishes the very same boot protocol over a VCOM/CDC-ACM pipe, so a
/// device the platform bound to its serial driver can still be driven, and refusing it would report a healthy
/// device as unopenable.
/// <para>当联发科启动端点完全没有可打开的 USB 接口时，解析它被绑定到的串口。联发科 preloader 在同一 VCOM/CDC-ACM
/// 管道上提供完全相同的启动协议，因此被平台绑定到串口驱动的设备仍可驱动；拒绝它会把一个健康的设备报成不可打开。</para>
/// </summary>
/// <remarks>
/// Every branch is fail-closed and resolves identity from the platform's own topology rather than from a port name
/// that shifts across re-enumerations: Windows matches the endpoint's exact PnP device node (itself or one of its
/// direct children) whose class is Ports; macOS matches the exact <c>IORegistryEntryID</c> carried by the endpoint
/// path and then walks that device's registry descendants for an <c>IOSerialBSDClient</c>. A node the platform bound
/// to WinUSB never qualifies on either platform, because such a binding publishes no port. Any failed step returns
/// <see langword="null"/> and the caller keeps its original exception semantics, so the real open failure still
/// reaches the user.
/// <para>每个分支都是 fail-closed，并且身份来自平台自身的拓扑，而不是会在重枚举后变化的端口名：Windows 匹配端点
/// 精确的 PnP 设备节点（其自身或某个直接子节点）且类为 Ports；macOS 匹配端点路径携带的精确
/// <c>IORegistryEntryID</c>，再遍历该设备的注册表后代寻找 <c>IOSerialBSDClient</c>。被平台绑定到 WinUSB 的节点
/// 在两个平台上都不合格，因为这种绑定不公布端口。任何一步失败都返回 <see langword="null"/>，由调用方维持原异常
/// 语义，从而把真实的打开失败如实呈现给用户。</para>
/// </remarks>
internal static partial class MtkSerialPortFallback
{
    private const int MaxInstanceIdChars = 200;

    /// <summary>Total open attempts for one resolved port, absorbing the exclusive-handle teardown of a lease that
    /// was just released. <para>单个已解析端口的打开尝试总数，用于吸收刚释放的租约仍在进行的独占句柄拆除。</para></summary>
    private const int SerialOpenAttempts = 4;

    /// <summary>Pause between two open attempts, long enough for the previous handle to be released.
    /// <para>两次打开尝试之间的暂停，足够前一个句柄被释放。</para></summary>
    private const int SerialOpenRetryDelayMs = 150;

    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    [GeneratedRegex("^COM\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ComPortNamePattern();

    /// <summary>
    /// Resolves the serial port name a MediaTek boot endpoint is bound to without opening it, so callers can key a
    /// parked connection by the exact port the endpoint owns. Every branch is fail-closed and resolves identity
    /// from the platform's own topology, matching the rules of <see cref="TryOpenConnected"/>.
    /// <para>解析联发科启动端点绑定的串口名但不打开它，使调用方可以用端点实际拥有的端口作为暂存连接的键。
    /// 每个分支都 fail-closed，并按 <see cref="TryOpenConnected"/> 的规则从平台自身拓扑解析身份。</para>
    /// </summary>
    /// <param name="devicePath">The exact device-interface path of the endpoint.
    /// <para>端点的精确设备接口路径。</para></param>
    /// <returns>The resolved port name, or <see langword="null"/> when no port is bound or the platform is
    /// unsupported. <para>解析出的端口名；未绑定串口或平台不支持时为 <see langword="null"/>。</para></returns>
    internal static string? TryResolvePortName(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? TryResolveWindowsPortName(devicePath)
            : OperatingSystem.IsOSPlatform("OSX")
                ? MacIoRegistryNative.TryFindSerialDeviceNode(devicePath)
                : null;
    }

    /// <summary>
    /// Resolves the serial port owned by the endpoint and opens it without flow control, which is what the
    /// MediaTek boot protocol expects over a VCOM pipe.
    /// <para>解析端点所属的串口并以无流控方式打开，这正是联发科启动协议在 VCOM 管道上期望的方式。</para>
    /// </summary>
    /// <param name="devicePath">The exact device-interface path of the endpoint.
    /// <para>端点的精确设备接口路径。</para></param>
    /// <param name="baudRate">The line speed to open the port with.<para>打开端口使用的线路速度。</para></param>
    /// <returns>A connected <see cref="SerialTransport"/> on the resolved port, or <see langword="null"/> when any
    /// resolution or open step fails.
    /// <para>解析出的端口上已连接的 <see cref="SerialTransport"/>；任一解析或打开步骤失败时为
    /// <see langword="null"/>。</para></returns>
    internal static SerialTransport? TryOpenConnected(string? devicePath, int baudRate = 115200)
    {
        var portName = TryResolvePortName(devicePath);
        if (portName is null)
        {
            return null;
        }

        // The port is acquired exclusively. A session that was just released can still hold the handle for a
        // moment while its teardown completes, so a single open attempt reports a healthy device as unopenable.
        // Each retry starts from a fresh transport because a failed SerialPort open leaves the instance unusable.
        // 该端口是独占获取的。刚释放的会话在拆除完成前仍可能持有句柄，使单次打开尝试把一个健康的设备报成不可打开。
        // 每次重试都从全新的传输开始，因为失败的 SerialPort 打开会让该实例无法复用。
        for (var attempt = 1; ; attempt++)
        {
            var transport = new SerialTransport(portName, baudRate, Parity.None, 8, StopBits.One, Handshake.None);
            try
            {
                transport.Connect();
                UsbTrace.LogFormatted(
                    $"MTK serial fallback: '{devicePath}' opened as '{portName}' at {baudRate} baud (attempt {attempt}).");
                return transport;
            }
            catch (Exception error)
            {
                transport.Dispose();
                if (attempt >= SerialOpenAttempts)
                {
                    var failure = error;
                    var attempts = attempt;
                    UsbTrace.LogFormatted(
                        $"MTK serial fallback: opening '{portName}' failed after {attempts} attempts: {failure}");
                    return null;
                }

                var retryError = error.GetType().Name;
                var retryAttempt = attempt;
                UsbTrace.LogFormatted(
                    $"MTK serial fallback: opening '{portName}' attempt {retryAttempt} failed ({retryError}); retrying.");
                Thread.Sleep(SerialOpenRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// Normalizes a Windows device-interface path into a PnP device instance id. Endpoint keys carry the
    /// device-interface path (for example <c>\\?\usb#vid_0e8d&amp;pid_2000#10000030#{a5dcbf10-…}</c>) while
    /// <see cref="UsbPnpNative.TryLocateDevNode"/> needs the instance id (for example
    /// <c>USB\VID_0E8D&amp;PID_2000\10000030</c>). The conversion is pure string work and never touches system state.
    /// <para>把 Windows 设备接口路径归一化为 PnP 设备实例标识。端点键携带的是设备接口路径（例如
    /// <c>\\?\usb#vid_0e8d&amp;pid_2000#10000030#{a5dcbf10-…}</c>），而
    /// <see cref="UsbPnpNative.TryLocateDevNode"/> 需要实例标识（例如
    /// <c>USB\VID_0E8D&amp;PID_2000\10000030</c>）。转换是纯字符串操作，不触及系统状态。</para>
    /// </summary>
    /// <param name="devicePath">The device-interface path from a USB endpoint key.
    /// <para>USB 端点键中的设备接口路径。</para></param>
    /// <returns>The PnP instance id, or <see langword="null"/> when the path is not a USB device-interface path of
    /// plausible length. <para>PnP 实例标识；路径不是长度合理的 USB 设备接口路径时为 <see langword="null"/>。</para></returns>
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
    /// Reports whether a devnode's current kernel binding is WinUSB, which leaves no serial port. The kernel service
    /// name is the authority: a WinUSB takeover installs WinUSB as the service, while every VCOM serial driver
    /// (<c>usbser</c>, <c>wdm_usb</c>) keeps the port. Registered interface GUIDs are deliberately not consulted,
    /// because a released takeover leaves its DeviceInterfaceGUIDs value behind.
    /// <para>判断设备节点当前的内核绑定是否为 WinUSB——是则没有串口。内核服务名是权威：WinUSB 接管会安装 WinUSB
    /// 为服务，而所有 VCOM 串口驱动（<c>usbser</c>、<c>wdm_usb</c>）都保留串口。注册的接口 GUID 一律不参与判定
    /// ——解除接管会残留 DeviceInterfaceGUIDs 值。</para>
    /// </summary>
    /// <param name="driverService">The devnode's kernel service name.<para>设备节点的内核服务名。</para></param>
    /// <returns><see langword="true"/> when a serial fallback must not be attempted.
    /// <para>不应尝试串口回退时为 <see langword="true"/>。</para></returns>
    internal static bool IsWinUsbBoundNode(string? driverService)
        => string.Equals(driverService, "WinUSB", StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? TryResolveWindowsPortName(string? devicePath)
    {
        var instanceId = TryDeriveInstanceId(devicePath);
        if (instanceId is null || !UsbPnpNative.TryLocateDevNode(instanceId, out var devInst))
        {
            UsbTrace.LogFormatted($"MTK serial fallback: device node '{devicePath}' cannot be located.");
            return null;
        }

        // The node itself may be the Ports device, or a USB-class function whose serial port lives on a child
        // node (a vendor VCOM driver that keeps the function under the USB class). Both shapes are valid MTK
        // preloader attachments, so the child walk runs whenever the node itself answers nothing.
        // 节点本身可能就是 Ports 设备，也可能是 USB 类功能而串口位于子节点（把功能挂在 USB 类下的厂商 VCOM 驱动）。
        // 两种形态都是合法的 MTK preloader 挂接，因此节点本身无答案时总是继续查找子节点。
        return TryReadSerialPortName(devInst) ?? ReadChildSerialPortName(devInst);
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadSerialPortName(uint devInst)
    {
        var instanceId = UsbPnpNative.GetDeviceId(devInst);
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        var properties = UsbPnpNative.ReadProperties(devInst, instanceId);
        if (IsWinUsbBoundNode(properties.DriverService))
        {
            UsbTrace.LogFormatted($"MTK serial fallback: device node '{instanceId}' is bound to WinUSB and exposes no serial port.");
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

    [SupportedOSPlatform("windows")]
    private static string? ReadChildSerialPortName(uint devInst)
    {
        if (!UsbPnpNative.TryGetChild(devInst, out var child))
        {
            return null;
        }

        while (true)
        {
            if (TryReadSerialPortName(child) is { } portName)
            {
                return portName;
            }

            if (!UsbPnpNative.TryGetSibling(child, out var sibling))
            {
                return null;
            }

            child = sibling;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryPortName(string instanceId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters");
            return key?.GetValue("PortName") as string;
        }
        catch (Exception error)
        {
            UsbTrace.LogFormatted(
                $"MTK serial fallback: PortName registry read for '{instanceId}' failed: {error.GetType().Name}");
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