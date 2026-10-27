namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Fastboot wire protocol constants.
/// <para>Fastboot 线上协议常量。</para>
/// </summary>
public static class FastbootConstants
{
    /// <summary>
    /// Maximum fastboot command length, fixed at 4096 bytes.
    /// <para>Fastboot 命令最大长度，固定 4096 字节。</para>
    /// </summary>
    public const int MaxCommandLength = 4096;

    /// <summary>
    /// Response buffer size, fixed at 256 bytes.
    /// <para>响应缓冲区大小，固定 256 字节。</para>
    /// </summary>
    public const int ResponseBufferSize = 256;

    /// <summary>
    /// Safety ceiling on bytes the response framer retains while waiting for a terminal frame, so a
    /// hostile device that keeps streaming unparsable bytes cannot grow the buffer without bound.
    /// <para>响应成帧器等待终止帧期间保留字节数的安全上限，使持续发送无法解析字节的恶意设备
    /// 无法无界增长缓冲区。</para>
    /// </summary>
    public const int MaxPendingResponseBytes = 1024 * 1024;

    /// <summary>
    /// Maximum per-transfer write size.
    /// <para>单次传输最大写入大小。</para>
    /// </summary>
    public const int TransferChunkSize = 512 * 1024;

    /// <summary>
    /// Ceiling for download/sparse transfers: the wire-level DATA size field is 8 hex digits wide,
    /// so the largest representable transfer is <see cref="uint.MaxValue"/>.
    /// <para>下载/稀疏传输上限：线协议 DATA 的尺寸字段为 16 进制 8 位，因此可表示的最大传输量是
    /// <see cref="uint.MaxValue"/>。</para>
    /// </summary>
    public const long SparseMaxDownloadSize = uint.MaxValue;

    /// <summary>
    /// Fallback max download size when device doesn't report one.
    /// <para>设备未报告最大下载大小时的回退值。</para>
    /// </summary>
    public const long FallbackMaxDownloadSize = 256L * 1024 * 1024;

    /// <summary>
    /// Default response timeout in seconds.
    /// <para>默认响应超时（秒）。</para>
    /// </summary>
    public const int DefaultResponseTimeoutSeconds = 30;

    /// <summary>
    /// OKAY response prefix.
    /// <para>OKAY 响应前缀。</para>
    /// </summary>
    public const string Okay = "OKAY";

    /// <summary>
    /// FAIL response prefix.
    /// <para>FAIL 响应前缀。</para>
    /// </summary>
    public const string Fail = "FAIL";

    /// <summary>
    /// INFO response prefix.
    /// <para>INFO 响应前缀。</para>
    /// </summary>
    public const string Info = "INFO";

    /// <summary>
    /// TEXT response prefix.
    /// <para>TEXT 响应前缀。</para>
    /// </summary>
    public const string Text = "TEXT";

    /// <summary>
    /// DATA response prefix.
    /// <para>DATA 响应前缀。</para>
    /// </summary>
    public const string Data = "DATA";

    /// <summary>
    /// USB interface class for fastboot devices.
    /// <para>Fastboot 设备的 USB 接口类。</para>
    /// </summary>
    public const byte FastbootInterfaceClass = 0xFF;

    /// <summary>
    /// USB interface subclass for fastboot devices.
    /// <para>Fastboot 设备的 USB 接口子类。</para>
    /// </summary>
    public const byte FastbootInterfaceSubclass = 0x42;

    /// <summary>
    /// USB interface protocol for fastboot devices.
    /// <para>Fastboot 设备的 USB 接口协议。</para>
    /// </summary>
    public const byte FastbootInterfaceProtocol = 0x03;

    /// <summary>
    /// getvar command prefix.
    /// <para>getvar 命令前缀。</para>
    /// </summary>
    public const string CommandGetVar = "getvar:";

    /// <summary>
    /// getvar:all command, listing all device variables via INFO frames.
    /// <para>getvar:all 命令，通过 INFO 帧列出所有设备变量。</para>
    /// </summary>
    public const string CommandGetVarAll = "getvar:all";

    /// <summary>
    /// download command prefix.
    /// <para>download 命令前缀。</para>
    /// </summary>
    public const string CommandDownload = "download:";

