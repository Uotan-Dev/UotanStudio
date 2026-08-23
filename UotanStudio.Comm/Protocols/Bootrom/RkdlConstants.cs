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
    /// RKDL download boot vendor request (BRequest = 0x0C). It is used together with
    /// WIndex = 0x0471 / 0x0472 to download the first/second stage loader (see
    /// rkdeveloptool RKU_DeviceRequest, BmRequestType = 0x40, WValue = 0).
    /// <para>RKDL 下载启动 vendor 请求（BRequest = 0x0C）。配合 WIndex = 0x0471 / 0x0472
    /// 用于下载第一/第二阶段 loader（参见 rkdeveloptool 的 RKU_DeviceRequest，
    /// BmRequestType = 0x40，WValue = 0）。</para>
    /// </summary>
    public const byte RkdlRequestDownloadBoot = 0x0C;

    /// <summary>
    /// RKDL vendor request: download 471 (first stage loader). Alias of
    /// <see cref="RkdlRequestDownloadBoot"/>. The 471/472 distinction lives in the
    /// WIndex field (see <see cref="RkdlWIndex471"/>).
    /// <para>RKDL 供应商请求：下载 471（第一阶段加载程序）。<see cref="RkdlRequestDownloadBoot"/>
    /// 的别名。471/472 的区别体现在 WIndex 字段（见 <see cref="RkdlWIndex471"/>）。</para>
    /// </summary>
    public const byte RkdlRequest471 = RkdlRequestDownloadBoot;

    /// <summary>
    /// RKDL vendor request: download 472 (second stage loader). Alias of
    /// <see cref="RkdlRequestDownloadBoot"/>. The 471/472 distinction lives in the
    /// WIndex field (see <see cref="RkdlWIndex472"/>).
    /// <para>RKDL 供应商请求：下载 472（第二阶段加载程序）。<see cref="RkdlRequestDownloadBoot"/>
    /// 的别名。471/472 的区别体现在 WIndex 字段（见 <see cref="RkdlWIndex472"/>）。</para>
    /// </summary>
    public const byte RkdlRequest472 = RkdlRequestDownloadBoot;

    /// <summary>
    /// WIndex used with <see cref="RkdlRequestDownloadBoot"/> when downloading the first-stage loader.
    /// <para>下载第一阶段 loader 时配合 <see cref="RkdlRequestDownloadBoot"/> 使用的 WIndex。</para>
    /// </summary>
    public const ushort RkdlWIndex471 = 0x0471;

    /// <summary>
    /// WIndex used with <see cref="RkdlRequestDownloadBoot"/> when downloading the second-stage loader.
    /// <para>下载第二阶段 loader 时配合 <see cref="RkdlRequestDownloadBoot"/> 使用的 WIndex。</para>
    /// </summary>
    public const ushort RkdlWIndex472 = 0x0472;

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

    /// <summary>
    /// Sector size in bytes used by the rockusb LBA protocol.
    /// <para>rockusb LBA 协议使用的扇区大小（字节）。</para>
    /// </summary>
    public const int SectorSize = 512;

    /// <summary>
    /// Chunk size used when downloading a loader through the 0x0471/0x0472 vendor request (4 KB).
    /// <para>通过 0x0471/0x0472 vendor 请求下载 loader 时使用的分块大小（4 KB）。</para>
    /// </summary>
    public const int LoaderChunkSize = 4096;

    /// <summary>
    /// Maximum block count per ERASE_NORMAL/ERASE_FORCE command (MAX_ERASE_BLOCKS).
    /// <para>每条 ERASE_NORMAL/ERASE_FORCE 命令的最大块数（MAX_ERASE_BLOCKS）。</para>
    /// </summary>
    public const int MaxEraseBlocksPerCommand = 16;

    /// <summary>
    /// Product IDs of devices already in rockusb (loader) mode, from the upstream
    /// 99-rk-rockusb.rules. Devices with other PIDs under the Rockchip VID that satisfy the
    /// generic rule (pid >> 8 != 0) are treated as maskrom/bootrom devices.
    /// <para>已处于 rockusb（loader）模式的设备产品 ID，来自上游 99-rk-rockusb.rules。
    /// Rockchip VID 下满足通用规则（pid >> 8 != 0）的其他 PID 设备按 maskrom/bootrom 处理。</para>
    /// </summary>
    public static readonly ushort[] KnownLoaderProductIds =
    [
        0x301A, // RK3036
        0x310C, // RK3128
        0x320A, // RK3288
        0x320B, // RK3229
        0x320C, // RK3328
        0x330A, // RK3368
        0x330C, // RK3399
        0x350A, // RK3566
    ];
}

