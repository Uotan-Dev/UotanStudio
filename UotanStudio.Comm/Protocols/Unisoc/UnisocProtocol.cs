namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// Vendor and wire constants of the UNISOC (Spreadtrum) BSL download protocol: the download serial pair, the HDLC
/// frame markers, payload bounds and the per-command timeouts the sessions apply.
/// <para>紫光展锐（Spreadtrum）BSL 下载协议的厂商与线上常量：下载串口 VID/PID、HDLC 帧标记、负载上限与各命令
/// 使用的超时。</para>
/// </summary>
internal static class UnisocConstants
{
    /// <summary>UNISOC USB vendor id. <para>紫光展锐 USB 厂商 id。</para></summary>
    public const ushort VendorId = 0x1782;

    /// <summary>Download serial (SPRD U2S Diag) product id; the only pair the discovery layer accepts for BSL.
    /// <para>下载串口（SPRD U2S Diag）产品 id；发现层只接受这一组合进入 BSL。</para></summary>
    public const ushort DownloadProductId = 0x4D00;

    /// <summary>HDLC frame delimiter. <para>HDLC 帧定界符。</para></summary>
    public const byte HeaderByte = 0x7E;

    /// <summary>HDLC escape marker; the escaped byte carries <see cref="EscapeMask"/> XORed on.
    /// <para>HDLC 转义标记；被转义字节与 <see cref="EscapeMask"/> 异或。</para></summary>
    public const byte EscapeByte = 0x7D;

    /// <summary>Value XORed onto an escaped byte. <para>被转义字节异或的掩码。</para></summary>
    public const byte EscapeMask = 0x20;

    /// <summary>Largest payload one BSL frame carries; the length field is 16-bit.
    /// <para>单条 BSL 帧承载的最大负载；长度字段为 16 位。</para></summary>
    public const int MaximumPayload = 0xFFFF;

    /// <summary>Wire size of one encoded frame carrying a payload of <see cref="MaximumPayload"/> bytes, escaped.
    /// <para>承载 <see cref="MaximumPayload"/> 字节负载的一条转义后的线上帧大小。</para></summary>
    public const int MaximumEncodedFrame = 2 * (4 + MaximumPayload + 2) + 2;

    /// <summary>Wire size of the fixed 72-code-unit partition name field. <para>定长 72 码元分区名字段的线上大小。</para></summary>
    public const int PartitionNameSize = 72;

    /// <summary>Wire size of one native partition-table record. <para>原生分区表一条记录的线上大小。</para></summary>
    public const int PartitionRecordSize = 0x4C;

    /// <summary>Maximum UTF-16 code units a partition name carries. <para>分区名最多承载的 UTF-16 码元数。</para></summary>
    public const int MaximumPartitionNameUnits = 35;

    /// <summary>Marker the last native partition-table record uses for "all remaining capacity".
    /// <para>原生分区表最后一条记录表示“剩余全部容量”的标记。</para></summary>
    public const uint RemainingCapacityMarker = 0xFFFFFFFF;

    /// <summary>Boot ROM version reply that marks an unprotected boot ROM; any other reply means secure boot.
    /// <para>标志未锁定 boot ROM 的版本应答；其他应答都表示启用了安全启动。</para></summary>
    public static ReadOnlySpan<byte> OpenBootRomVersion => "SPRD3"u8;

    /// <summary>Default chunk size for FDL1/FDL2 uploads. <para>FDL1/FDL2 上传的默认分块大小。</para></summary>
    public const int DefaultLoaderBlockSize = 528;

    /// <summary>Default chunk size for storage reads and writes. <para>存储读写的默认分块大小。</para></summary>
    public const int DefaultStorageBlockSize = 4096;

