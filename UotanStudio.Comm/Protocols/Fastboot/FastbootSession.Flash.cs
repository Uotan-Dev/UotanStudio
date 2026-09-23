using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Utils;
using UotanStudio.Comm.Transports.Abstractions;


namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>
    /// Updates the super partition metadata: downloads the metadata image and sends
    /// update-super:&lt;partition&gt;[:wipe].
    /// <para>更新 super 分区元数据：下载元数据镜像并发送 update-super:&lt;分区&gt;[:wipe]。</para>
    /// </summary>
    public FastbootResponse UpdateSuper(string partition, Stream metadataImage, bool wipe = false, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(partition))
        {
            throw new ArgumentException("Partition name must not be empty.", nameof(partition));
        }

        DownloadData(metadataImage, metadataImage.Length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(wipe
            ? FastbootConstants.CommandUpdateSuper + partition + ":wipe"
            : FastbootConstants.CommandUpdateSuper + partition);
    }

    /// <summary>
    /// Updates the super partition metadata from a metadata image file.
    /// <para>从元数据镜像文件更新 super 分区元数据。</para>
    /// </summary>
    public FastbootResponse UpdateSuper(string partition, string metadataImagePath, bool wipe = false, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(metadataImagePath);
        return UpdateSuper(partition, fs, wipe, progress, cancellationToken);
    }

    /// <summary>
    /// Creates a logical partition with the specified size. Requires userspace fastboot mode.
    /// <para>创建指定大小的逻辑分区。需要用户空间 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse CreateLogicalPartition(string partition, long size)
        => ExecuteCommand($"{FastbootConstants.CommandCreateLogicalPartition}{partition}:{size}");

    /// <summary>
    /// Deletes a logical partition. Requires userspace fastboot mode.
    /// <para>删除逻辑分区。需要用户空间 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse DeleteLogicalPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandDeleteLogicalPartition + partition);

    /// <summary>
    /// Resizes a logical partition to the specified size. Requires userspace fastboot mode.
    /// <para>将逻辑分区调整为指定大小。需要用户空间 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse ResizeLogicalPartition(string partition, long size)
        => ExecuteCommand($"{FastbootConstants.CommandResizeLogicalPartition}{partition}:{size}");

    /// <summary>
    /// Wipes the super partition via the device-side wipe-super command.
    /// <para>通过设备侧 wipe-super 命令清除 super 分区。</para>
    /// </summary>
    public FastbootResponse WipeSuper(string partition)
        => ExecuteCommand(FastbootConstants.CommandWipeSuper + partition);

    /// <summary>
    /// Sends a flashing sub-command to the device (e.g. unlock, lock, get_unlock_ability).
    /// <para>向设备发送 flashing 子命令（如 unlock、lock、get_unlock_ability）。</para>
    /// </summary>
    public FastbootResponse FlashingCommand(string subCommand)
        => ExecuteCommand(FastbootConstants.CommandFlashing + subCommand);

    /// <summary>
    /// Gets the flashing unlock ability value. Returns 0 if unavailable or on failure.
    /// <para>获取 flashing 解锁能力值。不可用或失败时返回 0。</para>
    /// </summary>
    public int FlashingGetUnlockAbility()
    {
        var response = FlashingCommand("get_unlock_ability");
        return response.State == FastbootResponseState.Success &&
               int.TryParse(response.Info.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ability)
            ? ability
            : 0;
    }

    /// <summary>
    /// Returns whether the bootloader allows unlocking (get_unlock_ability == 1).
    /// <para>返回 bootloader 是否允许解锁（get_unlock_ability == 1）。</para>
    /// </summary>
    public bool IsUnlockAbilityEnabled() => FlashingGetUnlockAbility() == 1;

    /// <summary>
    /// Unlocks the device via flashing command.
    /// <para>通过 flashing 命令解锁设备。</para>
    /// </summary>
    public FastbootResponse FlashingUnlock() => FlashingCommand("unlock");

    /// <summary>
    /// Locks the device via flashing command.
    /// <para>通过 flashing 命令锁定设备。</para>
    /// </summary>
    public FastbootResponse FlashingLock() => FlashingCommand("lock");

    /// <summary>
    /// Unlocks critical partitions via flashing command.
    /// <para>通过 flashing 命令解锁关键分区。</para>
    /// </summary>
    public FastbootResponse FlashingUnlockCritical() => FlashingCommand("unlock_critical");

    /// <summary>
    /// Locks critical partitions via flashing command.
    /// <para>通过 flashing 命令锁定关键分区。</para>
    /// </summary>
    public FastbootResponse FlashingLockCritical() => FlashingCommand("lock_critical");

    /// <summary>
    /// Sends a GSI (Generic System Image) sub-command to the device.
    /// <para>向设备发送 GSI（通用系统镜像）子命令。</para>
    /// </summary>
    public FastbootResponse GsiCommand(string subCommand)
        => ExecuteCommand(FastbootConstants.CommandGsi + subCommand);

    /// <summary>
    /// Enables the GSI on the device.
    /// <para>启用设备上的 GSI。</para>
    /// </summary>
    public FastbootResponse GsiEnable() => GsiCommand("enable");

    /// <summary>
    /// Disables the GSI on the device.
    /// <para>禁用设备上的 GSI。</para>
    /// </summary>
    public FastbootResponse GsiDisable() => GsiCommand("disable");

    /// <summary>
    /// Queries the GSI installation status on the device.
    /// <para>查询设备上的 GSI 安装状态。</para>
    /// </summary>
    public FastbootResponse GsiStatus() => GsiCommand("status");

    /// <summary>
    /// Wipes the GSI installation on the device.
    /// <para>清除设备上的 GSI 安装。</para>
    /// </summary>
    public FastbootResponse GsiWipe() => GsiCommand("wipe");

    /// <summary>
    /// Performs a snapshot update action on the device. Common actions include "cancel" to
    /// abort an in-progress update. If the device requests a reboot to fastbootd to complete
    /// the action, a reboot-fastboot is issued.
    /// <para>在设备上执行快照更新操作。常见操作包括 "cancel" 以中止正在进行的更新。
    /// 如果设备请求重启到 fastbootd 以完成操作，则发送 reboot-fastboot。</para>
    /// </summary>
    public FastbootResponse SnapshotUpdate(string action = "cancel")
    {
        var response = ExecuteCommand(FastbootConstants.CommandSnapshotUpdate + action);
        var requestsReboot =
            response.Info.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase) ||
            response.Text.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase) ||
            response.InfoLines.Any(line => line.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase));
        if (requestsReboot)
        {
            Reboot("fastboot");
        }

        return response;
    }

    /// <summary>
    /// Sends an OEM-specific command to the device. INFO/TEXT frames emitted by the command
    /// are accumulated on the returned response (InfoLines/Text).
    /// <para>向设备发送 OEM 特定命令。命令产生的 INFO/TEXT 帧累积到返回响应的
    /// InfoLines/Text 中。</para>
    /// </summary>
    public FastbootResponse OemCommand(string oemCommand)
        => ExecuteCommand(FastbootConstants.CommandOem + oemCommand);

    /// <summary>
    /// Boots the device using a boot image from a stream: downloads the image and sends
    /// the boot command.
    /// <para>使用来自流的启动镜像启动设备：下载镜像并发送 boot 命令。</para>
    /// </summary>
    public FastbootResponse Boot(Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DownloadData(stream, length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(FastbootConstants.CommandBoot);
    }

    /// <summary>
    /// Boots the device using the specified boot image data.
    /// <para>使用指定的启动镜像数据启动设备。</para>
    /// </summary>
    public FastbootResponse Boot(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return Boot(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Boots the device using a boot image file.
    /// <para>使用启动镜像文件启动设备。</para>
    /// </summary>
    public FastbootResponse BootFromFile(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(filePath);
        return Boot(fs, fs.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Shuts down the device.
    /// <para>关闭设备。</para>
    /// </summary>
    public FastbootResponse Shutdown()
        => ExecuteCommand(FastbootConstants.CommandShutdown);

    /// <summary>
    /// Flashes a partition with data from a stream.
    /// <para>用流中的数据刷写分区。</para>
    /// </summary>
    public FastbootResponse FlashPartition(string partition, Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default, bool resolveSlot = true)
    {
        ThrowIfDisposed();
        DownloadData(stream, length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(FastbootConstants.CommandFlash + (resolveSlot ? ResolvePartitionWithSlot(partition) : partition));
    }

    /// <summary>
    /// Erases the specified partition.
    /// <para>擦除指定分区。</para>
    /// </summary>
    public FastbootResponse ErasePartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandErase + partition);

    /// <summary>
    /// Formats the specified partition.
    /// <para>格式化指定分区。</para>
    /// </summary>
    public FastbootResponse FormatPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandFormat + partition);

    /// <summary>
    /// Reboots the device. Optionally into a target (recovery, bootloader, etc.).
    /// <para>重启设备，可指定目标（recovery、bootloader 等）。</para>
    /// </summary>
    public FastbootResponse Reboot(string? target = null)
        => ExecuteCommand(string.IsNullOrEmpty(target) ? FastbootConstants.CommandReboot : FastbootConstants.CommandReboot + "-" + target);

    /// <summary>
    /// Continues boot, exiting fastboot mode.
    /// <para>继续启动，退出 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse ContinueBoot()
        => ExecuteCommand(FastbootConstants.CommandContinue);

    /// <summary>
    /// Sets the active A/B slot.
    /// <para>设置活跃的 A/B 槽位。</para>
    /// </summary>
    public FastbootResponse SetActiveSlot(string slot)
        => ExecuteCommand(FastbootConstants.CommandSetActive + slot);

}