/// <summary>
/// TEST_UNIT_READY sub codes (used in CBWCB.ucReserved, see rkdeveloptool RKComm.h).
/// <para>TEST_UNIT_READY 子码（存于 CBWCB.ucReserved，见 rkdeveloptool 的 RKComm.h）。</para>
/// </summary>
public static class RkdlTestUnitSubCode
{
    /// <summary>Erase the system disk. <para>擦除系统盘。</para></summary>
    public const byte EraseSystem = 0xFE;

    /// <summary>Lower-format the storage. <para>低级格式化存储。</para></summary>
    public const byte LowerFormat = 0xFD;

    /// <summary>Erase user data. <para>擦除用户数据。</para></summary>
    public const byte EraseUserData = 0xFB;
}

/// <summary>
/// READ_LBA/WRITE_LBA access method sub codes (RWMETHOD, see rkdeveloptool RKComm.h).
/// <para>READ_LBA/WRITE_LBA 访问方式子码（RWMETHOD，见 rkdeveloptool 的 RKComm.h）。</para>
/// </summary>
public static class RkdlRwMethod
{
    /// <summary>Address by partition image offset. <para>按分区镜像偏移寻址。</para></summary>
    public const byte Image = 0;

    /// <summary>Address by logical block address. <para>按逻辑块地址寻址。</para></summary>
    public const byte Lba = 1;
}

/// <summary>
/// Rockchip rockusb operation codes (USB_OPERATION_CODE, see rkdeveloptool RKComm.h).
/// <para>Rockchip rockusb 操作码（USB_OPERATION_CODE，见 rkdeveloptool RKComm.h）。</para>
/// </summary>
public enum RkdlCommand : byte
{
    /// <summary>
    /// Test Unit Ready: 0.
    /// <para>就绪检测：0。</para>
    /// </summary>
    TestUnitReady = 0x00,

    /// <summary>
    /// Read Flash ID: 1.
    /// <para>读取 Flash ID：1。</para>
    /// </summary>
    ReadFlashId = 0x01,

    /// <summary>
    /// Read Sector: 4.
    /// <para>读取扇区：4。</para>
    /// </summary>
    ReadSector = 0x04,

    /// <summary>
    /// Write Sector: 5.
    /// <para>写入扇区：5。</para>
    /// </summary>
    WriteSector = 0x05,

    /// <summary>
    /// Erase Normal: 6.
    /// <para>正常擦除：6。</para>
    /// </summary>
    EraseNormal = 0x06,

    /// <summary>
    /// Read LBA: 0x14.
    /// <para>读取 LBA：0x14。</para>
    /// </summary>
    ReadLba = 0x14,

    /// <summary>
    /// Write LBA: 0x15.
    /// <para>写入 LBA：0x15。</para>
    /// </summary>
    WriteLba = 0x15,

    /// <summary>
    /// Erase System Disk: 0x16.
    /// <para>擦除系统盘：0x16。</para>
    /// </summary>
    EraseSystemDisk = 0x16,

    /// <summary>
    /// Read SDRAM: 0x17.
    /// <para>读取 SDRAM：0x17。</para>
    /// </summary>
    ReadSdram = 0x17,

    /// <summary>
    /// Write SDRAM: 0x18.
    /// <para>写入 SDRAM：0x18。</para>
    /// </summary>
    WriteSdram = 0x18,

    /// <summary>
    /// Execute SDRAM: 0x19.
    /// <para>执行 SDRAM：0x19。</para>
    /// </summary>
    ExecuteSdram = 0x19,

    /// <summary>
    /// Read Flash Info: 0x1A.
    /// <para>读取 Flash 信息：0x1A。</para>
    /// </summary>
    ReadFlashInfo = 0x1A,