    /// <summary>Largest storage chunk the sessions accept. The vendor tool raises its block size to
    /// <c>0xF800</c> (63488) on the high-speed path, which is the largest 2048-aligned value that still fits the
    /// 16-bit frame length field; larger chunks would exceed <see cref="MaximumPayload"/>.
    /// <para>会话接受的最大存储分块。厂商工具在高速路径把分块提升到 <c>0xF800</c>（63488），这是仍能装进
    /// 16 位帧长度字段的最大 2048 对齐值；更大的分块会超过 <see cref="MaximumPayload"/>。</para></summary>
    public const int MaximumStorageBlockSize = 0xF800;

    /// <summary>Default timeout of one BSL command exchange. <para>一次 BSL 命令交互的默认超时。</para></summary>
    public const int DefaultCommandTimeoutMs = 10_000;

    /// <summary>Timeout of one storage read or write chunk. <para>一个存储读写分块的超时。</para></summary>
    public const int StorageChunkTimeoutMs = 15_000;

    /// <summary>Timeout of an erase exchange, which large storage areas can outlast ordinary commands.
    /// <para>擦除交互的超时；大容量存储区域可能远超普通命令耗时。</para></summary>
    public const int EraseTimeoutMs = 300_000;

    /// <summary>Default wait after a Boot ROM exec before the first FDL handshake begins.
    /// <para>boot ROM 执行应答之后、开始第一级 FDL 握手前的默认等待。</para></summary>
    public const int Fdl1StartDelayMs = 500;

    /// <summary>Default probe budget of one CHECK_BAUD exchange on an unidentified link. The official tool's
    /// BMTimeout.ini default is 1000 ms; a 500 ms window is too short for a Boot ROM that is still settling after
    /// enumeration.
    /// <para>未识别链路上单次 CHECK_BAUD 交互的默认探测预算。官方 BMTimeout.ini 默认 1000 ms；500 ms 对刚完成枚举、
    /// 尚未稳定的 Boot ROM 过短。</para></summary>
    public const int EntryProbeTimeoutMs = 1000;

    /// <summary>How many times the FDL handshake repeats CHECK_BAUD before giving up.
    /// <para>FDL 握手在放弃前重复 CHECK_BAUD 的次数。</para></summary>
    public const int CheckBaudRetries = 10;
}

/// <summary>
/// BSL command numbers exchanged with the Boot ROM, FDL1 and FDL2.
/// <para>与 Boot ROM、FDL1、FDL2 交换的 BSL 命令号。</para>
/// </summary>
public enum UnisocCommand : ushort
{
    /// <summary>Link-layer connect. <para>链路层连接。</para></summary>
    Connect = 0x00,

    /// <summary>Starts one download to a start address. <para>开始一次到起始地址的下载。</para></summary>
    StartData = 0x01,

    /// <summary>Carries one download chunk. <para>承载一个下载分块。</para></summary>
    MidstData = 0x02,

    /// <summary>Ends one download. <para>结束一次下载。</para></summary>
    EndData = 0x03,

    /// <summary>Executes the downloaded image. <para>执行已下载的镜像。</para></summary>
    ExecData = 0x04,

    /// <summary>Resets the device into normal mode. <para>把设备重启进正常模式。</para></summary>
    NormalReset = 0x05,

    /// <summary>Reads flash or memory bytes. <para>读取闪存或内存字节。</para></summary>
    ReadFlash = 0x06,

    /// <summary>Reads the chip type. <para>读取芯片型号。</para></summary>
    ReadChipType = 0x07,

    /// <summary>Looks up one NV item. <para>查找一个 NV 项。</para></summary>
    ReadNvItem = 0x08,

    /// <summary>Changes the link baud rate. <para>更改链路波特率。</para></summary>
    ChangeBaud = 0x09,

    /// <summary>Erases flash by address or by partition name. <para>按地址或分区名擦除闪存。</para></summary>
    EraseFlash = 0x0A,

    /// <summary>Rebuilds the partition table from an ExtTable sent beforehand. <para>按预先下发的 ExtTable 重建分区表。</para></summary>
    Repartition = 0x0B,

    /// <summary>Sends the vendor ExtTable (partition type/alias/size records) before a repartition.
    /// <para>在重分区前下发厂商 ExtTable（分区类型/别名/大小记录）。</para></summary>
    SendExtTable = 0x14,

