using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Abstractions.Devices;

/// <summary>
/// <para>设备命令执行结果。</para>
/// Result of a device command execution.
/// </summary>
public sealed class DeviceCommandResult
{
    /// <summary>
    /// <para>是否成功。</para>
    /// Whether successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// <para>标准输出文本。</para>
    /// Standard output text.
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>
    /// <para>标准错误文本。</para>
    /// Standard error text.
    /// </summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// <para>退出码（-1 表示未获取）。</para>
    /// Exit code (-1 means not captured).
    /// </summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>
    /// <para>执行耗时（毫秒）。</para>
    /// Execution duration in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// <para>创建失败结果。</para>
    /// Creates a failure result.
    /// </summary>
    public static DeviceCommandResult Failed(string error, int exitCode = -1)
        => new() { Success = false, Error = error, ExitCode = exitCode };

    /// <summary>
    /// <para>创建成功结果。</para>
    /// Creates a success result.
    /// </summary>
    public static DeviceCommandResult Ok(string output, int exitCode = 0, long durationMs = 0)
        => new() { Success = true, Output = output, ExitCode = exitCode, DurationMs = durationMs };
}

/// <summary>
/// <para>设备管理器接口，统一兼容 ADB、Fastboot、Fastbootd、Recovery、Qualcomm EDL、MTK COM、Rockchip BootROM 等通讯协议。</para>
/// Device manager interface, uniformly compatible with ADB, Fastboot, Fastbootd, Recovery, Qualcomm EDL, MTK COM, Rockchip BootROM protocols.
/// </summary>
public interface IDeviceManager
{
    /// <summary>
    /// <para>当前已识别的设备列表（只读快照）。</para>
    /// Current identified device list (read-only snapshot).
    /// </summary>
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>
    /// <para>设备列表变更事件（新增/移除/状态变化）。</para>
    /// Device list change event (added/removed/state change).
    /// </summary>
    event DeviceListChangedEventHandler? DevicesChanged;

    /// <summary>
    /// <para>枚举所有 USB/串口设备，识别已连接设备并填充能力描述。</para>
    /// Enumerates all USB/serial devices, identifies connected devices and populates capability descriptors.
    /// </summary>
    Task EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>使用指定协议连接设备。</para>
    /// Connects to a device using the specified protocol.
    /// </summary>
    Task<DeviceInfo?> ConnectAsync(string serial, DeviceProtocol protocol, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>断开指定设备。</para>
    /// Disconnects the specified device.
    /// </summary>
    Task DisconnectAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>执行通用设备命令。</para>
    /// Executes a generic device command.
    /// </summary>
    Task<DeviceCommandResult> ExecuteAsync(string serial, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>刷写分区。</para>
    /// Flashes a partition.
    /// </summary>
    Task<DeviceCommandResult> FlashPartitionAsync(string serial, string partition, string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>擦除分区。</para>
    /// Erases a partition.
    /// </summary>
    Task<DeviceCommandResult> ErasePartitionAsync(string serial, string partition, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>重启设备到指定模式。</para>
    /// Reboots the device to the specified mode.
    /// </summary>
    Task<DeviceCommandResult> RebootAsync(string serial, RebootMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>获取设备能力描述。</para>
    /// Gets the device capability descriptor.
    /// </summary>
    DeviceCapabilities? GetCapabilities(string serial);
}

/// <summary>
/// <para>重启目标模式。</para>
/// Reboot target mode.
/// </summary>
public enum RebootMode
{
    /// <summary><para>正常重启。</para>Normal reboot.</summary>
    Normal,

    /// <summary><para>重启到 bootloader (fastboot)。</para>Reboot to bootloader (fastboot).</summary>
    Bootloader,

    /// <summary><para>重启到 recovery。</para>Reboot to recovery.</summary>
    Recovery,

    /// <summary><para>重启到 fastbootd (userspace fastboot)。</para>Reboot to fastbootd (userspace fastboot).</summary>
    Fastbootd,

    /// <summary><para>重启到 EDL 模式（Qualcomm）。</para>Reboot to EDL mode (Qualcomm).</summary>
    Edl,

    /// <summary><para>重启到 sideload。</para>Reboot to sideload.</summary>
    Sideload
}

/// <summary>
/// <para>设备列表变更事件处理器。</para>
/// Device list change event handler.
/// </summary>
/// <param name="sender">
/// <para>事件源。</para>
/// Event source.
/// </param>
/// <param name="e">
/// <para>事件参数。</para>
/// Event args.
/// </param>
public delegate void DeviceListChangedEventHandler(object? sender, DeviceListChangedEventArgs e);

/// <summary>
/// <para>设备列表变更事件参数。</para>
/// Device list change event args.
/// </summary>
public sealed class DeviceListChangedEventArgs : System.EventArgs
{
    /// <summary>
    /// <para>变更类型。</para>
    /// Change kind.
    /// </summary>
    public DeviceChangeKind Kind { get; init; }

    /// <summary>
    /// <para>受影响的设备信息。</para>
    /// Affected device info.
    /// </summary>
    public DeviceInfo? Device { get; init; }
}

/// <summary>
/// <para>设备变更类型。</para>
/// Device change kind.
/// </summary>
public enum DeviceChangeKind
{
    /// <summary><para>新增设备。</para>Device added.</summary>
    Added,

    /// <summary><para>移除设备。</para>Device removed.</summary>
    Removed,

    /// <summary><para>设备状态变化。</para>Device state changed.</summary>
    StateChanged,

    /// <summary><para>设备能力描述刷新。</para>Device capabilities refreshed.</summary>
    CapabilitiesRefreshed
}