    /// <summary>
    /// Read Chip Info: 0x1B.
    /// <para>读取芯片信息：0x1B。</para>
    /// </summary>
    ReadChipInfo = 0x1B,

    /// <summary>
    /// Set Reset Flag: 0x1E.
    /// <para>设置复位标志：0x1E。</para>
    /// </summary>
    SetResetFlag = 0x1E,

    /// <summary>
    /// Write Efuse: 0x1F.
    /// <para>写入 Efuse：0x1F。</para>
    /// </summary>
    WriteEfuse = 0x1F,

    /// <summary>
    /// Read Efuse: 0x20.
    /// <para>读取 Efuse：0x20。</para>
    /// </summary>
    ReadEfuse = 0x20,

    /// <summary>
    /// Read SPI Flash: 0x21.
    /// <para>读取 SPI Flash：0x21。</para>
    /// </summary>
    ReadSpiFlash = 0x21,

    /// <summary>
    /// Write SPI Flash: 0x22.
    /// <para>写入 SPI Flash：0x22。</para>
    /// </summary>
    WriteSpiFlash = 0x22,

    /// <summary>
    /// Erase LBA: 0x25.
    /// <para>擦除 LBA：0x25。</para>
    /// </summary>
    EraseLba = 0x25,

    /// <summary>
    /// Change Storage: 0x2A.
    /// <para>切换存储介质：0x2A。</para>
    /// </summary>
    ChangeStorage = 0x2A,

    /// <summary>
    /// Read Storage: 0x2B.
    /// <para>读取存储介质：0x2B。</para>
    /// </summary>
    ReadStorage = 0x2B,

    /// <summary>
    /// Read Capability: 0xAA.
    /// <para>读取能力：0xAA。</para>
    /// </summary>
    ReadCapability = 0xAA,

    /// <summary>
    /// Device Reset: 0xFF.
    /// <para>设备复位：0xFF。</para>
    /// </summary>
    DeviceReset = 0xFF,
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
    /// Gets the ASCII chip tag returned by READ_CHIP_INFO (first four bytes of the
    /// 16-byte response, e.g. "320A").
    /// <para>获取 READ_CHIP_INFO 返回的 ASCII 芯片标签（16 字节响应的前 4 字节，如 "320A"）。</para>
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Returns the chip name for a given chip ID (a USB-Style numeric ID or an ASCII tag
    /// value read directly from the chip-info response).
    /// <para>返回给定芯片 ID 的芯片名称（USB 风格数字 ID，或直接从芯片信息响应读取的 ASCII 标签值）。</para>
    /// </summary>
    /// <param name="chipId">The chip ID. 芯片 ID。</param>
    /// <returns>The chip name. 芯片名称。</returns>
    public static string GetChipName(uint chipId)
    {
        return chipId switch
        {
            // ASCII tags from the READ_CHIP_INFO response (upstream CRKDevice::CheckChip).
            0x33323041 or 0x41303233 => "RK32",   // "320A"
            0x33303041 or 0x41303033 => "RK30",   // "300A"
            0x33313041 or 0x41303133 => "RK31",   // "310A"
            0x33313042 or 0x42303133 => "RK31B",  // "310B"
            0x32393058 or 0x58303932 => "RK29",   // "290X"
            0x6E616E6F or 0x6F6E616E => "Nano",   // "nano"
            // USB product IDs (loader mode, 99-rk-rockusb.rules).
            0x301A => "RK3036",
            0x310C => "RK3128",
            0x320A => "RK3288",
            0x320B => "RK3229",
            0x320C => "RK3328",
            0x330A => "RK3368",
            0x330C => "RK3399",
            0x350A => "RK3566",
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
    /// <param name="errorCode">The RKDL error code. RKDL 错误码。</param>
    /// <param name="message">The error message. 错误信息。</param>
    public RkdlException(RkdlError errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RkdlException"/> class.
    /// <para>初始化 <see cref="RkdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The RKDL error code. RKDL 错误码。</param>
    /// <param name="message">The error message. 错误信息。</param>
    /// <param name="innerException">The inner exception. 内部异常。</param>
    public RkdlException(RkdlError errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}