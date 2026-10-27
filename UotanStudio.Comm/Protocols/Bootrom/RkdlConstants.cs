using System.Buffers.Binary;

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
    /// RKDL download boot vendor request (BRequest = 0x0C), paired with WIndex 0x0471 or
    /// 0x0472 to download the first- or second-stage loader (BmRequestType 0x40, WValue 0).
    /// <para>RKDL 下载启动 vendor 请求（BRequest = 0x0C），与 WIndex 0x0471 或 0x0472
    /// 配合以下载第一或第二阶段 loader（BmRequestType 0x40，WValue 0）。</para>
    /// </summary>
    public const byte RkdlRequestDownloadBoot = 0x0C;

    /// <summary>
    /// RKDL vendor request that downloads the 471 (first-stage) loader, an alias of
    /// <see cref="RkdlRequestDownloadBoot"/> whose stage is chosen by the WIndex field.
    /// <para>下载 471（第一阶段）loader 的 RKDL 供应商请求，是
    /// <see cref="RkdlRequestDownloadBoot"/> 的别名，阶段由 WIndex 字段决定。</para>
    /// </summary>
    public const byte RkdlRequest471 = RkdlRequestDownloadBoot;

    /// <summary>
    /// RKDL vendor request that downloads the 472 (second-stage) loader, an alias of
    /// <see cref="RkdlRequestDownloadBoot"/> whose stage is chosen by the WIndex field.
    /// <para>下载 472（第二阶段）loader 的 RKDL 供应商请求，是
    /// <see cref="RkdlRequestDownloadBoot"/> 的别名，阶段由 WIndex 字段决定。</para>
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
    /// Sector size in bytes that the rockusb LBA protocol addresses by, so the logical
    /// block addresses and sector counts of the LBA commands are counted in these units.
    /// <para>rockusb LBA 协议据以寻址的扇区大小（字节），LBA 命令的逻辑块地址与扇区
    /// 计数都按该单位计算。</para>
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
    /// Physical sector size on the wire for WRITE_SECTOR: 512 data bytes plus 16 spare bytes, as
    /// <c>RKU_WriteSector</c> uses.
    /// <para>WRITE_SECTOR 的线上物理扇区大小：512 数据字节加 16 备用字节，与 <c>RKU_WriteSector</c> 一致。</para>
    /// </summary>
    public const int SectorWithSpareSize = 528;

    /// <summary>
    /// Maximum number of 528-byte sectors one WRITE_SECTOR command may carry, the
    /// <c>RKU_WriteSector</c> cross-border limit.
    /// <para>一条 WRITE_SECTOR 命令最多携带的 528 字节扇区数，即 <c>RKU_WriteSector</c> 的越界上限。</para>
    /// </summary>
    public const int MaxWriteSectorsPerCommand = 32;

    /// <summary>
    /// Sector count one ERASE_LBA command is asked to erase by the whole-media erase loop, matching
    /// the <c>1024 * 32</c> chunk of <c>CRKDevice::EraseEmmc</c>.
    /// <para>整介质擦除循环每条 ERASE_LBA 命令擦除的扇区数，与 <c>CRKDevice::EraseEmmc</c> 的
    /// <c>1024 * 32</c> 分块一致。</para>
    /// </summary>
    public const int EraseSectorChunk = 1024 * 32;

    /// <summary>
    /// Length of the leading <c>STRUCT_FLASHINFO_CMD</c> bytes READ_FLASH_INFO transfers: flash size,
    /// block size, page size, ECC bits, access time, manufacturer code and flash chip select.
    /// <para>READ_FLASH_INFO 传输的 <c>STRUCT_FLASHINFO_CMD</c> 首部长度：flash 容量、块大小、
    /// 页大小、ECC 位数、访问时间、厂商代码与 flash 片选。</para>
    /// </summary>
    public const int FlashInfoStructureLength = 11;

    /// <summary>
    /// Upper bound of the READ_FLASH_INFO data phase; the device may answer with a longer
    /// structure, which is read up to this size.
    /// <para>READ_FLASH_INFO 数据阶段的上界；设备可能返回更长的结构，最多读取该长度。</para>
    /// </summary>
    public const int MaxFlashInfoLength = 512;

    /// <summary>
    /// READ_FLASH_ID value that marks an eMMC medium. Read as little-endian bytes the constant
    /// spells "EMMC", which is how <c>CRKDevice::GetFlashInfo</c> recognizes an eMMC.
    /// <para>标识 eMMC 介质的 READ_FLASH_ID 取值。按小端字节读取时该常量拼作 "EMMC"，
    /// 这正是 <c>CRKDevice::GetFlashInfo</c> 识别 eMMC 的方式。</para>
    /// </summary>
    public const uint EmmcFlashId = 0x434D4D45;

    /// <summary>
    /// Upper bound, in milliseconds, accepted for the <c>dwDataDelay</c> field of an
    /// RKBOOT loader entry, so a crafted file cannot stall the download or wrap negative
    /// as a signed wait.
    /// <para>RKBOOT loader 条目 <c>dwDataDelay</c> 字段可接受的毫秒上界，避免伪造文件
    /// 让下载停滞或转成有符号等待时回绕为负。</para>
    /// </summary>
    public const int MaxLoaderDataDelayMs = 60000;

    /// <summary>
    /// Settle window, in milliseconds, waited after the last loader entry is transferred.
    /// The device re-enumerates on a different USB product ID during this window.
    /// <para>传出最后一个 loader 条目后等待的稳定窗口（毫秒）。设备在该窗口内会以不同的
    /// USB 产品 ID 重新枚举。</para>
    /// </summary>
    public const int LoaderReenumerationSettleMs = 1000;

    /// <summary>
    /// Default budget, in milliseconds, granted for observing the replacement endpoint
    /// after a loader download. It is the default <c>Timeout</c> value of the offer type.
    /// <para>观测 loader 下载后替代端点的默认毫秒预算，即该 offer 类型的默认
    /// <c>Timeout</c> 取值。</para>
    /// </summary>
    public const int LoaderRelinkTimeoutMs = 30000;

    /// <summary>
    /// Product IDs of devices already in rockusb (loader) mode, paired with the RKDL chip-ID
    /// mapping. Other PIDs under the Rockchip VID that satisfy the generic bootrom rule are treated
    /// as maskrom or bootrom devices.
    /// <para>已处于 rockusb（loader）模式的设备产品 ID，并与 RKDL 芯片 ID 映射成对使用。Rockchip VID 下
    /// 满足通用 bootrom 规则的其他 PID 按 maskrom 或 bootrom 设备处理。</para>
    /// </summary>
    /// <remarks>
    /// Boundary: maskrom and loader differ in bit 0 of the USB <c>bcdUSB</c> field, but
    /// <c>UsbDeviceInfo</c> carries no <c>bcdUSB</c> field and this repository does not widen the USB
    /// abstraction for it. Classification is therefore PID-only: a loader PID listed here is opened in
    /// loader mode, an unlisted loader PID is treated as maskrom (so its loader download is refused),
    /// and a maskrom device sharing a listed PID is treated as loader (so its first CBW transaction
    /// fails instead of receiving a loader). Real-hardware confirmation is required for any PID not
    /// listed here.
    /// <para>边界：maskrom 与 loader 可由 <c>bcdUSB</c> 的 bit 0 区分，但 <c>UsbDeviceInfo</c>
    /// 没有 <c>bcdUSB</c> 字段，本仓库也不为此扩展 USB 抽象层，因此这里只按 PID 判定：列入本表的
    /// loader PID 按 loader 模式打开；未列出的 loader PID 会被当作 maskrom，其 loader 下载请求会被
    /// 拒绝；与表内 PID 相同的 maskrom 设备会被当作 loader，其首个 CBW 事务会失败而不会收到 loader。
    /// 本表之外的 PID 需要真机确认。</para>
    /// </remarks>
    public static readonly ushort[] KnownLoaderProductIds =
    [
        0x281A,
        0x290A,
        0x292A,
        0x300A,
        0x300B,
        0x301A,
        0x310A,
        0x310B,
        0x310C,
        0x320A,
        0x320B,
        0x320C,
        0x330A,
        0x330B,
        0x330C,
        0x350A,
        0x350B,
    ];
}