    /// <summary>
    /// fetch command prefix (fetch:partition[:offset[:size]]).
    /// <para>fetch 命令前缀（fetch:分区[:偏移[:大小]]）。</para>
    /// </summary>
    public const string CommandFetch = "fetch:";

    /// <summary>
    /// upload command: retrieves the data staged by a previous download (e.g. a boot image). The wire
    /// form is the bare word <c>upload</c>, with no colon and no filename.
    /// <para>upload 命令：取回先前 download 暂存的数据（如启动镜像）。线协议形式即裸词
    /// <c>upload</c>，不带冒号也不带文件名。</para>
    /// </summary>
    public const string CommandUpload = "upload";

    /// <summary>
    /// get_staged command name. Retained for source compatibility only; it is never sent on
    /// the wire because staged data is retrieved with <see cref="CommandUpload"/>.
    /// <para>get_staged 命令名。仅为源码兼容保留；暂存数据用 <see cref="CommandUpload"/> 取回，
    /// 因此本常量绝不在线协议中发送。</para>
    /// </summary>
    public const string CommandGetStaged = "get_staged";

    /// <summary>
    /// flash command prefix.
    /// <para>flash 命令前缀。</para>
    /// </summary>
    public const string CommandFlash = "flash:";

    /// <summary>
    /// erase command prefix.
    /// <para>erase 命令前缀。</para>
    /// </summary>
    public const string CommandErase = "erase:";

    /// <summary>
    /// format command prefix. Not part of the fastboot wire protocol and never sent on the
    /// wire; retained for source compatibility only.
    /// <para>format 命令前缀。不属于 fastboot 线协议，也绝不在线协议中发送；
    /// 仅为源码兼容保留。</para>
    /// </summary>
    [Obsolete("format: is not an AOSP fastboot command and is never sent on the wire; erase the partition instead.")]
    public const string CommandFormat = "format:";

    /// <summary>
    /// update-super command prefix (update-super:partition[:wipe]).
    /// <para>update-super 命令前缀（update-super:分区[:wipe]）。</para>
    /// </summary>
    public const string CommandUpdateSuper = "update-super:";

    /// <summary>
    /// create-logical-partition command prefix (create-logical-partition:partition:size).
    /// <para>create-logical-partition 命令前缀（create-logical-partition:分区:大小）。</para>
    /// </summary>
    public const string CommandCreateLogicalPartition = "create-logical-partition:";

    /// <summary>
    /// delete-logical-partition command prefix (delete-logical-partition:partition).
    /// <para>delete-logical-partition 命令前缀（delete-logical-partition:分区）。</para>
    /// </summary>
    public const string CommandDeleteLogicalPartition = "delete-logical-partition:";

    /// <summary>
    /// resize-logical-partition command prefix (resize-logical-partition:partition:size).
    /// <para>resize-logical-partition 命令前缀（resize-logical-partition:分区:大小）。</para>
    /// </summary>
    public const string CommandResizeLogicalPartition = "resize-logical-partition:";

    /// <summary>
    /// Host-side <c>wipe-super</c> command name. It is not a fastboot wire command: wiping the super
    /// partition sends <see cref="CommandUpdateSuper"/> followed by the partition name and <c>:wipe</c>.
    /// <para>宿主侧 <c>wipe-super</c> 命令名。它不是 fastboot 线协议命令：清除 super 分区时发送
    /// <see cref="CommandUpdateSuper"/>，后接分区名与 <c>:wipe</c>。</para>
    /// </summary>
    public const string CommandWipeSuper = "wipe-super:";

    /// <summary>
    /// flashing command prefix, followed by a sub-command
    /// (e.g. "flashing unlock", "flashing get_unlock_ability").
    /// <para>flashing 命令前缀，后接子命令（如 "flashing unlock"、"flashing get_unlock_ability"）。</para>
    /// </summary>
    public const string CommandFlashing = "flashing ";

    /// <summary>
    /// gsi command prefix (gsi:enable/disable/status/wipe).
    /// <para>gsi 命令前缀（gsi:enable/disable/status/wipe）。</para>
    /// </summary>
    public const string CommandGsi = "gsi:";

