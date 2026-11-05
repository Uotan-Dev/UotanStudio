namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Vendor and wire constants of the Allwinner FEL/FES (eFEX) protocol.
/// <para>全志 FEL/FES（eFEX）协议的厂商与线上常量。</para>
/// </summary>
internal static class SunxiEfexConstants
{
    /// <summary>Allwinner USB vendor id. <para>全志 USB 厂商 id。</para></summary>
    public const ushort VendorId = 0x1F3A;

    /// <summary>FEL and FES product id; both modes enumerate with this pair.
    /// <para>FEL 与 FES 的产品 id；两种模式都以该组合枚举。</para></summary>
    public const ushort ProductId = 0xEFE8;

    /// <summary>Bulk header magic that starts every host request. <para>每个主机请求起始处的批量头魔数。</para></summary>
    public static ReadOnlySpan<byte> RequestMagic => "AWUC"u8;

    /// <summary>Bulk response magic the device returns after each request. <para>设备在每个请求后返回的批量响应魔数。</para></summary>
    public static ReadOnlySpan<byte> ResponseMagic => "AWUS"u8;

    /// <summary>Eight-byte magic the boot ROM returns at the start of the identity payload; the SoC id follows it.
    /// <para>boot ROM 在身份负载起始处返回的 8 字节魔数；随后是 SoC id。</para></summary>
    public static ReadOnlySpan<byte> IdentityMagic => "AWUSBFEX"u8;

    /// <summary>Size of the bulk request header. <para>批量请求头的大小。</para></summary>
    public const int RequestHeaderSize = 32;

    /// <summary>Size of the bulk response. <para>批量响应的大小。</para></summary>
    public const int ResponseSize = 13;

    /// <summary>Size of the inner FEL request. <para>内层 FEL 请求的大小。</para></summary>
    public const int FelRequestSize = 16;

    /// <summary>Size of the FES transfer header. <para>FES 传输头的大小。</para></summary>
    public const int FesTransferSize = 20;

    /// <summary>Size of the FEL status payload. <para>FEL 状态负载的大小。</para></summary>
    public const int StatusSize = 8;

    /// <summary>Size of the identity payload. <para>身份负载的大小。</para></summary>
    public const int IdentitySize = 32;

    /// <summary>Largest payload one FEL or FES transfer carries. <para>单次 FEL 或 FES 传输承载的最大负载。</para></summary>
    public const int MaximumTransfer = 64 * 1024;

    /// <summary>Command-package length written into the bulk request header as <c>0x0C</c>, the
    /// payload size of one inner request. <para>写入批量请求头 <c>0x0C</c> 位置的值，即一条内层请求的负载长度。</para></summary>
    public const byte CommandLength = 0x0C;

    /// <summary>Flag that marks the last chunk of one FES download. <para>标记一次 FES 下载最后一个分块的标志。</para></summary>
    public const uint TransferFinishTag = 0x10000;

    /// <summary>Flag the FES service sets in a verification reply once both CRC words are valid.
    /// <para>FES 服务在两侧 CRC 都已算出的校验应答中置位的标志。</para></summary>
    public const uint CrcValidFlag = 0x6A617603;

    /// <summary>Mask that separates a FES data tag from its transfer flags. <para>把 FES 数据标签与传输标志分开的掩码。</para></summary>
    public const uint DataTagMask = 0x7FFF;

    /// <summary>Sector size used when a FES address counts sectors instead of bytes. <para>FES 地址按扇区而非字节计数时使用的扇区大小。</para></summary>
    public const int SectorSize = 512;
}

/// <summary>
/// eFEX command numbers exchanged with the boot ROM (FEL) or the FES service.
/// <para>与 boot ROM（FEL）或 FES 服务交换的 eFEX 命令号。</para>
/// </summary>
public enum SunxiEfexCommand : ushort
{
    /// <summary>Reads the device identity and current mode. <para>读取设备身份与当前模式。</para></summary>
    VerifyDevice = 0x0001,