/// <summary>
/// TEST_UNIT_READY sub codes carried in the CBWCB.ucReserved field.
/// <para>存于 CBWCB.ucReserved 字段的 TEST_UNIT_READY 子码。</para>
/// </summary>
internal static class RkdlTestUnitSubCode
{
    /// <summary>Erase the system disk. <para>擦除系统盘。</para></summary>
    public const byte EraseSystem = 0xFE;

    /// <summary>Lower-format the storage. <para>低级格式化存储。</para></summary>
    public const byte LowerFormat = 0xFD;

    /// <summary>Erase user data. <para>擦除用户数据。</para></summary>
    public const byte EraseUserData = 0xFB;

    /// <summary>Query the user-data sector map. <para>查询用户数据扇区映射。</para></summary>
    public const byte GetUserSector = 0xF9;
}

/// <summary>
/// READ_LBA and WRITE_LBA access method sub codes (RWMETHOD).
/// <para>READ_LBA 与 WRITE_LBA 的访问方式子码（RWMETHOD）。</para>
/// </summary>
internal static class RkdlRwMethod
{
    /// <summary>Address by partition image offset. <para>按分区镜像偏移寻址。</para></summary>
    public const byte Image = 0;

    /// <summary>Address by logical block address. <para>按逻辑块地址寻址。</para></summary>
    public const byte Lba = 1;
}

