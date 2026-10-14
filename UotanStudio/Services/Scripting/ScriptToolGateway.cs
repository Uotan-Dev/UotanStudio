using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mirroring;

namespace UotanStudio.Services.Scripting;

/// <summary>
/// Device and flash operations dispatched by the script tool surface.
/// <para>脚本工具面所分发的设备与刷写操作。</para>
/// </summary>
public interface IScriptToolGateway
{
    /// <summary>
    /// Returns the current device inventory.
    /// <para>返回当前的设备清单。</para>
    /// </summary>
    /// <returns>
    /// The devices visible right now.
    /// <para>此刻可见的设备。</para>
    /// </returns>
    IReadOnlyList<DetectedDevice> Scan();

    /// <summary>
    /// Reboots one device into the given mode.
    /// <para>将一台设备重启进入指定模式。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to reboot.
    /// <para>要重启的目标设备。</para>
    /// </param>
    /// <param name="mode">
    /// Requested reboot mode.
    /// <para>请求的重启模式。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// The mode the device was told to enter.
    /// <para>设备被要求进入的模式。</para>
    /// </returns>
    Task<string> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken);

    /// <summary>
    /// Flashes one image to a partition.
    /// <para>把一个镜像刷写到某个分区。</para>
    /// </summary>
    /// <param name="device">
    /// Target device receiving the image.
    /// <para>接收该镜像的目标设备。</para>
    /// </param>
    /// <param name="partition">
    /// Destination partition name.
    /// <para>目标分区名。</para>
    /// </param>
    /// <param name="imagePath">
    /// Local path of the image to write.
    /// <para>待写入镜像的本地路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending transfer.
    /// <para>取消待执行传输的令牌。</para>
    /// </param>
    /// <returns>
    /// The outcome of the flash operation.
    /// <para>刷写操作的结果。</para>
    /// </returns>
    Task<FlashResult> FlashImageAsync(DetectedDevice device, string partition, string imagePath, CancellationToken cancellationToken);

    /// <summary>
    /// Erases one partition.
    /// <para>擦除一个分区。</para>
    /// </summary>
    /// <param name="device">
    /// Target device whose partition is erased.
    /// <para>所要擦除分区的目标设备。</para>
    /// </param>
    /// <param name="partition">
    /// Partition to erase.
    /// <para>要擦除的分区。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// The outcome of the erase operation.
    /// <para>擦除操作的结果。</para>
    /// </returns>
    Task<FlashResult> ErasePartitionAsync(DetectedDevice device, string partition, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one property or fastboot variable.
    /// <para>读取一个属性或 fastboot 变量。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to query.
    /// <para>要查询的目标设备。</para>
    /// </param>
    /// <param name="propertyName">
    /// Name of the property or variable.
    /// <para>属性或变量的名称。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending read.
    /// <para>取消待执行读取的令牌。</para>
    /// </param>
    /// <returns>
    /// The result carrying the read value.
    /// <para>携带所读值的结果。</para>
    /// </returns>
    Task<FlashResult> GetDevicePropertyAsync(DetectedDevice device, string propertyName, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the Fastboot partition table.
    /// <para>读取 Fastboot 分区表。</para>
    /// </summary>
    /// <param name="device">
    /// Target device in Fastboot mode.
    /// <para>处于 Fastboot 模式的目标设备。</para>
    /// </param>
    /// <param name="showAll">
    /// Whether to include every reported entry.
    /// <para>是否包含全部上报条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending read.
    /// <para>取消待执行读取的令牌。</para>
    /// </param>
    /// <returns>
    /// Partition names paired with their sizes in bytes.
    /// <para>分区名及其字节大小的配对列表。</para>
    /// </returns>
    Task<IReadOnlyList<(string Name, long Size)>> ReadPartitionTableAsync(DetectedDevice device, bool showAll, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the current A/B slot.
    /// <para>读取当前 A/B 槽位。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to query.
    /// <para>要查询的目标设备。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending read.
    /// <para>取消待执行读取的令牌。</para>
    /// </param>
    /// <returns>
    /// Identifier of the active slot.
    /// <para>活动槽位的标识。</para>
    /// </returns>
    Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// Switches to the other A/B slot.
    /// <para>切换到另一个 A/B 槽位。</para>
    /// </summary>
    /// <param name="device">
    /// Target device whose slot is switched.
    /// <para>要切换槽位的目标设备。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// Identifier of the slot now marked active.
    /// <para>现在被标记为活动的槽位标识。</para>
    /// </returns>
    Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a recovery wipe in the given mode.
    /// <para>以指定模式执行恢复模式清除。</para>
    /// </summary>
    /// <param name="device">
    /// Target device booted into recovery.
    /// <para>已启动进入恢复模式的目标设备。</para>
    /// </param>
    /// <param name="mode">
    /// Wipe mode requested by the script.
    /// <para>脚本请求的清除模式。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// Output reported by the recovery wipe.
    /// <para>恢复模式清除上报的输出。</para>
    /// </returns>
    Task<string> RecoveryCleanAsync(DetectedDevice device, string mode, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one shell command on an ADB target.
    /// <para>在 ADB 目标上执行一条 shell 命令。</para>
    /// </summary>
    /// <param name="device">
    /// Target device reachable over ADB.
    /// <para>可通过 ADB 访问的目标设备。</para>
    /// </param>
    /// <param name="command">
    /// Shell command text to execute.
    /// <para>要执行的 shell 命令文本。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending command.
    /// <para>取消待执行命令的令牌。</para>
    /// </param>
    /// <returns>
    /// Output produced by the command.
    /// <para>命令产生的输出。</para>
    /// </returns>
    Task<string> ShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken);

    /// <summary>
    /// Starts one mirror session for a device.
    /// <para>为某台设备启动一个投屏会话。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to mirror.
    /// <para>要投屏的目标设备。</para>
    /// </param>
    /// <returns>
    /// The active managed mirror session.
    /// <para>已激活的托管投屏会话。</para>
    /// </returns>
    ManagedMirrorSession StartMirror(DetectedDevice device);
}

/// <summary>
/// Default gateway backed by the device monitor, flash service and mirror host.
/// <para>由设备监视、刷写服务与投屏承载构成的默认网关。</para>
/// </summary>
public sealed class DeviceScriptToolGateway : IScriptToolGateway
{
    /// <summary>
    /// Shared default gateway instance.
    /// <para>共享的默认网关实例。</para>
    /// </summary>
    /// <value>
    /// The process-wide default gateway.
    /// <para>进程范围的默认网关。</para>
    /// </value>
    public static DeviceScriptToolGateway Default { get; } = new();

    private DeviceScriptToolGateway() { }

    /// <summary>
    /// Returns the current device inventory.
    /// <para>返回当前的设备清单。</para>
    /// </summary>
    /// <returns>
    /// The devices visible right now.
    /// <para>此刻可见的设备。</para>
    /// </returns>
    public IReadOnlyList<DetectedDevice> Scan() => App.DeviceMonitor.Scan();

    /// <summary>
    /// Reboots one device into the given mode.
    /// <para>将一台设备重启进入指定模式。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to reboot.
    /// <para>要重启的目标设备。</para>
    /// </param>
    /// <param name="mode">
    /// Requested reboot mode.
    /// <para>请求的重启模式。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// The mode the device was told to enter.
    /// <para>设备被要求进入的模式。</para>
    /// </returns>
    public Task<string> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken)
        => RebootCoreAsync(device, mode, cancellationToken);

    /// <summary>
    /// Flashes one image to a partition.
    /// <para>把一个镜像刷写到某个分区。</para>
    /// </summary>
    /// <param name="device">
    /// Target device receiving the image.
    /// <para>接收该镜像的目标设备。</para>
    /// </param>
    /// <param name="partition">
    /// Destination partition name.
    /// <para>目标分区名。</para>
    /// </param>
    /// <param name="imagePath">
    /// Local path of the image to write.
    /// <para>待写入镜像的本地路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending transfer.
    /// <para>取消待执行传输的令牌。</para>
    /// </param>
    /// <returns>
    /// The outcome of the flash operation.
    /// <para>刷写操作的结果。</para>
    /// </returns>
    public Task<FlashResult> FlashImageAsync(DetectedDevice device, string partition, string imagePath,
        CancellationToken cancellationToken)
        => FlashService.Default.FlashImageAsync(new ViewModels.Devices.DeviceInfo(device), partition, imagePath,
            null, cancellationToken);

    /// <summary>
    /// Erases one partition.
    /// <para>擦除一个分区。</para>
    /// </summary>
    /// <param name="device">
    /// Target device whose partition is erased.
    /// <para>所要擦除分区的目标设备。</para>
    /// </param>
    /// <param name="partition">
    /// Partition to erase.
    /// <para>要擦除的分区。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// The outcome of the erase operation.
    /// <para>擦除操作的结果。</para>
    /// </returns>
    public Task<FlashResult> ErasePartitionAsync(DetectedDevice device, string partition,
        CancellationToken cancellationToken)
        => FlashService.Default.ErasePartitionAsync(new ViewModels.Devices.DeviceInfo(device), partition,
            null, cancellationToken);

    /// <summary>
    /// Reads one property or fastboot variable.
    /// <para>读取一个属性或 fastboot 变量。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to query.
    /// <para>要查询的目标设备。</para>
    /// </param>
    /// <param name="propertyName">
    /// Name of the property or variable.
    /// <para>属性或变量的名称。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending read.
    /// <para>取消待执行读取的令牌。</para>
    /// </param>
    /// <returns>
    /// The result carrying the read value.
    /// <para>携带所读值的结果。</para>
    /// </returns>
    public Task<FlashResult> GetDevicePropertyAsync(DetectedDevice device, string propertyName,
        CancellationToken cancellationToken)
        => FlashService.Default.GetDevicePropertyAsync(new ViewModels.Devices.DeviceInfo(device), propertyName,
            cancellationToken);

    /// <summary>
    /// Reads the Fastboot partition table.
    /// <para>读取 Fastboot 分区表。</para>
    /// </summary>
    /// <param name="device">
    /// Target device in Fastboot mode.
    /// <para>处于 Fastboot 模式的目标设备。</para>
    /// </param>
    /// <param name="showAll">
    /// Whether to include every reported entry.
    /// <para>是否包含全部上报条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending read.
    /// <para>取消待执行读取的令牌。</para>
    /// </param>
    /// <returns>
    /// Partition names paired with their sizes in bytes.
    /// <para>分区名及其字节大小的配对列表。</para>
    /// </returns>
    public Task<IReadOnlyList<(string Name, long Size)>> ReadPartitionTableAsync(DetectedDevice device, bool showAll,
        CancellationToken cancellationToken)
        => FastbootPartitionReader.ReadAsync(device, showAll, cancellationToken);

    /// <summary>
    /// Reads the current A/B slot.
    /// <para>读取当前 A/B 槽位。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to query.
    /// <para>要查询的目标设备。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending read.
    /// <para>取消待执行读取的令牌。</para>
    /// </param>
    /// <returns>
    /// Identifier of the active slot.
    /// <para>活动槽位的标识。</para>
    /// </returns>
    public Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
        => FastbootSlotService.GetCurrentSlotAsync(device, cancellationToken);

    /// <summary>
    /// Switches to the other A/B slot.
    /// <para>切换到另一个 A/B 槽位。</para>
    /// </summary>
    /// <param name="device">
    /// Target device whose slot is switched.
    /// <para>要切换槽位的目标设备。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// Identifier of the slot now marked active.
    /// <para>现在被标记为活动的槽位标识。</para>
    /// </returns>
    public Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
        => FastbootSlotService.SetOtherAsync(device, cancellationToken);

    /// <summary>
    /// Runs a recovery wipe in the given mode.
    /// <para>以指定模式执行恢复模式清除。</para>
    /// </summary>
    /// <param name="device">
    /// Target device booted into recovery.
    /// <para>已启动进入恢复模式的目标设备。</para>
    /// </param>
    /// <param name="mode">
    /// Wipe mode requested by the script.
    /// <para>脚本请求的清除模式。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending operation.
    /// <para>取消待执行操作的令牌。</para>
    /// </param>
    /// <returns>
    /// Output reported by the recovery wipe.
    /// <para>恢复模式清除上报的输出。</para>
    /// </returns>
    public Task<string> RecoveryCleanAsync(DetectedDevice device, string mode, CancellationToken cancellationToken)
        => RecoveryCleanService.CleanAsync(device, mode, cancellationToken);

    /// <summary>
    /// Runs one shell command on an ADB target.
    /// <para>在 ADB 目标上执行一条 shell 命令。</para>
    /// </summary>
    /// <param name="device">
    /// Target device reachable over ADB.
    /// <para>可通过 ADB 访问的目标设备。</para>
    /// </param>
    /// <param name="command">
    /// Shell command text to execute.
    /// <para>要执行的 shell 命令文本。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending command.
    /// <para>取消待执行命令的令牌。</para>
    /// </param>
    /// <returns>
    /// Output produced by the command.
    /// <para>命令产生的输出。</para>
    /// </returns>
    public Task<string> ShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken)
        => DeviceShellCommandService.ExecuteAsync(device, command, cancellationToken);

    /// <summary>
    /// Starts one mirror session for a device.
    /// <para>为某台设备启动一个投屏会话。</para>
    /// </summary>
    /// <param name="device">
    /// Target device to mirror.
    /// <para>要投屏的目标设备。</para>
    /// </param>
    /// <returns>
    /// The connected managed mirror session.
    /// <para>已连接的托管投屏会话。</para>
    /// </returns>
    public ManagedMirrorSession StartMirror(DetectedDevice device)
    {
        var artifact = Scrcpy.ScrcpyServerService.Shared.Resolve();
        var options = new ScrcpyServerOptions { ServerVersion = artifact.Version };
        var session = App.DeviceMonitor.StartMirror(device, artifact.Path, options);
        try
        {
            session.Connect();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static async Task<string> RebootCoreAsync(DetectedDevice device, string mode,
        CancellationToken cancellationToken)
    {
        await App.DeviceMonitor.RebootAsync(device, mode, cancellationToken).ConfigureAwait(false);
        return mode;
    }
}