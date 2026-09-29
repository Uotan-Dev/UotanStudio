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
    /// Sector size in bytes the rockusb LBA protocol addresses by. This is a protocol constant, not
    /// a device-reported value: <c>READ_LBA</c>/<c>WRITE_LBA</c>/<c>ERASE_LBA</c> carry a
    /// <c>cbwcb.dwAddress</c> logical block address and a 16-bit <c>cbwcb.usLength</c> sector count
    /// that upstream rkdeveloptool also computes in 512-byte units. The erase/write granularity a
    /// device reports (upstream <c>usBlockSize</c>/<c>usPageSize</c>) is a property of the media, not
    /// of LBA addressing, so no LBA arithmetic in this protocol may be scaled by it.
    /// <para>rockusb LBA 协议据以寻址的扇区大小（字节）。这是协议常量而非设备报告值：
    /// <c>READ_LBA</c>/<c>WRITE_LBA</c>/<c>ERASE_LBA</c> 携带 <c>cbwcb.dwAddress</c> 逻辑块地址与
    /// 16 位 <c>cbwcb.usLength</c> 扇区计数，上游 rkdeveloptool 同样按 512 字节单位计算。设备报告的
    /// 擦写粒度（上游 <c>usBlockSize</c>/<c>usPageSize</c>）属于介质属性而非 LBA 寻址属性，
    /// 因此本协议的任何 LBA 算术都不得按它缩放。</para>
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
    /// Upper bound, in milliseconds, accepted for the <c>dwDataDelay</c> field of an RKBOOT loader
    /// entry. The field is read from a loader file, which is untrusted input, and upstream entry
    /// tables use it as a short settle window between loader stages; the bound exists so a crafted
    /// file cannot pin the download path for days (a raw <c>uint</c> would allow roughly 24.8 days)
    /// and cannot wrap negative when converted to a signed wait.
    /// <para>RKBOOT loader 条目 <c>dwDataDelay</c> 字段可接受的毫秒上界。该字段读自 loader 文件，
    /// 属于不受信输入；上游条目表把它用作 loader 阶段之间的短暂稳定窗口。设置上界是为了让伪造文件
    /// 既无法把下载路径挂住数天（裸 <c>uint</c> 允许约 24.8 天），也无法在转换为有符号等待值时回绕为负。</para>
    /// </summary>
    public const int MaxLoaderDataDelayMs = 60000;

    /// <summary>
    /// The settle window, in milliseconds, waited after the last loader entry was transferred. The
    /// device re-enumerates on a different USB product ID during this window; the wait is a settle
    /// delay only and its completion is not evidence that the device reached loader mode, because
    /// <see cref="RkdlSession.IsRockusbMode"/> is derived from a device-info snapshot taken when the
    /// session was opened and can never observe the change.
    /// <para>传出最后一个 loader 条目后等待的稳定窗口（毫秒）。设备在该窗口内会以不同的 USB 产品 ID
    /// 重新枚举；这段等待仅是稳定延时，其结束并不能证明设备已进入 loader 模式，因为
    /// <see cref="RkdlSession.IsRockusbMode"/> 派生自会话打开时的设备信息快照，永远观测不到该变化。</para>
    /// </summary>
    public const int LoaderReenumerationSettleMs = 1000;

    /// <summary>
    /// Default budget, in milliseconds, a caller should grant the device layer to observe the
    /// replacement endpoint after a loader download. It matches the controller bound of
    /// 1 millisecond through 24 hours.
    /// <para>调用方应授予设备层去观测 loader 下载后替代端点的默认预算（毫秒）。它符合控制器
    /// 1 毫秒至 24 小时的约束。</para>
    /// </summary>
    public const int LoaderRelinkTimeoutMs = 30000;

    /// <summary>
    /// Product IDs of devices already in rockusb (loader) mode, from the upstream
    /// 99-rk-rockusb.rules. Devices with other PIDs under the Rockchip VID that satisfy the
    /// generic rule (pid >> 8 != 0) are treated as maskrom/bootrom devices.
    /// <para>已处于 rockusb（loader）模式的设备产品 ID，来自上游 99-rk-rockusb.rules。
    /// Rockchip VID 下满足通用规则（pid >> 8 != 0）的其他 PID 设备按 maskrom/bootrom 处理。</para>
    /// </summary>
    public static readonly ushort[] KnownLoaderProductIds =
    [
        0x301A,
        0x310C,
        0x320A,
        0x320B,
        0x320C,
        0x330A,
        0x330C,
        0x350A,
    ];
}