/// <summary>
/// Rockchip rockusb operation codes (USB_OPERATION_CODE).
/// <para>Rockchip rockusb 的操作码（USB_OPERATION_CODE）。</para>
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
    /// Erase Force: 0x0B, which ignores bad-block markers.
    /// <para>强制擦除：0x0B，忽略坏块标记。</para>
    /// </summary>
    EraseForce = 0x0B,

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
    /// <value>The chip ID. <para>芯片 ID。</para></value>
    public uint ChipId { get; set; }

    /// <summary>
    /// Gets or sets the chip revision.
    /// <para>获取或设置芯片版本。</para>
    /// </summary>
    /// <value>The chip revision. <para>芯片版本。</para></value>
    public uint Revision { get; set; }

    /// <summary>
    /// Gets the human-readable chip name.
    /// <para>获取人类可读的芯片名称。</para>
    /// </summary>
    /// <value>The name derived from <see cref="ChipId"/>. <para>由 <see cref="ChipId"/> 得出的名称。</para></value>
    public string ChipName => GetChipName(ChipId);

    /// <summary>
    /// Gets the ASCII chip tag returned by READ_CHIP_INFO: the first four bytes of the
    /// 16-byte response, such as "320A".
    /// <para>获取 READ_CHIP_INFO 返回的 ASCII 芯片标签：16 字节响应的前 4 字节，如 "320A"。</para>
    /// </summary>
    /// <value>The four-character ASCII tag. <para>四字符 ASCII 标签。</para></value>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Returns the chip name for a given chip ID (a USB-Style numeric ID or an ASCII tag
    /// value read directly from the chip-info response).
    /// <para>返回给定芯片 ID 的芯片名称（USB 风格数字 ID，或直接从芯片信息响应读取的 ASCII 标签值）。</para>
    /// </summary>
    /// <param name="chipId">The chip ID. <para>芯片 ID。</para></param>
    /// <returns>The chip name. <para>芯片名称。</para></returns>
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
/// RKDL flash info as reported by READ_FLASH_INFO, decoded from the packed
/// <c>STRUCT_FLASHINFO_CMD</c> the device returns.
/// <para>READ_FLASH_INFO 报告的 RKDL 闪存信息，由设备返回的紧凑 <c>STRUCT_FLASHINFO_CMD</c> 解码而来。</para>
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
    /// <value>The verbatim response bytes, at least 11 long. <para>逐字节的响应，至少 11 字节。</para></value>
    public required byte[] RawResponse { get; init; }

    /// <summary>
    /// Gets the sector size the rockusb LBA protocol addresses by.
    /// <para>获取 rockusb LBA 协议据以寻址的扇区大小。</para>
    /// </summary>
    /// <value>Always <see cref="RkdlConstants.SectorSize"/> (512), the protocol-defined
    /// LBA granularity. <para>恒为 <see cref="RkdlConstants.SectorSize"/>（512），即协议
    /// 规定的 LBA 粒度。</para></value>
    public int SectorSize => RkdlConstants.SectorSize;

    /// <summary>
    /// Gets the total flash size in bytes implied by the device-reported sector count.
    /// <para>获取由设备报告扇区数推得的闪存总字节数。</para>
    /// </summary>
    /// <value><see cref="SectorCount"/> scaled by the protocol LBA granularity
    /// <see cref="SectorSize"/>. <para><see cref="SectorCount"/> 按协议 LBA 粒度
    /// <see cref="SectorSize"/> 缩放的结果。</para></value>
    public long Size => SectorCount * SectorSize;

    /// <summary>
    /// Gets the erase block size in 512-byte sectors (<c>usBlockSize</c>).
    /// <para>获取以 512 字节扇区计的擦除块大小（<c>usBlockSize</c>）。</para>
    /// </summary>
    /// <value>The device-reported block size in sectors; <c>0</c> means the device did not report
    /// usable block geometry. <para>设备报告的块大小（扇区）；<c>0</c> 表示设备未报告可用的块几何。</para></value>
    public ushort BlockSizeSectors { get; init; }

    /// <summary>
    /// Gets the NAND page size in 512-byte sectors (<c>bPageSize</c>).
    /// <para>获取以 512 字节扇区计的 NAND 页大小（<c>bPageSize</c>）。</para>
    /// </summary>
    /// <value>The device-reported page size in sectors; <c>0</c> for media that has no NAND page.
    /// <para>设备报告的页大小（扇区）；没有 NAND 页的介质为 <c>0</c>。</para></value>
    public byte PageSizeSectors { get; init; }

    /// <summary>
    /// Gets the ECC bit count per sector (<c>bECCBits</c>).
    /// <para>获取每扇区的 ECC 位数（<c>bECCBits</c>）。</para>
    /// </summary>
    /// <value>The device-reported ECC strength. <para>设备报告的 ECC 强度。</para></value>
    public byte EccBits { get; init; }

    /// <summary>
    /// Gets the access time reported by the device (<c>bAccessTime</c>).
    /// <para>获取设备报告的访问时间（<c>bAccessTime</c>）。</para>
    /// </summary>
    /// <value>The device-reported access time. <para>设备报告的访问时间。</para></value>
    public byte AccessTime { get; init; }

    /// <summary>
    /// Gets the flash manufacturer code (<c>bManufCode</c>).
    /// <para>获取 flash 厂商代码（<c>bManufCode</c>）。</para>
    /// </summary>
    /// <value>The manufacturer index decoded by <see cref="ManufacturerName"/>.
    /// <para>由 <see cref="ManufacturerName"/> 解码的厂商下标。</para></value>
    public byte ManufacturerCode { get; init; }

    /// <summary>
    /// Gets the flash chip-select bitmask (<c>bFlashCS</c>); bit <c>n</c> set means chip <c>n</c>
    /// is populated.
    /// <para>获取 flash 片选位掩码（<c>bFlashCS</c>）；第 <c>n</c> 位置位表示芯片 <c>n</c> 在位。</para>
    /// </summary>
    /// <value>The chip-select bitmask. <para>片选位掩码。</para></value>
    public byte FlashCs { get; init; }

    /// <summary>
    /// Gets the manufacturer name for <see cref="ManufacturerCode"/>, or <c>UNKNOWN</c> above the
    /// eight indices the protocol names.
    /// <para>获取 <see cref="ManufacturerCode"/> 对应的厂商名称；超过协议命名的八个下标时为 <c>UNKNOWN</c>。</para>
    /// </summary>
    /// <value>The manufacturer name. <para>厂商名称。</para></value>
    public string ManufacturerName => ManufacturerCode switch
    {
        0 => "SAMSUNG",
        1 => "TOSHIBA",
        2 => "HYNIX",
        3 => "INFINEON",
        4 => "MICRON",
        5 => "RENESAS",
        6 => "ST",
        7 => "INTEL",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// Gets the erase block size in bytes, or <c>0</c> when the device reported no block geometry.
    /// <para>获取以字节计的擦除块大小；设备未报告块几何时为 <c>0</c>。</para>
    /// </summary>
    /// <value><see cref="BlockSizeSectors"/> scaled by <see cref="SectorSize"/>.
    /// <para><see cref="BlockSizeSectors"/> 按 <see cref="SectorSize"/> 缩放的结果。</para></value>
    public long BlockSizeBytes => BlockSizeSectors * (long)SectorSize;

    /// <summary>
    /// Gets the number of erase blocks the media holds, or <c>0</c> when the block size is unusable.
    /// <para>获取介质包含的擦除块数；块大小不可用时为 <c>0</c>。</para>
    /// </summary>
    /// <value><see cref="SectorCount"/> divided by <see cref="BlockSizeSectors"/>.
    /// <para><see cref="SectorCount"/> 除以 <see cref="BlockSizeSectors"/> 的结果。</para></value>
    public long BlockCount => BlockSizeSectors == 0 ? 0 : SectorCount / BlockSizeSectors;

    /// <summary>
    /// Gets the zero-based indices of the populated flash chips decoded from <see cref="FlashCs"/>.
    /// <para>获取由 <see cref="FlashCs"/> 解码出的在位 flash 芯片从零开始的下标。</para>
    /// </summary>
    /// <value>One entry per set bit, lowest index first; empty when the device reported no chip.
    /// <para>每个置位一位的条目，下标由低到高；设备未报告芯片时为空。</para></value>
    public IReadOnlyList<int> FlashChips
    {
        get
        {
            var chips = new List<int>(8);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((FlashCs & (1 << bit)) != 0)
                {
                    chips.Add(bit);
                }
            }

            return chips;
        }
    }

    /// <summary>
    /// Decodes a READ_FLASH_INFO data phase into the packed <c>STRUCT_FLASHINFO_CMD</c> fields.
    /// <para>将 READ_FLASH_INFO 数据阶段解码为紧凑 <c>STRUCT_FLASHINFO_CMD</c> 字段。</para>
    /// </summary>
    /// <param name="response">The flashed info bytes; at least <see cref="RkdlConstants.FlashInfoStructureLength"/> long.
    /// <para>flash 信息字节；至少 <see cref="RkdlConstants.FlashInfoStructureLength"/> 长。</para></param>
    /// <returns>The decoded flash info, with the verbatim response preserved.
    /// <para>解码出的 flash 信息，并保留逐字节原始响应。</para></returns>
    /// <exception cref="RkdlException">The response is shorter than the 11-byte structure prefix.
    /// <para>响应短于 11 字节结构前缀。</para></exception>
    public static RkFlashInfo Decode(ReadOnlySpan<byte> response)
    {
        if (response.Length < RkdlConstants.FlashInfoStructureLength)
        {
            throw new RkdlException(RkdlError.FlashInfoFailed,
                $"READ_FLASH_INFO answered {response.Length} bytes, fewer than the {RkdlConstants.FlashInfoStructureLength}-byte structure prefix.");
        }

        return new RkFlashInfo
        {
            SectorCount = BinaryPrimitives.ReadUInt32LittleEndian(response),
            BlockSizeSectors = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(4, 2)),
            PageSizeSectors = response[6],
            EccBits = response[7],
            AccessTime = response[8],
            ManufacturerCode = response[9],
            FlashCs = response[10],
            RawResponse = response.ToArray(),
        };
    }
}

