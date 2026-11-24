using System.IO.Ports;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Comm.Transports.Usb.Backend.Unisoc;

/// <summary>
/// Opens the serial port a UNISOC download endpoint is bound to when the native USB open fails, mirroring the
/// MediaTek VCOM fallback: the exact endpoint PnP node (itself or a direct child) is resolved from the platform
/// topology, the port is opened with no flow control, and the open is retried a bounded number of times so a
/// handle that was just released does not make a healthy device look unopenable.
/// <para>当原生 USB 打开失败时，打开紫光展锐下载端点所绑定的串口，镜像联发科 VCOM 回退：从平台拓扑按端点的
/// 精确 PnP 节点（自身或直接子节点）解析端口，以无流控方式打开，并做有界重试，使刚释放的句柄不会让健康设备
/// 被误判为不可打开。</para>
/// </summary>
internal static class UnisocSerialPortFallback
{
    private const int FallbackBaudRate = 115200;
    private const int SerialOpenAttempts = 4;
    private const int SerialOpenRetryDelayMs = 150;

    /// <summary>Resolves the port name without opening it. <para>解析端口名但不打开。</para></summary>
    /// <param name="devicePath">The endpoint's device-interface path. <para>端点的设备接口路径。</para></param>
    /// <returns>The COM/TTY port name, or <see langword="null"/>. <para>COM/TTY 端口名，或 <see langword="null"/>。</para></returns>
    internal static string? TryResolvePortName(string? devicePath)
    {
        if (OperatingSystem.IsLinux())
        {
            // The EDL fallback already maps a usbfs path to its owning tty through sysfs; the resolution is
            // VID-agnostic and identical for a Unisoc download gadget.
            return null;
        }

        return MtkSerialPortFallback.TryResolvePortName(devicePath);
    }

    /// <summary>
    /// Reports whether the endpoint's exact PnP node (or a direct child) is already bound to a serial driver.
    /// Such a node publishes no WinUSB interface, so the native open would only fail and disturb the gadget.
    /// <para>报告端点的精确 PnP 节点（或其直接子节点）是否已绑定到串口驱动。此类节点不公布 WinUSB 接口，
    /// 原生打开只会失败并惊扰设备。</para>
    /// </summary>
    /// <param name="devicePath">The endpoint's device-interface path. <para>端点的设备接口路径。</para></param>
    /// <returns><see langword="true"/> when a serial binding is present. <para>存在串口绑定时为 <see langword="true"/>。</para></returns>
    internal static bool HasSerialBinding(string? devicePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var instanceId = MtkSerialPortFallback.TryDeriveInstanceId(devicePath);
        if (instanceId is null || !UsbPnpNative.TryLocateDevNode(instanceId, out var devInst))
        {
            return false;
        }

        return SerialNodeExists(devInst, depth: 0);
    }

    private const int MaxChildDepth = 2;

    private static bool SerialNodeExists(uint devInst, int depth)
    {
        var instanceId = UsbPnpNative.GetDeviceId(devInst);
        if (!string.IsNullOrEmpty(instanceId))
        {
            var properties = UsbPnpNative.ReadProperties(devInst, instanceId);
            if (properties.ClassGuid == SerialPortClassGuid &&
                !MtkSerialPortFallback.IsWinUsbBoundNode(properties.DriverService))
            {
                return true;
            }
        }

        if (depth >= MaxChildDepth || !UsbPnpNative.TryGetChild(devInst, out var child))
        {
            return false;
        }

        do
        {
            if (SerialNodeExists(child, depth + 1))
            {
                return true;
            }
        }
        while (UsbPnpNative.TryGetSibling(child, out child));

        return false;
    }

    private static readonly Guid SerialPortClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    /// <summary>Resolves and opens the endpoint's serial port with bounded retries and no flow control.
    /// <para>解析并以有界重试、无流控方式打开端点的串口。</para></summary>
    /// <param name="devicePath">The endpoint's device-interface path. <para>端点的设备接口路径。</para></param>
    /// <param name="baudRate">The line speed. <para>线路速度。</para></param>
    /// <returns>A connected transport, or <see langword="null"/> when resolution or every open attempt fails.
    /// <para>已连接的传输；解析失败或每次打开都失败时为 <see langword="null"/>。</para></returns>
    internal static SerialTransport? TryOpenConnected(string? devicePath, int baudRate = FallbackBaudRate)
    {
        if (OperatingSystem.IsLinux())
        {
            return EdlSerialPortFallback.TryOpenConnected(devicePath);
        }

        var portName = TryResolvePortName(devicePath);
        if (portName is null)
        {
            UsbTrace.LogFormatted($"UNISOC serial fallback: no Ports-class child with a port name under '{devicePath}'.");
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            var transport = new SerialTransport(portName, baudRate, Parity.None, 8, StopBits.One, Handshake.None);
            try
            {
                transport.Connect();
                // The official tool clears the channel before CHECK_BAUD (BootModeOpr.cpp: m_pChannel->Clear()).
                // A freshly opened VCOM can still hold bytes from an earlier probe or from the boot ROM's arrival
                // banner; leaving them buffered makes the stage parser read a stale ACK instead of the version
                // reply. Discard both buffers before any protocol byte is written.
                transport.Reset();
                UsbTrace.LogFormatted(
                    $"UNISOC serial fallback: '{devicePath}' opened as '{portName}' at {baudRate} baud (attempt {attempt}), buffers cleared.");
                return transport;
            }
            catch (Exception error)
            {
                transport.Dispose();
                if (attempt >= SerialOpenAttempts)
                {
                    UsbTrace.LogFormatted(
                        $"UNISOC serial fallback: opening '{portName}' failed after {attempt} attempts: {error}");
                    return null;
                }

                Thread.Sleep(SerialOpenRetryDelayMs);
            }
        }
    }
}