    /// <summary>FEL: writes host bytes into device memory. <para>FEL：把主机字节写入设备内存。</para></summary>
    FelWrite = 0x0101,

    /// <summary>FEL: calls an address. <para>FEL：调用一个地址。</para></summary>
    FelExec = 0x0102,

    /// <summary>FEL: reads device memory into host bytes. <para>FEL：把设备内存读到主机。</para></summary>
    FelRead = 0x0103,

    /// <summary>FES: sends host bytes into storage. <para>FES：把主机字节写入存储。</para></summary>
    FesDown = 0x0206,

    /// <summary>FES: reads storage into host bytes. <para>FES：把存储读到主机。</para></summary>
    FesUp = 0x0207,

    /// <summary>FES: queries the storage type. <para>FES：查询存储类型。</para></summary>
    QueryStorage = 0x0209,

    /// <summary>FES: turns the flash controller on. <para>FES：打开闪存控制器。</para></summary>
    FlashSetOn = 0x020A,

    /// <summary>FES: turns the flash controller off. <para>FES：关闭闪存控制器。</para></summary>
    FlashSetOff = 0x020B,

    /// <summary>FES: compares a storage range against a CRC. <para>FES：把存储区间与 CRC 比较。</para></summary>
    VerifyValue = 0x020C,

    /// <summary>FES: queries the status of a previous tagged transfer. <para>FES：查询前一次带标签传输的状态。</para></summary>
    VerifyStatus = 0x020D,

    /// <summary>FES: probes the flash capacity in sectors. <para>FES：探测闪存容量（扇区）。</para></summary>
    FlashSizeProbe = 0x020E,

    /// <summary>FES: sets the tool mode and the mode that follows it. <para>FES：设置工具模式及其后续模式。</para></summary>
    ToolMode = 0x020F,

    /// <summary>FES: verifies the U-Boot block a previous tagged transfer wrote.
    /// <para>FES：校验前一次带标签传输写入的 U-Boot 块。</para></summary>
    VerifyUbootBlk = 0x0214,

    /// <summary>FES: queries the secure-boot / boot-media mode word. <para>FES：查询安全启动与启动介质模式字。</para></summary>
    QuerySecure = 0x0230,

    /// <summary>FES: queries the bitmask of the storage types the service exposes.
    /// <para>FES：查询服务所暴露存储类型的位图。</para></summary>
    QueryStorageList = 0x0232,

    /// <summary>FES: switches the active storage the following transfers target.
    /// <para>FES：切换后续传输所针对的活动存储。</para></summary>
    FlashSwitch = 0x0233,

    /// <summary>FES: reads raw NAND directly, byte-addressed. <para>FES：直读 raw NAND，按字节寻址。</para></summary>
    FesNand = 0x0301,

    /// <summary>FES: reads SPI NAND directly, byte-addressed. <para>FES：直读 SPI NAND，按字节寻址。</para></summary>
    FesSpinand = 0x0302,

    /// <summary>FES: reads SPI NOR directly, byte-addressed. <para>FES：直读 SPI NOR，按字节寻址。</para></summary>
    FesNor = 0x0303
}

/// <summary>
/// Mode a device reports for <see cref="SunxiEfexCommand.VerifyDevice"/>.
/// <para>设备针对 <see cref="SunxiEfexCommand.VerifyDevice"/> 上报的模式。</para>
/// </summary>
public enum SunxiDeviceMode : ushort
{
    /// <summary>No mode reported. <para>未上报模式。</para></summary>
    None = 0,

    /// <summary>Booting from the USB boot ROM. <para>以 USB boot ROM 运行。</para></summary>
    Fel = 1,

    /// <summary>Running the FES service inside U-Boot. <para>在 U-Boot 中运行 FES 服务。</para></summary>
    Fes = 2,

    /// <summary>Cold update mode. <para>冷更新模式。</para></summary>
    UpdateCold = 3,

    /// <summary>Hot update mode. <para>热更新模式。</para></summary>
    UpdateHot = 4
}