    /// <summary>
    /// snapshot-update command prefix (e.g. snapshot-update:cancel).
    /// <para>snapshot-update 命令前缀（如 snapshot-update:cancel）。</para>
    /// </summary>
    public const string CommandSnapshotUpdate = "snapshot-update:";

    /// <summary>
    /// oem command prefix, followed by an OEM-specific command (e.g. "oem device-info").
    /// <para>oem 命令前缀，后接 OEM 特定命令（如 "oem device-info"）。</para>
    /// </summary>
    public const string CommandOem = "oem ";

    /// <summary>
    /// boot command (boots the image downloaded earlier in the same fastboot session).
    /// <para>boot 命令（启动同一 fastboot 会话中较早下载的镜像）。</para>
    /// </summary>
    public const string CommandBoot = "boot";

    /// <summary>
    /// reboot command.
    /// <para>reboot 命令。</para>
    /// </summary>
    public const string CommandReboot = "reboot";

    /// <summary>
    /// continue command.
    /// <para>continue 命令。</para>
    /// </summary>
    public const string CommandContinue = "continue";

    /// <summary>
    /// shutdown command.
    /// <para>shutdown 命令。</para>
    /// </summary>
    public const string CommandShutdown = "shutdown";

    /// <summary>
    /// set_active command prefix.
    /// <para>set_active 命令前缀。</para>
    /// </summary>
    public const string CommandSetActive = "set_active:";

    /// <summary>
    /// Variable reporting the device's maximum download size.
    /// <para>报告设备最大下载大小的变量。</para>
    /// </summary>
    public const string VarMaxDownloadSize = "max-download-size";

    /// <summary>
    /// Variable reporting the device's maximum fetch size (fastbootd).
    /// <para>报告设备最大 fetch 大小的变量（fastbootd）。</para>
    /// </summary>
    public const string VarMaxFetchSize = "max-fetch-size";

    /// <summary>
    /// Variable prefix reporting partition sizes (partition-size:&lt;partition&gt;).
    /// <para>报告分区大小的变量前缀（partition-size:&lt;分区&gt;）。</para>
    /// </summary>
    public const string VarPartitionSizePrefix = "partition-size:";

    /// <summary>
    /// Variable prefix reporting A/B slot support (has-slot:&lt;partition&gt;).
    /// <para>报告 A/B 槽位支持的变量前缀（has-slot:&lt;分区&gt;）。</para>
    /// </summary>
    public const string VarHasSlotPrefix = "has-slot:";

    /// <summary>
    /// Variable reporting the currently active A/B slot.
    /// <para>报告当前活跃 A/B 槽位的变量。</para>
    /// </summary>
    public const string VarCurrentSlot = "current-slot";

    /// <summary>
    /// Variable reporting whether the device runs userspace fastboot (fastbootd).
    /// <para>报告设备是否运行用户空间 fastboot（fastbootd）的变量。</para>
    /// </summary>
    public const string VarIsUserspace = "is-userspace";

    /// <summary>
    /// Variable prefix reporting whether a partition is logical (is-logical:&lt;partition&gt;).
    /// <para>报告分区是否为逻辑分区的变量前缀（is-logical:&lt;分区&gt;）。</para>
    /// </summary>
    public const string VarIsLogicalPrefix = "is-logical:";

    /// <summary>
    /// Variable reporting the number of A/B slots.
    /// <para>报告 A/B 槽位数量的变量。</para>
    /// </summary>
    public const string VarSlotCount = "slot-count";

    /// <summary>
    /// Variable prefix reporting partition filesystem types (partition-type:&lt;partition&gt;).
    /// <para>报告分区文件系统类型的变量前缀（partition-type:&lt;分区&gt;）。</para>
    /// </summary>
    public const string VarPartitionTypePrefix = "partition-type:";

    /// <summary>
    /// Variable reporting the snapshot update status ("none"/"merging"/"completed").
    /// <para>报告快照更新状态的变量（"none"/"merging"/"completed"）。</para>
    /// </summary>
    public const string VarSnapshotUpdateStatus = "snapshot-update-status";
}