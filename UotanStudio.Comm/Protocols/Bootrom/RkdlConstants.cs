namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Rockchip Bootrom (RKDL) protocol constants.
/// <para>Rockchip Bootrom（RKDL）协议常量。</para>
/// </summary>
public static class RkdlConstants
{
    /// <summary>
    /// Rockchip USB vendor ID.
    /// <para>Rockchip USB 供应商 ID。</para>
    /// </summary>
    public const ushort RockchipVid = 0x2207;

    /// <summary>
    /// RKDL vendor request: download 471 (first stage loader).
    /// <para>RKDL 供应商请求：下载 471（第一阶段加载程序）。</para>
    /// </summary>
    public const byte RkdlRequest471 = 0x47;

    /// <summary>
    /// RKDL vendor request: download 472 (second stage loader).
    /// <para>RKDL 供应商请求：下载 472（第二阶段加载程序）。</para>
    /// </summary>
    public const byte RkdlRequest472 = 0x47;

    /// <summary>
    /// RKDL vendor request: read chip info.
    /// <para>RKDL 供应商请求：读取芯片信息。</para>
    /// </summary>
    public const byte RkdlRequestChipInfo = 0x01;

    /// <summary>
    /// RKDL vendor request: read flash info.
    /// <para>RKDL 供应商请求：读取闪存信息。</para>
    /// </summary>
    public const byte RkdlRequestFlashInfo = 0x02;

    /// <summary>
    /// RKDL vendor request: erase flash.
    /// <para>RKDL 供应商请求：擦除闪存。</para>
    /// </summary>
    public const byte RkdlRequestEraseFlash = 0x03;

    /// <summary>
    /// RKDL vendor request: reset device.
    /// <para>RKDL 供应商请求：复位设备。</para>
    /// </summary>
    public const byte RkdlRequestReset = 0x04;

    /// <summary>
    /// RKDL vendor request: read flash ID.
    /// <para>RKDL 供应商请求：读取闪存 ID。</para>
    /// </summary>
    public const byte RkdlRequestFlashId = 0x05;

    /// <summary>
    /// Maximum transfer size per bulk write (64 KB).
    /// <para>每次批量写入的最大传输大小（64 KB）。</para>
    /// </summary>
    public const int TransferChunkSize = 64 * 1024;

    /// <summary>
    /// Maximum loader size (1 MB).
    /// <para>最大加载程序大小（1 MB）。</para>
    /// </summary>
    public const int MaxLoaderSize = 1024 * 1024;

    /// <summary>
    /// Default USB timeout (30 seconds).
    /// <para>默认 USB 超时（30 秒）。</para>
    /// </summary>
    public const int DefaultTimeoutMs = 30000;
}

/// <summary>
/// RKDL command types.
/// <para>RKDL 命令类型。</para>
/// </summary>
public enum RkdlCommand : byte
{
    /// <summary>
    /// Download boot command.
    /// <para>下载启动命令。</para>
    /// </summary>
    DownloadBoot = 0x01,

    /// <summary>
    /// Upgrade loader command.
    /// <para>升级加载程序命令。</para>
    /// </summary>
    UpgradeLoader = 0x02,

    /// <summary>
    /// Read LBA command.
    /// <para>读取 LBA 命令。</para>
    /// </summary>
    ReadLBA = 0x03,

    /// <summary>
    /// Write LBA command.
    /// <para>写入 LBA 命令。</para>
    /// </summary>
    WriteLBA = 0x04,

    /// <summary>
    /// Write GPT command.
    /// <para>写入 GPT 命令。</para>
    /// </summary>
    WriteGPT = 0x05,

    /// <summary>
    /// Erase flash command.
    /// <para>擦除闪存命令。</para>
    /// </summary>
    EraseFlash = 0x06,

    /// <summary>
    /// Test device command.
    /// <para>测试设备命令。</para>
    /// </summary>
    TestDevice = 0x07,

    /// <summary>
    /// Reset device command.
    /// <para>复位设备命令。</para>
    /// </summary>
    ResetDevice = 0x08,

    /// <summary>
    /// Reboot to mask ROM command.
    /// <para>重启到 Mask ROM 命令。</para>
    /// </summary>
    RebootMaskRom = 0x09,

    /// <summary>
    /// Shutdown command.
    /// <para>关机命令。</para>
    /// </summary>
    Shutdown = 0x0A,

    /// <summary>
    /// Read flash ID command.
    /// <para>读取闪存 ID 命令。</para>
    /// </summary>
    ReadFlashId = 0x0B,

    /// <summary>
    /// Read flash info command.
    /// <para>读取闪存信息命令。</para>
    /// </summary>
    ReadFlashInfo = 0x0C,

    /// <summary>
    /// Read chip info command.
    /// <para>读取芯片信息命令。</para>
    /// </summary>
    ReadChipInfo = 0x0D,

    /// <summary>
    /// Read capability command.
    /// <para>读取能力命令。</para>
    /// </summary>
    ReadCapability = 0x0E,
}

