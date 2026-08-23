namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Fastboot wire protocol constants (AOSP fastboot.h).
/// <para>Fastboot 线上协议常量（AOSP fastboot.h）。</para>
/// </summary>
public static class FastbootConstants
{
    /// <summary>
    /// Maximum fastboot command length (AOSP FB_COMMAND_SZ = 4096).
    /// <para>Fastboot 命令最大长度（AOSP FB_COMMAND_SZ = 4096）。</para>
    /// </summary>
    public const int MaxCommandLength = 4096;

    /// <summary>
    /// Response buffer size (AOSP FB_RESPONSE_SZ = 256).
    /// <para>响应缓冲区大小（AOSP FB_RESPONSE_SZ = 256）。</para>
    /// </summary>
    public const int ResponseBufferSize = 256;

    /// <summary>
    /// Maximum per-transfer write size.
    /// <para>单次传输最大写入大小。</para>
    /// </summary>
    public const int TransferChunkSize = 512 * 1024;

    /// <summary>
    /// Ceiling for download/sparse transfers, matching the wire-level DATA size field width
    /// (8 hex digits, 32-bit) and AOSP MAX_DOWNLOAD_SIZE = UINT32_MAX.
    /// <para>下载/稀疏传输上限，与线协议 DATA 尺寸字段宽度（16 进制 8 位、32 位）及
    /// AOSP MAX_DOWNLOAD_SIZE = UINT32_MAX 一致。</para>
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

    // Response prefix strings (4-char status codes).
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

    // USB class/subclass/protocol for fastboot devices.
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

    // Common fastboot commands.
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
    /// upload command prefix.
    /// <para>upload 命令前缀。</para>
    /// </summary>
    public const string CommandUpload = "upload:";

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
    /// format command prefix.
    /// <para>format 命令前缀。</para>
    /// </summary>
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
    /// wipe-super command prefix (wipe-super:partition).
    /// <para>wipe-super 命令前缀（wipe-super:分区）。</para>
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
    /// boot command (boots a previously downloaded image).
    /// <para>boot 命令（启动先前下载的镜像）。</para>
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

    // Common fastboot variable names/prefixes (getvar keys).
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
}