/// <summary>
/// Device capability word returned by READ_CAPABILITY.
/// <para>READ_CAPABILITY 返回的设备能力字。</para>
/// </summary>
/// <remarks>
/// Only the two bits <c>CRKDevice::ReadCapability</c> consumes are decoded: bit 0 selects
/// direct-LBA addressing and bit 2 marks first-4M access. The remaining bits stay verbatim in
/// <see cref="Raw"/> and are not interpreted.
/// <para>只解码 <c>CRKDevice::ReadCapability</c> 使用的两个位：位 0 表示直接 LBA 寻址，位 2 表示
/// 前 4M 访问。其余位原样保留在 <see cref="Raw"/> 中且不作解释。</para>
/// </remarks>
public sealed class RkCapability
{
    private RkCapability(byte[] raw) => Raw = raw;

    /// <summary>
    /// Gets the capability bytes exactly as the device returned them.
    /// <para>获取设备原样返回的能力字节。</para>
    /// </summary>
    /// <value>The verbatim response, at least eight bytes. <para>逐字节响应，至少 8 字节。</para></value>
    public byte[] Raw { get; }

    /// <summary>
    /// Gets whether the device accepts direct-LBA addressing (bit 0 of the first capability byte),
    /// which lets the whole media be erased through ERASE_LBA instead of per-block erase commands.
    /// <para>获取设备是否接受直接 LBA 寻址（首字节位 0），从而可以通过 ERASE_LBA 而非逐块擦除命令擦除整个介质。</para>
    /// </summary>
    /// <value><c>true</c> when direct LBA is supported. <para>支持直接 LBA 时为 <c>true</c>。</para></value>
    public bool DirectLba => (Raw[0] & 0x01) != 0;