/// <summary>
/// Data tag of one FES transfer; the tag selects the destination and the addressing unit.
/// <para>一次 FES 传输的数据标签；标签决定目标与地址单位。</para>
/// </summary>
public enum SunxiFesDataTag : uint
{
    /// <summary>Untagged transfer whose address counts 512-byte sectors; the package plan writes partitions
    /// under <see cref="Flash"/> and reserves this value for callers that need an untagged transfer.
    /// <para>无标签传输，地址按 512 字节扇区计数；整包计划以 <see cref="Flash"/> 写入分区，本值保留给需要
    /// 无标签传输的调用方。</para></summary>
    Raw = 0x0000,

    /// <summary>DRAM configuration blob. <para>DRAM 配置数据。</para></summary>
    Dram = 0x7F00,

    /// <summary>Partition table written at address zero. <para>写入地址 0 的分区表。</para></summary>
    Mbr = 0x7F01,

    /// <summary>Boot1 image. <para>Boot1 镜像。</para></summary>
    Boot1 = 0x7F02,

    /// <summary>Boot0 image. <para>Boot0 镜像。</para></summary>
    Boot0 = 0x7F03,

    /// <summary>Erase request. <para>擦除请求。</para></summary>
    Erase = 0x7F04,

    /// <summary>Preboot image. <para>Preboot 镜像。</para></summary>
    Preboot = 0x7F08,

    /// <summary>Total image size report. <para>镜像总大小上报。</para></summary>
    FullImageSize = 0x7F10,

    /// <summary>Ext4 or UBI file system payload. <para>Ext4 或 UBI 文件系统负载。</para></summary>
    Ext4Ubifs = 0x7FF0,

    /// <summary>Logical flash access addressed in 512-byte sectors. <para>按 512 字节扇区寻址的逻辑闪存访问。</para></summary>
    Flash = 0x8000,

    /// <summary>eMMC physical boot0 partition, read back through <see cref="SunxiFesSession.UploadAsync"/>.
    /// <para>eMMC 物理 boot0 分区，经 <see cref="SunxiFesSession.UploadAsync"/> 读回。</para></summary>
    EmmcBoot0 = 0x8001,

    /// <summary>eMMC physical boot1 partition, read back through <see cref="SunxiFesSession.UploadAsync"/>.
    /// <para>eMMC 物理 boot1 分区，经 <see cref="SunxiFesSession.UploadAsync"/> 读回。</para></summary>
    EmmcBoot1 = 0x8002,

    /// <summary>Raw or SPI NAND boot0 partition, read through the storage-direct commands.
    /// <para>raw 或 SPI NAND 的 boot0 分区，经存储直读命令读取。</para></summary>
    NandBoot0 = 0x8010,

    /// <summary>Raw or SPI NAND boot1 partition, read through the storage-direct commands.
    /// <para>raw 或 SPI NAND 的 boot1 分区，经存储直读命令读取。</para></summary>
    NandBoot1 = 0x8020,

    /// <summary>SPI NOR boot0 partition, read through the storage-direct commands.
    /// <para>SPI NOR 的 boot0 分区，经存储直读命令读取。</para></summary>
    NorBoot0 = 0x8011,

    /// <summary>SPI NOR boot1 partition, read through the storage-direct commands.
    /// <para>SPI NOR 的 boot1 分区，经存储直读命令读取。</para></summary>
    NorBoot1 = 0x8021,

    /// <summary>Raw or SPI NAND boot0 size query; the storage-direct commands answer it with a 4-byte word.
    /// <para>raw 或 SPI NAND 的 boot0 大小查询；存储直读命令以 4 字节字应答。</para></summary>
    NandBoot0Size = 0x10001,

    /// <summary>Raw or SPI NAND boot1 size query; the storage-direct commands answer it with a 4-byte word.
    /// <para>raw 或 SPI NAND 的 boot1 大小查询；存储直读命令以 4 字节字应答。</para></summary>
    NandBoot1Size = 0x10002,

