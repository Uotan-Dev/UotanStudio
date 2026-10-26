using UotanStudio.Services.Terminal;
using UotanStudio.Services.Workspace;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Validates a debug bridge target and opens an interactive shell session for it.
/// <para>校验调试桥目标，并为该目标打开交互式 shell 会话。</para>
/// </summary>
internal static class DeviceShellService
{
    internal static bool IsAdbReadyState(string state) => state is "device" or "online";

    internal static void RequireServerTarget(DetectedDevice device, bool serverPresent, bool targetReady)
    {
        if (device.Protocol is not (DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc))
            throw new ArgumentException("An ADB or HDC target is required.", nameof(device));
        if (!serverPresent)
            throw new NotSupportedException("An interactive shell requires a running debug bridge server.");
        if (!targetReady)
            throw new IOException("The selected target is not ready on the debug bridge server.");
    }

    internal static void OpenInteractiveShell(DeviceMonitorService monitor, DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb)
        {
            if (!IsAdbReadyState(device.State) || !monitor.ProbeAdb(device))
                throw new IOException("The selected ADB target is not ready.");
        }
        else if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            if (DeviceMonitorService.UseHdcServerFor(device))
            {
                RequireServerTarget(device, serverPresent: true, targetReady: device.State == "device");
            }
        }
        else
        {
            RequireServerTarget(device, false, false);
        }
        WorkspacePanelService.Instance.OpenDeviceShell(device);
    }

    /// <summary>
    /// Creates the in-process shell connection for an already validated target, so the terminal
    /// consumes the host's own device session instead of a second process opening a competing one.
    /// <para>为已校验的目标创建进程内 shell 连接，使终端消费宿主自己的设备会话，而不是由第二个进程另开
    /// 一份竞争的传输。</para>
    /// </summary>
    /// <param name="monitor">The host monitor that owns the device session.
    /// <para>持有该设备会话的宿主监视器。</para></param>
    /// <param name="device">The validated target.<para>已校验的目标。</para></param>
    /// <returns>The connection to hand to the terminal view.<para>交给终端视图的连接。</para></returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.
    /// <para>参数为 <see langword="null"/>。</para></exception>
    internal static DeviceShellConnection CreateConnection(DeviceMonitorService monitor, DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceShellConnection((input, output, cancellationToken) =>
            monitor.RunInteractiveShellAsync(device, input, output, cancellationToken));
    }
}