    /// <summary>
    /// Gets whether the device exposes first-4M access (bit 2 of the first capability byte).
    /// <para>获取设备是否提供前 4M 访问（首字节位 2）。</para>
    /// </summary>
    /// <value><c>true</c> when first-4M access is supported. <para>支持前 4M 访问时为 <c>true</c>。</para></value>
    public bool First4MAccess => (Raw[0] & 0x04) != 0;

    /// <summary>
    /// Decodes the eight-byte READ_CAPABILITY data phase.
    /// <para>解码 8 字节 READ_CAPABILITY 数据阶段。</para>
    /// </summary>
    /// <param name="response">The capability bytes; at least eight long. <para>能力字节；至少 8 字节。</para></param>
    /// <returns>The decoded capability word with the verbatim bytes preserved.
    /// <para>解码后的能力字，并保留逐字节原始内容。</para></returns>
    /// <exception cref="RkdlException">The response is shorter than eight bytes.
    /// <para>响应短于 8 字节。</para></exception>
    public static RkCapability Decode(ReadOnlySpan<byte> response)
    {
        if (response.Length < 8)
        {
            throw new RkdlException(RkdlError.ReadFailed,
                $"READ_CAPABILITY answered {response.Length} bytes, fewer than the 8 bytes the protocol transfers.");
        }

        return new RkCapability(response.ToArray());
    }
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
    /// <value>The error code that describes the failure. <para>描述该失败的错误码。</para></value>
    public RkdlError ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RkdlException"/> class.
    /// <para>初始化 <see cref="RkdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The RKDL error code. <para>RKDL 错误码。</para></param>
    /// <param name="message">The error message. <para>错误信息。</para></param>
    public RkdlException(RkdlError errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RkdlException"/> class.
    /// <para>初始化 <see cref="RkdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The RKDL error code. <para>RKDL 错误码。</para></param>
    /// <param name="message">The error message. <para>错误信息。</para></param>
    /// <param name="innerException">The inner exception. <para>内部异常。</para></param>
    public RkdlException(RkdlError errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}