/// <summary>
/// RKDL chip info structure.
/// <para>RKDL 芯片信息结构。</para>
/// </summary>
public class RkChipInfo
{
    /// <summary>
    /// Gets or sets the chip ID.
    /// <para>获取或设置芯片 ID。</para>
    /// </summary>
    public uint ChipId { get; set; }

    /// <summary>
    /// Gets or sets the chip revision.
    /// <para>获取或设置芯片版本。</para>
    /// </summary>
    public uint Revision { get; set; }

    /// <summary>
    /// Gets the human-readable chip name.
    /// <para>获取人类可读的芯片名称。</para>
    /// </summary>
    public string ChipName => GetChipName(ChipId);

    /// <summary>
    /// Returns the chip name for a given chip ID.
    /// <para>返回给定芯片 ID 的芯片名称。</para>
    /// </summary>
    /// <param name="chipId">The chip ID. 芯片 ID。</param>
    /// <returns>The chip name. 芯片名称。</returns>
    public static string GetChipName(uint chipId)
    {
        return chipId switch
        {
            0x300A => "RK3066",
            0x320A => "RK3229",
            0x320B => "RK3288",
            0x330A => "RK3326",
            0x330B => "RK3308",
            0x330C => "RK3399",
            0x330D => "RK3368",
            0x350A => "RK3566",
            0x350B => "RK3588",
            0x350C => "RK3568",
            0x350D => "RK3528",
            0x350E => "RK3562",
            _ => $"Unknown(0x{chipId:X4})"
        };
    }
}

/// <summary>
/// RKDL flash info structure.
/// <para>RKDL 闪存信息结构。</para>
/// </summary>
public class RkFlashInfo
{
    /// <summary>
    /// Gets or sets the total flash size in bytes.
    /// <para>获取或设置闪存总大小（字节）。</para>
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the sector size in bytes.
    /// <para>获取或设置扇区大小（字节）。</para>
    /// </summary>
    public int SectorSize { get; set; }

    /// <summary>
    /// Gets the total sector count.
    /// <para>获取扇区总数。</para>
    /// </summary>
    public long SectorCount => SectorSize > 0 ? Size / SectorSize : 0;
}

/// <summary>
/// RKDL error codes.
/// <para>RKDL 错误码。</para>
/// </summary>
public enum RkdlError
{
    /// <summary>
    /// Operation completed successfully.
    /// <para>操作成功完成。</para>
    /// </summary>
    Success = 0,

    /// <summary>
    /// Invalid parameter provided.
    /// <para>提供了无效参数。</para>
    /// </summary>
    InvalidParameter = 1,

    /// <summary>
    /// Device not found.
    /// <para>未找到设备。</para>
    /// </summary>
    DeviceNotFound = 2,

    /// <summary>
    /// Data transfer failed.
    /// <para>数据传输失败。</para>
    /// </summary>
    TransferFailed = 3,

    /// <summary>
    /// Loader image is too large.
    /// <para>加载程序镜像过大。</para>
    /// </summary>
    LoaderTooLarge = 4,

    /// <summary>
    /// Device was reset.
    /// <para>设备已复位。</para>
    /// </summary>
    DeviceReset = 5,

    /// <summary>
    /// Operation not supported.
    /// <para>不支持该操作。</para>
    /// </summary>
    NotSupported = 6,

    /// <summary>
    /// Failed to read chip info.
    /// <para>读取芯片信息失败。</para>
    /// </summary>
    ChipInfoFailed = 7,

    /// <summary>
    /// Failed to read flash info.
    /// <para>读取闪存信息失败。</para>
    /// </summary>
    FlashInfoFailed = 8,

    /// <summary>
    /// Erase operation failed.
    /// <para>擦除操作失败。</para>
    /// </summary>
    EraseFailed = 9,

    /// <summary>
    /// Write operation failed.
    /// <para>写入操作失败。</para>
    /// </summary>
    WriteFailed = 10,

    /// <summary>
    /// Read operation failed.
    /// <para>读取操作失败。</para>
    /// </summary>
    ReadFailed = 11,
}

/// <summary>
/// Exception thrown when an RKDL operation fails.
/// <para>RKDL 操作失败时抛出的异常。</para>
/// </summary>
public class RkdlException : Exception
{
    /// <summary>
    /// Gets the RKDL error code.
    /// <para>获取 RKDL 错误码。</para>
    /// </summary>
    public RkdlError ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RkdlException"/> class.
    /// <para>初始化 <see cref="RkdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. 错误码。</param>
    /// <param name="message">The error message. 错误消息。</param>
    public RkdlException(RkdlError errorCode, string message)
        : base(message) { ErrorCode = errorCode; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RkdlException"/> class.
    /// <para>初始化 <see cref="RkdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. 错误码。</param>
    /// <param name="message">The error message. 错误消息。</param>
    /// <param name="innerException">The inner exception. 内部异常。</param>
    public RkdlException(RkdlError errorCode, string message, Exception innerException)
        : base(message, innerException) { ErrorCode = errorCode; }
}
