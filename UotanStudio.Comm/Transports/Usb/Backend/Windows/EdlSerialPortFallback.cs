using System.IO.Ports;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// <para>当 EDL 端点的 WinUSB 直开失败、且目标被 QDLoader 类串口驱动绑定为 COM 口时，从端点的
/// 精确设备路径解析所属串口并打开。解析始终 fail-closed：只有与端点设备节点精确关联（节点自身或
/// 其直接子节点）、类 GUID 为 Ports（<c>{4d36e978-e325-11ce-bfc1-08002be10318}</c>）且能解析出
/// <c>COMn</c> 端口名的节点才会被采纳；驱动服务名不作限定——真机上的 QDLoader 既可能落到
/// Microsoft <c>usbser</c>，也可能落到高通自己的 <c>qcwdfserial</c> 等串口服务，而身份保证来自
/// 父节点精确匹配而非驱动名。任何一步失败都返回 <see langword="null"/>，由调用方维持原异常语义。
/// 每次调用都按当前 PnP 状态重新解析，不缓存端口名（QDLoader 重枚举后端口号可能变化）。</para>
/// When the WinUSB direct open of an EDL endpoint fails because the target is bound by a
/// QDLoader-style serial driver as a COM port, this resolves the owning serial port from the
/// endpoint's exact device path and opens it. Resolution is fail-closed: only a node exactly
/// associated with the endpoint device node (the node itself or one of its direct children) whose
/// class GUID is Ports (<c>{4d36e978-e325-11ce-bfc1-08002be10318}</c>) and whose <c>COMn</c> port
/// name can be resolved is accepted; the driver service name is deliberately not restricted — on
/// real machines the QDLoader binding may land on Microsoft's <c>usbser</c> or on Qualcomm's own
/// <c>qcwdfserial</c> serial service, and identity is guaranteed by the exact parent match rather
/// than by a driver name. Any failed step returns <see langword="null"/> and the caller keeps the
/// original exception semantics. Every call re-resolves the current PnP state and never caches the
/// port name, since the QDLoader port number may change across re-enumerations.
/// </summary>
internal static partial class EdlSerialPortFallback
{
    private const int FallbackBaudRate = 115200;

    private const int MaxInstanceIdChars = 200;

    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    [GeneratedRegex("^COM\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ComPortNamePattern();

    /// <summary>
    /// <para>把 Windows 设备接口路径归一化为 PnP 设备实例标识。端点键携带的是设备接口路径
    /// （如 <c>\\?\usb#vid_05c6&amp;pid_9008#20000030#{a5dcbf10-…}</c>），而
    /// <see cref="UsbPnpNative.TryLocateDevNode"/> 需要实例标识
    /// （如 <c>USB\VID_05C6&amp;PID_9008\20000030</c>）。转换是纯字符串操作，不触及系统状态。</para>
    /// Normalizes a Windows device-interface path into a PnP device instance id. Endpoint keys
    /// carry the device-interface path (e.g. <c>\\?\usb#vid_05c6&amp;pid_9008#20000030#{a5dcbf10-…}</c>)
    /// while <see cref="UsbPnpNative.TryLocateDevNode"/> needs the instance id
    /// (e.g. <c>USB\VID_05C6&amp;PID_9008\20000030</c>). The conversion is pure string work and
    /// never touches system state.
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
    /// <para>解析端点设备节点所属的 QDLoader 串口，打开并返回已连接的传输。候选节点按
    /// 「节点自身优先、直接子节点其次」遍历：单接口 QDLoader 设备（真机证据形态）的 Ports
    /// 节点就是端点设备节点本身；复合设备形态下串口驱动绑定在 <c>&amp;MI_00</c> 子节点上，
    /// 子节点经 <see cref="UsbPnpNative.TryGetChild"/>/<see cref="UsbPnpNative.TryGetSibling"/>
    /// 取得，天然只覆盖端点节点的直接后代。打开参数为 115200 / 8N1 / 无流控，与宿主现有
    /// EDL 串口路径一致。</para>
    /// Resolves the QDLoader serial port owned by the endpoint device node and returns a connected
    /// transport. Candidates are walked "node itself first, then direct children": for a
    /// single-interface QDLoader device (the shape observed on real hardware) the Ports node is the
    /// endpoint device node itself, while for composite shapes the serial driver binds to the
    /// <c>&amp;MI_00</c> child obtained via <see cref="UsbPnpNative.TryGetChild"/>/<see cref="UsbPnpNative.TryGetSibling"/>,
    /// which by construction only covers direct descendants of the endpoint node. The port is
    /// opened at 115200 / 8N1 / no handshake, matching the host's existing EDL serial path.
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
    /// <para>遍历节点的直接子节点，返回第一个可用串口名；无子节点或都不可用时为
    /// <see langword="null"/>。</para>
    /// Walks the node's direct children and returns the first usable port name, or
    /// <see langword="null"/> when the node has no children or none of them qualifies.
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
    /// <para>判断单个设备节点是否为 Ports 类的串口节点，是则解析端口名；否则返回
    /// <see langword="null"/>。端口名优先取注册表
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters</c> 下的
    /// <c>PortName</c>（权威来源），缺失时回退到从友好名称解析 <c>(COMx)</c> 的方式。</para>
    /// Checks whether one device node is a Ports-class serial node and, if so, resolves its port
    /// name; otherwise returns <see langword="null"/>. The port name prefers the authoritative
    /// <c>PortName</c> value under
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters</c> and falls back
    /// to parsing the <c>(COMx)</c> suffix of the friendly name when the value is absent.
    /// </summary>
    private static string? TryReadSerialPortName(uint devInst)
    {
        var instanceId = UsbPnpNative.GetDeviceId(devInst);
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        var properties = UsbPnpNative.ReadProperties(devInst, instanceId);
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