    /// <summary>Reads the flash type. <para>读取闪存类型。</para></summary>
    ReadFlashType = 0x0C,

    /// <summary>Reads the flash information. <para>读取闪存信息。</para></summary>
    ReadFlashInfo = 0x0D,

    /// <summary>Reads the NOR sector size. <para>读取 NOR 扇区大小。</para></summary>
    ReadSectorSize = 0x0F,

    /// <summary>Starts one named-range read session. <para>开始一次按名称范围的读会话。</para></summary>
    ReadStart = 0x10,

    /// <summary>Carries one read chunk of the open session. <para>承载当前读会话的一个分块。</para></summary>
    ReadMidst = 0x11,

    /// <summary>Ends the open read session. <para>结束当前读会话。</para></summary>
    ReadEnd = 0x12,

    /// <summary>Keeps the device charging while downloading. <para>下载期间保持设备充电。</para></summary>
    KeepCharge = 0x13,

    /// <summary>Sets the extended table. <para>设置扩展表。</para></summary>
    ExttAble = 0x14,

    /// <summary>Reads the flash UID. <para>读取闪存 UID。</para></summary>
    ReadFlashUid = 0x15,

    /// <summary>Reads the softSIM EID. <para>读取 softSIM EID。</para></summary>
    ReadSoftSimEid = 0x16,

    /// <summary>Powers the device off. <para>让设备关机。</para></summary>
    PowerOff = 0x17,

    /// <summary>Checks the root state. <para>检查 root 状态。</para></summary>
    CheckRoot = 0x19,

    /// <summary>Reads the chip UID. <para>读取芯片 UID。</para></summary>
    ReadChipUid = 0x1A,

    /// <summary>Enables flash writes. <para>允许闪存写入。</para></summary>
    EnableWriteFlash = 0x1B,

    /// <summary>Enables secure boot. <para>启用安全启动。</para></summary>
    EnableSecureBoot = 0x1C,

    /// <summary>Starts the identify pass. <para>开始识别流程。</para></summary>
    IdentifyStart = 0x1D,

    /// <summary>Ends the identify pass. <para>结束识别流程。</para></summary>
    IdentifyEnd = 0x1E,

    /// <summary>Reads the CU reference. <para>读取 CU 参考值。</para></summary>
    ReadCuRef = 0x1F,

    /// <summary>Reads the reference info. <para>读取参考信息。</para></summary>
    ReadRefInfo = 0x20,

    /// <summary>Turns the HDLC transcoding off. <para>关闭 HDLC 转义。</para></summary>
    DisableTranscode = 0x21,

    /// <summary>Writes the PAC build time into miscdata. <para>把 PAC 构建时间写入 miscdata。</para></summary>
    WriteDatetime = 0x22,

    /// <summary>Carrier-specific placeholder. <para>运营商自定义占位命令。</para></summary>
    CustDummy = 0x23,

    /// <summary>Reads the RF transceiver type. <para>读取射频频段类型。</para></summary>
    ReadRfTransceiverType = 0x24,

    /// <summary>Sets the debug info output. <para>设置调试信息输出。</para></summary>
    SetDebugInfo = 0x25,

    /// <summary>Checks the DRAM. <para>检查 DRAM。</para></summary>
    DdrCheck = 0x26,

    /// <summary>Puts DRAM into self refresh. <para>让 DRAM 进入自刷新。</para></summary>
    SelfRefresh = 0x27,

    /// <summary>Enables the raw data (0x31/0x33) download mode. <para>启用原始数据（0x31/0x33）下载模式。</para></summary>
    WriteRawDataEnable = 0x28,

    /// <summary>Reads the NAND block info. <para>读取 NAND 块信息。</para></summary>
    ReadNandBlockInfo = 0x29,

    /// <summary>Sets the first mode. <para>设置首选模式。</para></summary>
    SetFirstMode = 0x2A,

    /// <summary>Reads the native partition table. <para>读取原生分区表。</para></summary>
    ReadPartition = 0x2D,