    /// <summary>SPI NOR boot0 size query; the storage-direct commands answer it with a 4-byte word.
    /// <para>SPI NOR 的 boot0 大小查询；存储直读命令以 4 字节字应答。</para></summary>
    NorBoot0Size = 0x10005,

    /// <summary>SPI NOR boot1 size query; the storage-direct commands answer it with a 4-byte word.
    /// <para>SPI NOR 的 boot1 大小查询；存储直读命令以 4 字节字应答。</para></summary>
    NorBoot1Size = 0x10006
}

/// <summary>
/// Erase flag the tool sends under the erase tag before a run; it selects how much storage the service erases.
/// <para>工具在一次运行前以擦除标签发送的擦除标志，决定服务擦除多少存储。</para>
/// </summary>
public enum SunxiEraseFlag : uint
{
    /// <summary>Erase nothing. <para>不擦除。</para></summary>
    None = 0x0,

    /// <summary>Erase the selected partitions only. <para>只擦除所选分区。</para></summary>
    Partition = 0x1,

    /// <summary>Erase the whole storage. <para>擦除整片存储。</para></summary>
    Full = 0x12
}

/// <summary>Identity and mode payload the device returns for <see cref="SunxiEfexCommand.VerifyDevice"/>.
/// <para>设备针对 <see cref="SunxiEfexCommand.VerifyDevice"/> 返回的身份与模式负载。</para></summary>
/// <param name="Id">SoC id used to resolve the chip family. <para>用于解析芯片家族的 SoC id。</para></param>
/// <param name="Firmware">Boot ROM or FES firmware version word. <para>boot ROM 或 FES 固件版本字。</para></param>
/// <param name="Mode">Mode the device currently runs. <para>设备当前运行的模式。</para></param>
/// <param name="DataFlag">Data flag byte. <para>数据标志字节。</para></param>
/// <param name="DataLength">Data length byte. <para>数据长度字节。</para></param>
/// <param name="DataStartAddress">Start address of the reported data window. <para>所报告数据窗口的起始地址。</para></param>
public readonly record struct SunxiDeviceIdentity(uint Id, uint Firmware, SunxiDeviceMode Mode,
    byte DataFlag, byte DataLength, uint DataStartAddress)
{
    /// <summary>Chip family name resolved from <see cref="Id"/>, or <see langword="null"/> when unknown.
    /// <para>由 <see cref="Id"/> 解析出的芯片家族名；未知时为 <see langword="null"/>。</para></summary>
    public string? ChipName => SunxiChipTable.Resolve(Id);
}

/// <summary>Bulk response every request ends with. <para>每个请求结束时收到的批量响应。</para></summary>
/// <param name="Tag">Response tag. <para>响应标签。</para></param>
/// <param name="Residue">Bytes the device did not transfer. <para>设备未传输的字节数。</para></param>
/// <param name="Status">Device status; zero means success. <para>设备状态；0 表示成功。</para></param>
public readonly record struct SunxiUsbResponse(uint Tag, uint Residue, byte Status);

/// <summary>Result of a FES verification command. <para>FES 校验命令的结果。</para></summary>
/// <param name="Flag">Status flags reported by the device. <para>设备上报的状态标志。</para></param>
/// <param name="FesCrc">CRC computed by the FES service. <para>FES 服务计算的 CRC。</para></param>
/// <param name="MediaCrc">CRC read back from the storage media. <para>从存储介质读回的 CRC。</para></param>
public readonly record struct SunxiFesVerifyResult(uint Flag, int FesCrc, int MediaCrc)
{
    /// <summary>Whether the device flagged both CRC words as valid; only then may the caller compare them.
    /// <para>设备是否把两侧 CRC 都标记为有效；只有此时调用方才可以比较它们。</para></summary>
    public bool IsCrcValid => Flag == SunxiEfexConstants.CrcValidFlag;

    /// <summary>Whether a value verification found matching CRCs; only meaningful for
    /// <see cref="SunxiFesSession.VerifyValueAsync"/>. <para>值校验是否发现两侧 CRC 一致；仅对
    /// <see cref="SunxiFesSession.VerifyValueAsync"/> 有意义。</para></summary>
    public bool IsValueMatch => IsCrcValid && FesCrc == MediaCrc;

    /// <summary>Whether a status verification succeeded, which the service reports as a zero media word; only
    /// meaningful for <see cref="SunxiFesSession.VerifyStatusAsync"/>. <para>状态校验是否成功，服务以介质字为零
    /// 表示成功；仅对 <see cref="SunxiFesSession.VerifyStatusAsync"/> 有意义。</para></summary>
    public bool IsStatusOk => IsCrcValid && MediaCrc == 0;
}