/// <summary>
/// TEST_UNIT_READY sub codes (used in CBWCB.ucReserved, see rkdeveloptool RKComm.h).
/// <para>TEST_UNIT_READY 子码（存于 CBWCB.ucReserved，见 rkdeveloptool 的 RKComm.h）。</para>
/// </summary>
internal static class RkdlTestUnitSubCode
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
internal static class RkdlRwMethod
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
internal enum RkdlCommand : byte
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
internal class RkChipInfo
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
            0x33323041 or 0x41303233 => "RK32",
            0x33303041 or 0x41303033 => "RK30",
            0x33313041 or 0x41303133 => "RK31",
            0x33313042 or 0x42303133 => "RK31B",
            0x32393058 or 0x58303932 => "RK29",
            0x6E616E6F or 0x6F6E616E => "Nano",
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
/// RKDL flash info as reported by READ_FLASH_INFO. Only the leading DWORD of the response is
/// parsed, because it is the only field whose offset this repository can verify; the remaining
/// bytes of the packed upstream <c>STRUCT_FLASHINFO_CMD</c> (media geometry such as block and page
/// size) are kept verbatim in <see cref="RawResponse"/> instead of being decoded from guessed
/// offsets.
/// <para>READ_FLASH_INFO 报告的 RKDL 闪存信息。只解析响应的首个 DWORD，因为它是本仓库唯一能核对偏移
/// 的字段；上游紧凑 <c>STRUCT_FLASHINFO_CMD</c> 的其余字节（块大小、页大小等介质几何）原样保留在
/// <see cref="RawResponse"/> 中，而不按猜测的偏移解码。</para>
/// </summary>
internal sealed class RkFlashInfo
{
    /// <summary>
    /// Gets the total sector count reported by the device, widened from the leading little-endian
    /// DWORD of the response.
    /// <para>获取设备报告的总扇区数，由响应首个小端 DWORD 加宽而来。</para>
    /// </summary>
    /// <value>The device-reported count in <see cref="SectorSize"/>-byte sectors; <c>0</c> means the
    /// device reported no usable geometry. <para>以 <see cref="SectorSize"/> 字节扇区计的设备报告值；
    /// <c>0</c> 表示设备未报告可用几何。</para></value>
    public required long SectorCount { get; init; }

    /// <summary>
    /// Gets the complete READ_FLASH_INFO data phase exactly as the device returned it.
    /// <para>获取设备原样返回的完整 READ_FLASH_INFO 数据阶段。</para>
    /// </summary>
    /// <value>The verbatim response bytes, at least four long; unparsed fields beyond the leading
    /// DWORD are present but undecoded. <para>逐字节的响应，至少 4 字节；首个 DWORD 之后的未解析字段
    /// 存在但未解码。</para></value>
    public required byte[] RawResponse { get; init; }

    /// <summary>
    /// Gets the sector size the rockusb LBA protocol addresses by.
    /// <para>获取 rockusb LBA 协议据以寻址的扇区大小。</para>
    /// </summary>
    /// <value>Always <see cref="RkdlConstants.SectorSize"/> (512). This is the protocol-defined LBA
    /// granularity and not a device-reported value: the media block or page size a device may report
    /// in the unparsed tail of the response describes erase/write granularity, so it must never be
    /// substituted for LBA addressing.
    /// <para>恒为 <see cref="RkdlConstants.SectorSize"/>（512）。这是协议规定的 LBA 粒度而非设备报告值：
    /// 设备可能在响应未解析尾部报告的介质块大小或页大小描述的是擦写粒度，因此绝不能用它替代 LBA 寻址。</para></value>
    public int SectorSize => RkdlConstants.SectorSize;

    /// <summary>
    /// Gets the total flash size in bytes implied by the device-reported sector count.
    /// <para>获取由设备报告扇区数推得的闪存总字节数。</para>
    /// </summary>
    /// <value><see cref="SectorCount"/> scaled by the protocol LBA granularity
    /// <see cref="SectorSize"/>. <para><see cref="SectorCount"/> 按协议 LBA 粒度
    /// <see cref="SectorSize"/> 缩放的结果。</para></value>
    public long Size => SectorCount * SectorSize;
}

/// <summary>
/// RKDL error codes.
/// <para>RKDL 错误码。</para>
/// </summary>
internal enum RkdlError
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
internal class RkdlException : Exception
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