    /// <summary>Raw-mode download start. <para>原始模式下载开始。</para></summary>
    DloadRawStart = 0x31,

    /// <summary>Flushes raw write data. <para>冲刷原始写入数据。</para></summary>
    WriteFlushData = 0x32,

    /// <summary>Raw-mode whole-file download start. <para>原始模式整文件下载开始。</para></summary>
    DloadRawStart2 = 0x33,

    /// <summary>Reads the FDL log. <para>读取 FDL 日志。</para></summary>
    ReadLog = 0x35,

    /// <summary>Link-layer baud probe sent as repeated header bytes. <para>以重复帧头字节发送的链路层波特率探测。</para></summary>
    CheckBaud = 0x7E,

    /// <summary>Ends the flash process. <para>结束刷写流程。</para></summary>
    EndProcess = 0x7F,
}

/// <summary>
/// BSL reply numbers the device returns. <para>设备返回的 BSL 应答号。</para>
/// </summary>
public enum UnisocReply : ushort
{
    /// <summary>Operation acknowledged. <para>操作已确认。</para></summary>
    Ack = 0x80,

    /// <summary>Version string reply. <para>版本字符串应答。</para></summary>
    Version = 0x81,

    /// <summary>Invalid command number. <para>无效命令号。</para></summary>
    InvalidCommand = 0x82,

    /// <summary>Unknown command number. <para>未知命令号。</para></summary>
    UnknownCommand = 0x83,

    /// <summary>Operation failed. <para>操作失败。</para></summary>
    OperationFailed = 0x84,

    /// <summary>Baud rate not supported. <para>波特率不受支持。</para></summary>
    NotSupportBaudRate = 0x85,

    /// <summary>Download not started. <para>下载未开始。</para></summary>
    DownNotStart = 0x86,

    /// <summary>Download started twice. <para>下载被重复开始。</para></summary>
    DownMultiStart = 0x87,

    /// <summary>Download ended too early. <para>下载过早结束。</para></summary>
    DownEarlyEnd = 0x88,

    /// <summary>Download destination error. <para>下载目标地址错误。</para></summary>
    DownDestError = 0x89,

    /// <summary>Download size or policy error. <para>下载大小或策略错误。</para></summary>
    DownSizeError = 0x8A,

    /// <summary>Verification failed. <para>校验失败。</para></summary>
    VerifyError = 0x8B,

    /// <summary>Not verified. <para>未校验。</para></summary>
    NotVerify = 0x8C,

    /// <summary>Device is out of memory. <para>设备内存不足。</para></summary>
    PhoneNotEnoughMemory = 0x8D,

    /// <summary>Device input wait timed out. <para>设备等待输入超时。</para></summary>
    PhoneWaitInputTimeout = 0x8E,

    /// <summary>Internal success marker. <para>内部成功标记。</para></summary>
    PhoneSucceed = 0x8F,

    /// <summary>Internal valid baud rate marker. <para>内部有效波特率标记。</para></summary>
    PhoneValidBaudRate = 0x90,

    /// <summary>Internal repeat continue marker. <para>内部重复继续标记。</para></summary>
    PhoneRepeatContinue = 0x91,

    /// <summary>Internal repeat break marker. <para>内部重复中断标记。</para></summary>
    PhoneRepeatBreak = 0x92,

    /// <summary>Read data reply. <para>读数据应答。</para></summary>
    ReadFlash = 0x93,

    /// <summary>Chip type reply. <para>芯片型号应答。</para></summary>
    ReadChipType = 0x94,

    /// <summary>NV item reply. <para>NV 项应答。</para></summary>
    ReadNvItem = 0x95,

    /// <summary>Incompatible partition; FDL2 also returns it after an exec it does not need.
    /// <para>分区不兼容；FDL2 在收到其不需要的执行命令时也可能返回。</para></summary>
    IncompatiblePartition = 0x96,

    /// <summary>Unknown device. <para>未知设备。</para></summary>
    UnknownDevice = 0x97,

