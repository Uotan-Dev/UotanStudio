using System.Globalization;


namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>Updates the super partition metadata: downloads the metadata image and sends update-super:&lt;partition&gt;[:wipe].
    /// <para>更新 super 分区元数据：下载元数据镜像并发送 update-super:&lt;分区&gt;[:wipe]。</para></summary>
    /// <param name="partition">Super partition name to update; must not be empty.
    /// <para>要更新的 super 分区名；不得为空。</para></param>
    /// <param name="metadataImage">Metadata image stream read from its current position for its whole <see cref="Stream.Length"/>; the caller retains ownership.
    /// <para>元数据镜像流，从当前位置按整个 <see cref="Stream.Length"/> 读取；所有权由调用方保留。</para></param>
    /// <param name="wipe">When <see langword="true"/>, appends <c>:wipe</c> so the device also wipes the partition contents.
    /// <para>为 <see langword="true"/> 时追加 <c>:wipe</c>，使设备同时清除分区内容。</para></param>
    /// <param name="progress">Receives the cumulative downloaded byte count; may be <see langword="null"/>.
    /// <para>接收累计已下载字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the download between chunks; an in-flight device write is not interrupted.
    /// <para>在分块下载的各块之间取消；不会打断正在进行的设备写入。</para></param>
    /// <returns>The parsed update-super response.<para>解析后的 update-super 响应。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="partition"/> is empty.
    /// <para><paramref name="partition"/> 为空。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已释放。</para></exception>
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

    /// <summary>Updates the super partition metadata from a metadata image file.
    /// <para>从元数据镜像文件更新 super 分区元数据。</para></summary>
    /// <param name="partition">Super partition name to update; must not be empty.
    /// <para>要更新的 super 分区名；不得为空。</para></param>
    /// <param name="metadataImagePath">Path to the metadata image file, opened for reading and closed when the call returns.
    /// <para>元数据镜像文件路径；以只读方式打开，调用返回时关闭。</para></param>
    /// <param name="wipe">When <see langword="true"/>, requests a super wipe alongside the metadata update.
    /// <para>为 <see langword="true"/> 时在更新元数据的同时请求清除 super。</para></param>
    /// <param name="progress">Receives the cumulative downloaded byte count; may be <see langword="null"/>.
    /// <para>接收累计已下载字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the download between chunks.
    /// <para>在分块下载的各块之间取消。</para></param>
    /// <returns>The parsed update-super response.<para>解析后的 update-super 响应。</para></returns>
    public FastbootResponse UpdateSuper(string partition, string metadataImagePath, bool wipe = false, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(metadataImagePath);
        return UpdateSuper(partition, fs, wipe, progress, cancellationToken);
    }

    /// <summary>
    /// Creates a logical partition with the specified size. Requires userspace fastboot mode.
    /// <para>创建指定大小的逻辑分区。需要用户空间 fastboot 模式。</para>
    /// </summary>
    /// <param name="partition">Logical partition name to create. <para>要创建的逻辑分区名。</para></param>
    /// <param name="size">Requested partition size in bytes. <para>请求的分区大小（字节）。</para></param>
    /// <returns>The parsed create-logical-partition response. <para>解析后的 create-logical-partition 响应。</para></returns>
    public FastbootResponse CreateLogicalPartition(string partition, long size)
        => ExecuteCommand($"{FastbootConstants.CommandCreateLogicalPartition}{partition}:{size}");

    /// <summary>
    /// Deletes a logical partition. Requires userspace fastboot mode.
    /// <para>删除逻辑分区。需要用户空间 fastboot 模式。</para>
    /// </summary>
    /// <param name="partition">Logical partition name to delete. <para>要删除的逻辑分区名。</para></param>
    /// <returns>The parsed delete-logical-partition response. <para>解析后的 delete-logical-partition 响应。</para></returns>
    public FastbootResponse DeleteLogicalPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandDeleteLogicalPartition + partition);

    /// <summary>
    /// Resizes a logical partition to the specified size. Requires userspace fastboot mode.
    /// <para>将逻辑分区调整为指定大小。需要用户空间 fastboot 模式。</para>
    /// </summary>
    /// <param name="partition">Logical partition name to resize. <para>要调整大小的逻辑分区名。</para></param>
    /// <param name="size">New partition size in bytes. <para>新分区大小（字节）。</para></param>
    /// <returns>The parsed resize-logical-partition response. <para>解析后的 resize-logical-partition 响应。</para></returns>
    public FastbootResponse ResizeLogicalPartition(string partition, long size)
        => ExecuteCommand($"{FastbootConstants.CommandResizeLogicalPartition}{partition}:{size}");

    /// <summary>
    /// Wipes the super partition via the device-side wipe-super command.
    /// <para>通过设备侧 wipe-super 命令清除 super 分区。</para>
    /// </summary>
    /// <param name="partition">Super partition name to wipe. <para>要清除的 super 分区名。</para></param>
    /// <returns>The parsed wipe-super response. <para>解析后的 wipe-super 响应。</para></returns>
    public FastbootResponse WipeSuper(string partition)
        => ExecuteCommand(FastbootConstants.CommandWipeSuper + partition);

    /// <summary>
    /// Sends a flashing sub-command to the device (e.g. unlock, lock, get_unlock_ability).
    /// <para>向设备发送 flashing 子命令（如 unlock、lock、get_unlock_ability）。</para>
    /// </summary>
    /// <param name="subCommand">Sub-command appended after the <c>flashing:</c> prefix. <para>追加在 <c>flashing:</c> 前缀之后的子命令。</para></param>
    /// <returns>The parsed flashing response. <para>解析后的 flashing 响应。</para></returns>
    public FastbootResponse FlashingCommand(string subCommand)
        => ExecuteCommand(FastbootConstants.CommandFlashing + subCommand);

    /// <summary>
    /// Gets the flashing unlock ability value. Returns 0 if unavailable or on failure.
    /// <para>获取 flashing 解锁能力值。不可用或失败时返回 0。</para>
    /// </summary>
    /// <returns>The parsed unlock-ability value, or 0 when the command fails or the value is not an integer.
    /// <para>解析出的解锁能力值；命令失败或值非整数时返回 0。</para></returns>
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
    /// <returns><see langword="true"/> when the unlock-ability value is 1. <para>解锁能力值为 1 时返回 <see langword="true"/>。</para></returns>
    public bool IsUnlockAbilityEnabled() => FlashingGetUnlockAbility() == 1;

    /// <summary>
    /// Unlocks the device via flashing command.
    /// <para>通过 flashing 命令解锁设备。</para>
    /// </summary>
    /// <returns>The parsed flashing unlock response. <para>解析后的 flashing 解锁响应。</para></returns>
    public FastbootResponse FlashingUnlock() => FlashingCommand("unlock");

    /// <summary>
    /// Locks the device via flashing command.
    /// <para>通过 flashing 命令锁定设备。</para>
    /// </summary>
    /// <returns>The parsed flashing lock response. <para>解析后的 flashing 锁定响应。</para></returns>
    public FastbootResponse FlashingLock() => FlashingCommand("lock");

    /// <summary>
    /// Unlocks critical partitions via flashing command.
    /// <para>通过 flashing 命令解锁关键分区。</para>
    /// </summary>
    /// <returns>The parsed flashing unlock_critical response. <para>解析后的 flashing unlock_critical 响应。</para></returns>
    public FastbootResponse FlashingUnlockCritical() => FlashingCommand("unlock_critical");

    /// <summary>
    /// Locks critical partitions via flashing command.
    /// <para>通过 flashing 命令锁定关键分区。</para>
    /// </summary>
    /// <returns>The parsed flashing lock_critical response. <para>解析后的 flashing lock_critical 响应。</para></returns>
    public FastbootResponse FlashingLockCritical() => FlashingCommand("lock_critical");

    /// <summary>
    /// Sends a GSI (Generic System Image) sub-command to the device.
    /// <para>向设备发送 GSI（通用系统镜像）子命令。</para>
    /// </summary>
    /// <param name="subCommand">Sub-command appended after the <c>gsi:</c> prefix. <para>追加在 <c>gsi:</c> 前缀之后的子命令。</para></param>
    /// <returns>The parsed gsi response. <para>解析后的 gsi 响应。</para></returns>
    public FastbootResponse GsiCommand(string subCommand)
        => ExecuteCommand(FastbootConstants.CommandGsi + subCommand);

    /// <summary>
    /// Enables the GSI on the device.
    /// <para>启用设备上的 GSI。</para>
    /// </summary>
    /// <returns>The parsed gsi enable response. <para>解析后的 gsi enable 响应。</para></returns>
    public FastbootResponse GsiEnable() => GsiCommand("enable");

    /// <summary>
    /// Disables the GSI on the device.
    /// <para>禁用设备上的 GSI。</para>
    /// </summary>
    /// <returns>The parsed gsi disable response. <para>解析后的 gsi disable 响应。</para></returns>
    public FastbootResponse GsiDisable() => GsiCommand("disable");

    /// <summary>
    /// Queries the GSI installation status on the device.
    /// <para>查询设备上的 GSI 安装状态。</para>
    /// </summary>
    /// <returns>The parsed gsi status response. <para>解析后的 gsi status 响应。</para></returns>
    public FastbootResponse GsiStatus() => GsiCommand("status");

    /// <summary>
    /// Wipes the GSI installation on the device.
    /// <para>清除设备上的 GSI 安装。</para>
    /// </summary>
    /// <returns>The parsed gsi wipe response. <para>解析后的 gsi wipe 响应。</para></returns>
    public FastbootResponse GsiWipe() => GsiCommand("wipe");

    /// <summary>
    /// Performs a snapshot update action on the device; "cancel" aborts an in-progress update.
    /// <para>在设备上执行快照更新操作；"cancel" 用于中止正在进行的更新。</para>
    /// </summary>
    /// <remarks>
    /// <para>A bootloader that can only complete the action from fastbootd says so in free-form reply
    /// text; that request is reported on <see cref="FastbootResponse.RequestsFastbootReboot"/> and
    /// deliberately not acted on here, because a reboot invalidates the caller's session and the hint comes
    /// from untrusted text.</para>
    /// <para>只能在 fastbootd 中完成该操作的 bootloader 会以自由文本应答说明这一点；该请求通过
    /// <see cref="FastbootResponse.RequestsFastbootReboot"/> 上报，并且刻意不在此处执行，因为重启会使
    /// 调用方的会话失效，而该提示来自不可信文本。</para>
    /// </remarks>
    /// <param name="action">Snapshot-update action sent after the <c>snapshot-update:</c> prefix; defaults to <c>cancel</c>.
    /// <para>追加在 <c>snapshot-update:</c> 前缀之后的操作；默认为 <c>cancel</c>。</para></param>
    /// <returns>The parsed snapshot-update response. <para>解析后的 snapshot-update 响应。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse SnapshotUpdate(string action = "cancel")
        => ExecuteCommand(FastbootConstants.CommandSnapshotUpdate + action);

    /// <summary>
    /// Sends an OEM-specific command to the device. INFO/TEXT frames emitted by the command
    /// are accumulated on the returned response (InfoLines/Text).
    /// <para>向设备发送 OEM 特定命令。命令产生的 INFO/TEXT 帧累积到返回响应的
    /// InfoLines/Text 中。</para>
    /// </summary>
    /// <param name="oemCommand">Command body sent after the <c>oem </c> prefix. <para>在 <c>oem </c> 前缀之后发送的命令体。</para></param>
    /// <returns>The parsed OEM response with accumulated INFO/TEXT content. <para>解析后的 OEM 响应，含累积的 INFO/TEXT 内容。</para></returns>
    public FastbootResponse OemCommand(string oemCommand)
        => ExecuteCommand(FastbootConstants.CommandOem + oemCommand);

    /// <summary>Boots the device using a boot image from a stream: downloads the image and sends the boot command.
    /// <para>使用来自流的启动镜像启动设备：下载镜像并发送 boot 命令。</para></summary>
    /// <param name="stream">Boot image stream read for <paramref name="length"/> bytes from its current position; the caller retains ownership.
    /// <para>启动镜像流，从当前位置读取 <paramref name="length"/> 字节；所有权由调用方保留。</para></param>
    /// <param name="length">Exact byte length to download; must be positive and not exceed the negotiated max download size.
    /// <para>要下载的精确字节长度；必须为正且不超过协商的最大下载大小。</para></param>
    /// <param name="progress">Receives the cumulative downloaded byte count; may be <see langword="null"/>.
    /// <para>接收累计已下载字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the download between chunks.
    /// <para>在分块下载的各块之间取消。</para></param>
    /// <returns>The parsed boot response.<para>解析后的 boot 响应。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已释放。</para></exception>
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
    /// <param name="data">Boot image bytes to download; its full length is used. <para>要下载的启动镜像字节；使用其完整长度。</para></param>
    /// <param name="progress">Receives the cumulative downloaded byte count; may be <see langword="null"/>.
    /// <para>接收累计已下载字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the download between chunks. <para>在分块下载的各块之间取消。</para></param>
    /// <returns>The parsed boot response. <para>解析后的 boot 响应。</para></returns>
    public FastbootResponse Boot(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return Boot(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Boots the device using a boot image file.
    /// <para>使用启动镜像文件启动设备。</para>
    /// </summary>
    /// <param name="filePath">Path to the boot image file, opened for reading and closed when the call returns.
    /// <para>启动镜像文件路径；以只读方式打开，调用返回时关闭。</para></param>
    /// <param name="progress">Receives the cumulative downloaded byte count; may be <see langword="null"/>.
    /// <para>接收累计已下载字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the download between chunks. <para>在分块下载的各块之间取消。</para></param>
    /// <returns>The parsed boot response. <para>解析后的 boot 响应。</para></returns>
    public FastbootResponse BootFromFile(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(filePath);
        return Boot(fs, fs.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Shuts down the device.
    /// <para>关闭设备。</para>
    /// </summary>
    /// <returns>The parsed shutdown response. <para>解析后的 shutdown 响应。</para></returns>
    public FastbootResponse Shutdown()
        => ExecuteCommand(FastbootConstants.CommandShutdown);

    /// <summary>
    /// Flashes a partition with data from a stream.
    /// <para>用流中的数据刷写分区。</para>
    /// Before any payload is transferred the target is validated against the device through the read-only
    /// queries selected by <paramref name="options"/> (see <see cref="FlashPartitionPreflight"/>), so a
    /// rejected flash leaves the device untouched instead of failing after a full image transfer.
    /// <para>在传输任何负载之前，先通过 <paramref name="options"/> 选定的只读查询对照设备校验目标
    /// （见 <see cref="FlashPartitionPreflight"/>），因此被拒绝的刷写不会改动设备，而不是在整镜像传完
    /// 之后才失败。</para>
    /// </summary>
    /// <param name="partition">Target partition name; resolved against A/B slots unless <paramref name="resolveSlot"/> is <see langword="false"/>.
    /// <para>目标分区名；除 <paramref name="resolveSlot"/> 为 <see langword="false"/> 外，按 A/B 槽位解析。</para></param>
    /// <param name="stream">Image stream read for <paramref name="length"/> bytes from its current position; the caller retains ownership.
    /// <para>镜像流，从当前位置读取 <paramref name="length"/> 字节；所有权由调用方保留。</para></param>
    /// <param name="length">Exact byte length to download; must be positive and not exceed the negotiated max download size.
    /// <para>要下载的精确字节长度；必须为正且不超过协商的最大下载大小。</para></param>
    /// <param name="progress">Receives the cumulative downloaded byte count; may be <see langword="null"/>.
    /// <para>接收累计已下载字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels before the preflight queries and between download chunks. <para>在预检查询之前以及下载各分块之间取消。</para></param>
    /// <param name="resolveSlot">When <see langword="true"/>, the partition name is slot-resolved before the preflight and the flash.
    /// <para>为 <see langword="true"/> 时，预检与刷写前先对分区名做槽位解析。</para></param>
    /// <param name="options">Preflight policy; <see langword="null"/> means <see cref="FlashPartitionOptions.Default"/>, which enables every check.
    /// Callers that already validated the target pass a narrowed policy (or
    /// <see cref="FlashPartitionOptions.NoPreflight"/>) instead of paying duplicate getvar round trips.
    /// <para>预检策略；<see langword="null"/> 表示 <see cref="FlashPartitionOptions.Default"/>，即启用全部检查。
    /// 已自行校验目标的调用方应传入收窄后的策略（或 <see cref="FlashPartitionOptions.NoPreflight"/>），
    /// 而不是重复支付 getvar 往返。</para></param>
    /// <returns>The parsed flash response. <para>解析后的 flash 响应。</para></returns>
    /// <exception cref="FastbootException">An enabled preflight check was not satisfied, the payload exceeds the negotiated max-download-size, or the device rejected the download or the flash.
    /// <para>某项已启用的预检未通过、负载超过协商的 max-download-size，或设备拒绝了下载或刷写。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse FlashPartition(string partition, Stream stream, long length, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default, bool resolveSlot = true, FlashPartitionOptions? options = null)
    {
        ThrowIfDisposed();
        var target = resolveSlot ? ResolvePartitionWithSlot(partition) : partition;
        FlashPartitionPreflight.Validate(this, target, length, options ?? FlashPartitionOptions.Default, cancellationToken);
        DownloadData(stream, length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(FastbootConstants.CommandFlash + target);
    }

    /// <summary>
    /// Erases the specified partition.
    /// <para>擦除指定分区。</para>
    /// </summary>
    /// <param name="partition">Partition name to erase, used verbatim without slot resolution. <para>要擦除的分区名，原样使用而不做槽位解析。</para></param>
    /// <returns>The parsed erase response. <para>解析后的 erase 响应。</para></returns>
    public FastbootResponse ErasePartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandErase + partition);

    /// <summary>
    /// Formats the specified partition.
    /// <para>格式化指定分区。</para>
    /// </summary>
    /// <param name="partition">Partition name to format. <para>要格式化的分区名。</para></param>
    /// <returns>The parsed format response. <para>解析后的 format 响应。</para></returns>
    public FastbootResponse FormatPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandFormat + partition);

    /// <summary>
    /// Reboots the device. Optionally into a target (recovery, bootloader, etc.).
    /// <para>重启设备，可指定目标（recovery、bootloader 等）。</para>
    /// </summary>
    /// <param name="target">Reboot target suffix, or <see langword="null"/> for a plain reboot. <para>重启目标后缀；为 <see langword="null"/> 时执行普通重启。</para></param>
    /// <returns>The parsed reboot response. <para>解析后的 reboot 响应。</para></returns>
    public FastbootResponse Reboot(string? target = null)
        => ExecuteCommand(string.IsNullOrEmpty(target) ? FastbootConstants.CommandReboot : FastbootConstants.CommandReboot + "-" + target);

    /// <summary>
    /// Continues boot, exiting fastboot mode.
    /// <para>继续启动，退出 fastboot 模式。</para>
    /// </summary>
    /// <returns>The parsed continue response. <para>解析后的 continue 响应。</para></returns>
    public FastbootResponse ContinueBoot()
        => ExecuteCommand(FastbootConstants.CommandContinue);

    /// <summary>
    /// Sets the active A/B slot.
    /// <para>设置活跃的 A/B 槽位。</para>
    /// </summary>
    /// <param name="slot">Slot suffix to activate (for example <c>_a</c> or <c>_b</c>). <para>要激活的槽位后缀（如 <c>_a</c> 或 <c>_b</c>）。</para></param>
    /// <returns>The parsed set-active response. <para>解析后的 set-active 响应。</para></returns>
    public FastbootResponse SetActiveSlot(string slot)
        => ExecuteCommand(FastbootConstants.CommandSetActive + slot);

}