/// <summary>
/// Raised when the device answers a request with a non-zero status or an unexpected frame.
/// <para>设备以非零状态或异常帧应答请求时抛出。</para>
/// </summary>
public sealed class SunxiEfexException : Exception
{
    /// <summary>Creates the exception from the device status. <para>由设备状态创建异常。</para></summary>
    /// <param name="status">Non-zero device status. <para>非零的设备状态。</para></param>
    public SunxiEfexException(byte status)
        : base($"The Allwinner device reported status 0x{status:X2}.")
        => Status = status;

    /// <summary>Creates the exception from a message. <para>由消息创建异常。</para></summary>
    /// <param name="message">Failure description. <para>失败描述。</para></param>
    public SunxiEfexException(string message)
        : base(message)
    {
    }

    /// <summary>Device status byte, or zero when the failure happened before a status was read.
    /// <para>设备状态字节；在读取状态之前失败时为 0。</para></summary>
    public byte Status { get; }
}

/// <summary>
/// Chip table of the SoC ids the USB boot ROM reports, taken from the per-family router table of the
/// reference FEL client. Two ids are reported by more than one family; the first family wins because
/// the reference router stops at the first match.
/// <para>USB boot ROM 上报的 SoC id 芯片表，取自参考 FEL 客户端的按家族路由表。有两个 id 被多个家族上报；
/// 由于参考路由在首个匹配处停止，先登记的家族胜出。</para>
/// </summary>
internal static class SunxiChipTable
{
    private static readonly Dictionary<uint, string> Chips = new()
    {
        [0x00162300] = "A10",
        [0x00162500] = "A13/A10S/R8",
        [0x00165100] = "A20",
        [0x00165000] = "A23",
        [0x00163300] = "A31",
        [0x00166700] = "A33/R16",
        [0x00170100] = "A40I/R40",
        [0x00175500] = "A50",
        [0x00189000] = "A523/A527/MR527/T527",
        [0x00168900] = "A64",
        [0x00163900] = "A80",
        [0x00167300] = "A83T",
        [0x00185900] = "D1/F133",
        [0x00166300] = "F1C100S/F1C200S/F1C500S",
        [0x00168000] = "H2/H3",
        [0x00171800] = "H5",
        [0x00172800] = "H6",
        [0x00182300] = "H313/H616/T507/T517/H618",
        [0x00188300] = "R128",
        [0x00182100] = "R328",
        [0x00185100] = "R329",
        [0x00185500] = "R818",
        [0x00168100] = "V3S/S3",
        [0x00181600] = "V536",
        [0x00181700] = "V831",
        [0x00188600] = "V851/V853"
    };

    /// <summary>Number of registered SoC ids. <para>已登记的 SoC id 数量。</para></summary>
    public static int Count => Chips.Count;

    /// <summary>
    /// Resolves a SoC id to its family name.
    /// <para>把 SoC id 解析为家族名。</para>
    /// </summary>
    /// <param name="id">SoC id reported by the boot ROM. <para>boot ROM 上报的 SoC id。</para></param>
    /// <returns>The family name, or <see langword="null"/> when the id is not in the table.
    /// <para>家族名；id 不在表内时为 <see langword="null"/>。</para></returns>
    public static string? Resolve(uint id) => Chips.GetValueOrDefault(id);
}