    /// <summary>Invalid device size. <para>设备容量非法。</para></summary>
    InvalidDeviceSize = 0x98,

    /// <summary>Illegal SDRAM configuration. <para>非法的 SDRAM 配置。</para></summary>
    IllegalSdram = 0x99,

    /// <summary>Wrong SDRAM parameter. <para>SDRAM 参数错误。</para></summary>
    WrongSdramParameter = 0x9A,

    /// <summary>Flash information reply. <para>闪存信息应答。</para></summary>
    ReadFlashInfo = 0x9B,

    /// <summary>NOR sector size reply. <para>NOR 扇区大小应答。</para></summary>
    ReadSectorSize = 0x9C,

    /// <summary>Flash type reply. <para>闪存类型应答。</para></summary>
    ReadFlashType = 0x9D,

    /// <summary>Flash UID reply. <para>闪存 UID 应答。</para></summary>
    ReadFlashUid = 0x9E,

    /// <summary>SoftSIM EID reply. <para>SoftSIM EID 应答。</para></summary>
    ReadSoftsimEid = 0x9F,

    /// <summary>Checksum error while downloading fixed NV. <para>下载固定 NV 时校验和错误。</para></summary>
    ChecksumError = 0xA0,

    /// <summary>Checksum mismatch. <para>校验和不匹配。</para></summary>
    ChecksumDiff = 0xA1,

    /// <summary>Write error. <para>写入错误。</para></summary>
    WriteError = 0xA2,

    /// <summary>Chip ID does not match. <para>芯片 ID 不匹配。</para></summary>
    ChipIdNotMatch = 0xA3,

    /// <summary>Flash configuration error. <para>闪存配置错误。</para></summary>
    FlashConfigError = 0xA4,

    /// <summary>Download STL size error. <para>下载 STL 大小错误。</para></summary>
    DownStlSizeError = 0xA5,

    /// <summary>Phone is rooted. <para>设备已 root。</para></summary>
    PhoneIsRooted = 0xA7,

    /// <summary>Security data verification error. <para>安全数据校验错误。</para></summary>
    SecVerifyError = 0xAA,

    /// <summary>Chip UID reply. <para>芯片 UID 应答。</para></summary>
    ReadChipUid = 0xAB,

    /// <summary>Flash write is not enabled. <para>闪存写入未启用。</para></summary>
    NotEnableWriteFlash = 0xAC,

    /// <summary>Enabling secure boot failed. <para>启用安全启动失败。</para></summary>
    EnableSecureBootError = 0xAD,

    /// <summary>Identification started. <para>识别已开始。</para></summary>
    IdentifyStart = 0xAE,

    /// <summary>Identification finished. <para>识别已结束。</para></summary>
    IdentifyEnd = 0xAF,

    /// <summary>CU reference reply. <para>CU 参考值应答。</para></summary>
    ReadCuRef = 0xB0,

    /// <summary>Reference information reply. <para>参考信息应答。</para></summary>
    ReadRefInfo = 0xB1,

    /// <summary>Customized dummy reply. <para>自定义占位应答。</para></summary>
    CustomDummy = 0xB2,

    /// <summary>Flash write protection reply. <para>闪存写保护应答。</para></summary>
    FlashWrittenProtection = 0xB3,

    /// <summary>Flash initialization failed. <para>闪存初始化失败。</para></summary>
    FlashInitializingFail = 0xB4,

    /// <summary>RF transceiver type reply. <para>RF 收发器类型应答。</para></summary>
    RfTransceiverType = 0xB5,

    /// <summary>DDR check error. <para>DDR 检查错误。</para></summary>
    DdrCheckError = 0xB6,

    /// <summary>Self-refresh error. <para>自刷新错误。</para></summary>
    SelfRefreshError = 0xB7,

    /// <summary>NAND block information reply. <para>NAND 块信息应答。</para></summary>
    ReadNandBlockInfo = 0xB8,

    /// <summary>Partition table reply. <para>分区表应答。</para></summary>
    ReadPartition = 0xBA,

    /// <summary>Log reply. <para>日志应答。</para></summary>
    ReadLog = 0xBB,

    /// <summary>Unsupported command; an empty reply of this kind marks an already-running FDL2.
    /// <para>不支持的命令；该类的空应答标志着已运行的 FDL2。</para></summary>
    UnsupportedCommand = 0xFE,

    /// <summary>Asynchronous log frame. <para>异步日志帧。</para></summary>
    Log = 0xFF,
}

/// <summary>
/// The loader stage the link currently talks to, decided by the handshake instead of by VID/PID.
/// <para>链路当前对端的加载阶段，由握手判定而不是 VID/PID。</para>
/// </summary>
public enum UnisocBootStage
{
    /// <summary>The stage has not been identified yet. <para>阶段尚未识别。</para></summary>
    None = 0,

    /// <summary>The on-chip Boot ROM, using CRC-16/XMODEM frame checksums.
    /// <para>片上 Boot ROM，使用 CRC-16/XMODEM 帧校验。</para></summary>
    BootRom = 1,

    /// <summary>The first-stage loader running in internal RAM, using the additive checksum.
    /// <para>运行在内部 RAM 的一级加载器，使用加性校验和。</para></summary>
    Fdl1 = 2,

    /// <summary>The second-stage loader running in DRAM, using the additive checksum.
    /// <para>运行在 DRAM 的二级加载器，使用加性校验和。</para></summary>
    Fdl2 = 3,
}

/// <summary>
/// The frame checksum algorithm of the current stage. <para>当前阶段的帧校验算法。</para>
/// </summary>
public enum UnisocChecksumKind
{
    /// <summary>Additive ones-complement checksum, used by FDL1/FDL2.
    /// <para>加性一补数校验和，FDL1/FDL2 使用。</para></summary>
    Sum = 0,

    /// <summary>CRC-16/XMODEM, used by the Boot ROM. <para>CRC-16/XMODEM，Boot ROM 使用。</para></summary>
    Crc16 = 1,
}

/// <summary>
/// Identity captured during the handshake. <para>握手期间捕获的身份信息。</para>
/// </summary>
/// <param name="Stage">The identified loader stage. <para>识别出的加载阶段。</para></param>
/// <param name="Version">The raw version reply payload, when one was received.
/// <para>收到的版本应答原文；未收到时为 <see langword="null"/>。</para></param>
/// <param name="SecureBoot">Reports an unprotected Boot ROM reply (<c>SPRD3</c>) as <see langword="false"/>; every
/// other Boot ROM reply marks a signed loader requirement.
/// <para>未锁定 Boot ROM 的应答（<c>SPRD3</c>）为 <see langword="false"/>；其他 Boot ROM 应答都意味着需要
/// 签名加载器。</para></param>
public sealed record UnisocDeviceIdentity(UnisocBootStage Stage, byte[]? Version, bool SecureBoot);

/// <summary>
/// One native partition-table record. <para>一条原生分区表记录。</para>
/// </summary>
/// <param name="Name">Partition name, decoded from the UTF-16LE field.
/// <para>分区名，由 UTF-16LE 字段解码。</para></param>
/// <param name="SizeValue">Raw size field; the unit (bytes, KiB or 4 KiB sectors) is device-specific and must be
/// confirmed before the value is presented as a capacity.
/// <para>原始大小字段；单位（字节、KiB 或 4KiB 扇区）随设备而定，作为容量展示前必须先行确认。</para></param>
/// <param name="IsRemaining"><see langword="true"/> when the record marks all remaining capacity.
/// <para>记录表示剩余全部容量时为 <see langword="true"/>。</para></param>
public sealed record UnisocPartitionRecord(string Name, uint SizeValue, bool IsRemaining);

/// <summary>
/// Reports a failed or unexpected BSL exchange; the session is faulted and the transport closed when this is raised.
/// <para>报告失败或非预期的 BSL 交互；抛出时会话已失效且传输已关闭。</para>
/// </summary>
public sealed class UnisocProtocolException(string message) : IOException(message)
{
}