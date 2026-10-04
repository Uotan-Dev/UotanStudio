namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// EDL (Emergency Download Mode) protocol constants.
/// <para>EDL（紧急下载模式）协议常量。</para>
/// </summary>
internal static class EdlConstants
{
    /// <summary>
    /// Sahara protocol version supported by the host.
    /// <para>主机支持的 Sahara 协议版本。</para>
    /// </summary>
    public const uint SaharaVersion = 2;

    /// <summary>
    /// Minimum Sahara protocol version the host accepts.
    /// <para>主机接受的最低 Sahara 协议版本。</para>
    /// </summary>
    public const uint SaharaMinVersion = 1;

    /// <summary>
    /// Sahara host-compatible version advertised in HELLO_RESPONSE.
    /// <para>在 HELLO_RESPONSE 中通告的 Sahara 主机兼容版本。</para>
    /// </summary>
    public const uint SaharaHostCompatibleVersion = 1;

    /// <summary>
    /// Maximum Sahara protocol version the host negotiates. Version 3 targets keep the
    /// READ_DATA pull-based image transfer and only change the chip-identity query.
    /// <para>主机协商的最高 Sahara 协议版本。版本 3 目标保留 READ_DATA 拉取式镜像传输，
    /// 仅芯片身份查询方式改变。</para>
    /// </summary>
    public const uint SaharaHostMaxProtocolVersion = 3;

    /// <summary>
    /// SAHARA_EXEC_CMD_READ_CHIP_ID_V3 command-mode client command (protocol version 3).
    /// <para>SAHARA_EXEC_CMD_READ_CHIP_ID_V3 命令模式客户端命令（协议版本 3）。</para>
    /// </summary>
    public const uint SaharaExecCmdReadChipIdV3 = 0x0A;

    /// <summary>
    /// SAHARA_EXEC_CMD_NOP command-mode client command.
    /// <para>SAHARA_EXEC_CMD_NOP 命令模式客户端命令。</para>
    /// </summary>
    public const uint SaharaExecCmdNop = 0x00;

    /// <summary>
    /// SAHARA_EXEC_CMD_SWITCH_TO_DMSS_DLOAD command-mode client command.
    /// <para>SAHARA_EXEC_CMD_SWITCH_TO_DMSS_DLOAD 命令模式客户端命令。</para>
    /// </summary>
    public const uint SaharaExecCmdSwitchToDmssDload = 0x04;

    /// <summary>
    /// SAHARA_EXEC_CMD_SWITCH_TO_STREAM_DLOAD command-mode client command.
    /// <para>SAHARA_EXEC_CMD_SWITCH_TO_STREAM_DLOAD 命令模式客户端命令。</para>
    /// </summary>
    public const uint SaharaExecCmdSwitchToStreamDload = 0x05;

    /// <summary>
    /// SAHARA_EXEC_CMD_READ_DEBUG_DATA command-mode client command.
    /// <para>SAHARA_EXEC_CMD_READ_DEBUG_DATA 命令模式客户端命令。</para>
    /// </summary>
    public const uint SaharaExecCmdReadDebugData = 0x06;

    /// <summary>
    /// SAHARA_EXEC_CMD_GET_COMMAND_ID_LIST command-mode client command.
    /// <para>SAHARA_EXEC_CMD_GET_COMMAND_ID_LIST 命令模式客户端命令。</para>
    /// </summary>
    public const uint SaharaExecCmdGetCommandIdList = 0x08;

    /// <summary>
    /// SAHARA_EXEC_CMD_GET_TRAINING_DATA command-mode client command.
    /// <para>SAHARA_EXEC_CMD_GET_TRAINING_DATA 命令模式客户端命令。</para>
    /// </summary>
    public const uint SaharaExecCmdGetTrainingData = 0x09;

    /// <summary>
    /// Default maximum packet length when not negotiated.
    /// <para>未协商时的默认最大包长度。</para>
    /// </summary>
    public const uint DefaultMaxPacketLength = 4096;

    /// <summary>
    /// Sahara HELLO command code.
    /// <para>Sahara HELLO 命令码。</para>
    /// </summary>
    public const uint SaharaCmdHello = 0x01;

    /// <summary>
    /// Sahara HELLO_RESPONSE command code.
    /// <para>Sahara HELLO_RESPONSE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdHelloResponse = 0x02;

    /// <summary>
    /// Sahara READ_DATA command code.
    /// <para>Sahara READ_DATA 命令码。</para>
    /// </summary>
    public const uint SaharaCmdReadData = 0x03;

    /// <summary>
    /// Sahara END_OF_IMAGE command code.
    /// <para>Sahara END_OF_IMAGE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdEndOfImage = 0x04;

    /// <summary>
    /// Sahara DONE command code.
    /// <para>Sahara DONE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdDone = 0x05;

    /// <summary>
    /// Sahara DONE_RESPONSE command code.
    /// <para>Sahara DONE_RESPONSE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdDoneResponse = 0x06;

    /// <summary>
    /// Sahara RESET command code.
    /// <para>Sahara RESET 命令码。</para>
    /// </summary>
    public const uint SaharaCmdReset = 0x07;

    /// <summary>
    /// Sahara RESET_RESPONSE command code.
    /// <para>Sahara RESET_RESPONSE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdResetResponse = 0x08;

    /// <summary>
    /// Sahara MEMORY_DEBUG command code.
    /// <para>Sahara MEMORY_DEBUG 命令码。</para>
    /// </summary>
    public const uint SaharaCmdMemoryDebug = 0x09;

    /// <summary>
    /// Sahara MEMORY_READ command code.
    /// <para>Sahara MEMORY_READ 命令码。</para>
    /// </summary>
    public const uint SaharaCmdMemoryRead = 0x0a;

    /// <summary>
    /// Sahara COMMAND_READY command code.
    /// <para>Sahara COMMAND_READY 命令码。</para>
    /// </summary>
    public const uint SaharaCmdCommandReady = 0x0b;

    /// <summary>
    /// Sahara SWITCH_MODE command code.
    /// <para>Sahara SWITCH_MODE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdSwitchMode = 0x0c;

    /// <summary>
    /// Sahara EXECUTE command code.
    /// <para>Sahara EXECUTE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdExecute = 0x0d;

    /// <summary>
    /// Sahara EXECUTE_RESPONSE command code.
    /// <para>Sahara EXECUTE_RESPONSE 命令码。</para>
    /// </summary>
    public const uint SaharaCmdExecuteResponse = 0x0e;

    /// <summary>
    /// Sahara EXECUTE_DATA command code.
    /// <para>Sahara EXECUTE_DATA 命令码。</para>
    /// </summary>
    public const uint SaharaCmdExecuteData = 0x0f;

    /// <summary>
    /// Sahara MEMORY_DEBUG64 command code.
    /// <para>Sahara MEMORY_DEBUG64 命令码。</para>
    /// </summary>
    public const uint SaharaCmdMemoryDebug64 = 0x10;

    /// <summary>
    /// Sahara MEMORY_READ64 command code.
    /// <para>Sahara MEMORY_READ64 命令码。</para>
    /// </summary>
    public const uint SaharaCmdMemoryRead64 = 0x11;

    /// <summary>
    /// Sahara READ_DATA64 command code.
    /// <para>Sahara READ_DATA64 命令码。</para>
    /// </summary>
    public const uint SaharaCmdReadData64 = 0x12;

    /// <summary>
    /// SAHARA_RESET_STATE command, introduced with protocol version 2.9.
    /// <para>SAHARA_RESET_STATE 命令，随协议版本 2.9 引入。</para>
    /// </summary>
    public const uint SaharaCmdResetState = 0x13;

    /// <summary>
    /// SAHARA_WRITE_DATA command, introduced with protocol version 3.0 as a host-driven image push.
    /// <para>SAHARA_WRITE_DATA 命令，随协议版本 3.0 引入，是主机主动推送镜像的方式。</para>
    /// </summary>
    public const uint SaharaCmdWriteData = 0x14;

    /// <summary>
    /// Largest Sahara memory-debug region table the host will buffer, mirroring the reference
    /// server's single megabyte intermediate buffer that a table must fit into
    /// (QSaharaServer/src/sahara_protocol.h:58 and QSaharaServer/src/sahara_protocol.c:626-629).
    /// <para>主机愿意缓冲的最大 Sahara 内存调试区域表大小，对齐参考服务端必须放入其中的 1 MiB
    /// 中间缓冲区（QSaharaServer/src/sahara_protocol.h:58 与 QSaharaServer/src/sahara_protocol.c:626-629）。</para>
    /// </summary>
    public const int SaharaMemoryTableLimitBytes = 1024 * 1024;

    /// <summary>
    /// Largest single Sahara MEMORY_READ request, matching the reference server's default
    /// max_ram_dump_read (QSaharaServer/src/sahara_protocol.c:80 and the chunking at
    /// QSaharaServer/src/sahara_protocol.c:922-923).
    /// <para>单次 Sahara MEMORY_READ 的最大请求量，对齐参考服务端默认的 max_ram_dump_read
    /// （QSaharaServer/src/sahara_protocol.c:80 与 QSaharaServer/src/sahara_protocol.c:922-923 的分块）。</para>
    /// </summary>
    public const int SaharaMemoryReadChunkBytes = 1024 * 1024;

    /// <summary>
    /// Byte size of one 32-bit Sahara debug-table record, the reference layout of save preference,
    /// base address, length and two twenty-byte strings
    /// (QSaharaServer/src/sahara_protocol.h:463-471).
    /// <para>单个 32 位 Sahara 调试表记录的字节数，即参考布局中保存优先级、基地址、长度与两个
    /// 20 字节字符串（QSaharaServer/src/sahara_protocol.h:463-471）。</para>
    /// </summary>
    public const int SaharaMemoryRecordBytesLegacy = 52;

    /// <summary>
    /// Byte size of one 64-bit Sahara debug-table record, the same fields widened to eight bytes
    /// (QSaharaServer/src/sahara_protocol.h:472-479).
    /// <para>单个 64 位 Sahara 调试表记录的字节数，即同一组字段全部拓宽为 8 字节
    /// （QSaharaServer/src/sahara_protocol.h:472-479）。</para>
    /// </summary>
    public const int SaharaMemoryRecordBytes64 = 64;

    /// <summary>
    /// Byte length of the fixed description and file-name fields inside a Sahara debug record
    /// (QSaharaServer/src/sahara_protocol.h:463).
    /// <para>Sahara 调试记录内固定描述与文件名字段的字节长度
    /// （QSaharaServer/src/sahara_protocol.h:463）。</para>
    /// </summary>
    public const int SaharaMemoryRecordStringBytes = 20;

    /// <summary>
    /// Host-side ceiling for one Sahara memory-debug region request. No reference implementation
    /// bounds a region by protocol, so this is a local guard against an unbounded dump rather than
    /// a claim about target behaviour.
    /// <para>主机侧对单个 Sahara 内存调试区域请求的上限。任何参考实现都未按协议限制区域大小，因此这只是
    /// 防止无限转储的本地护栏，而非对目标行为的断言。</para>
    /// </summary>
    public const long SaharaMemoryRegionLimitBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Standard XML header for Firehose messages.
    /// <para>Firehose 消息的标准 XML 头。</para>
    /// </summary>
    public const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n";

    /// <summary>
    /// Opening &lt;data&gt; tag.
    /// <para>&lt;data&gt; 开始标签。</para>
    /// </summary>
    public const string DataOpen = "<data>\n";

    /// <summary>
    /// Closing &lt;/data&gt; tag.
    /// <para>&lt;/data&gt; 结束标签。</para>
    /// </summary>
    public const string DataClose = "</data>\n";

    /// <summary>
    /// Default command timeout in milliseconds.
    /// <para>默认命令超时（毫秒）。</para>
    /// </summary>
    public const int DefaultCommandTimeoutMs = 10000;

    /// <summary>
    /// Timeout for the initial HELLO probe in milliseconds.
    /// <para>初始 HELLO 探测超时（毫秒）。</para>
    /// </summary>
    public const int HelloProbeTimeoutMs = 1500;

    /// <summary>
    /// Short timeout used to read the optional trailing packet after the Sahara handshake
    /// loop exits (timeout is tolerated — some devices send nothing after DONE_RESPONSE).
    /// <para>握手循环退出后读取可选尾包的短超时（毫秒）；超时可容忍——部分设备在
    /// DONE_RESPONSE 之后不再发送任何包。</para>
    /// </summary>
    public const int SaharaTrailingPacketTimeoutMs = 1000;

    /// <summary>
    /// Total read budget (ms) for the active Firehose NOP probe.
    /// <para>主动 Firehose NOP 探测的总读取预算（毫秒）。</para>
    /// </summary>
    public const int ProbeReadBudgetMs = 3000;

    /// <summary>
    /// Read chunk size for the active Firehose NOP probe.
    /// <para>主动 Firehose NOP 探测的读取分块大小。</para>
    /// </summary>
    public const int ProbeReadChunkBytes = 4096;

    /// <summary>
    /// Default response timeout in seconds.
    /// <para>默认响应超时（秒）。</para>
    /// </summary>
    public const int DefaultResponseTimeoutSeconds = 120;

    /// <summary>
    /// Timeout for configure response in milliseconds.
    /// <para>配置响应超时（毫秒）。</para>
    /// </summary>
    public const int ConfigureResponseTimeoutMs = 15000;

    /// <summary>
    /// Timeout for the first configure attempt in milliseconds.
    /// <para>首次配置尝试超时（毫秒）。</para>
    /// </summary>
    public const int ConfigureFirstAttemptTimeoutMs = 3000;

    /// <summary>
    /// Timeout for power operation response in milliseconds.
    /// <para>电源操作响应超时（毫秒）。</para>
    /// </summary>
    public const int PowerResponseTimeoutMs = 3000;

    /// <summary>
    /// Default budget (ms) for verifying that a power reset actually took effect by reading
    /// target logs until the device disappears.
    /// <para>通过读取目标日志直到设备消失来验证电源复位是否生效的默认预算（毫秒）。</para>
    /// </summary>
    public const int PowerResetVerifyTimeoutMs = 30000;

    /// <summary>
    /// Per-storage write (ZLP) timeout for UFS storage in milliseconds.
    /// <para>UFS 存储的写入（ZLP）超时（毫秒）。</para>
    /// </summary>
    public const int WriteTimeoutUfsMs = 20000;

    /// <summary>
    /// Per-storage write (ZLP) timeout for NAND storage in milliseconds.
    /// <para>NAND 存储的写入（ZLP）超时（毫秒）。</para>
    /// </summary>
    public const int WriteTimeoutNandMs = 30000;

    /// <summary>
    /// Per-storage write (ZLP) timeout for SPI NOR storage in milliseconds.
    /// <para>SPI NOR 存储的写入（ZLP）超时（毫秒）。</para>
    /// </summary>
    public const int WriteTimeoutSpinorMs = 60000;

    /// <summary>
    /// Default write (ZLP) timeout for other storage types (eMMC etc.) in milliseconds.
    /// <para>其他存储类型（eMMC 等）的默认写入（ZLP）超时（毫秒）。</para>
    /// </summary>
    public const int WriteTimeoutDefaultMs = 10000;

    /// <summary>
    /// Per-storage erase timeout for eMMC storage in milliseconds.
    /// <para>eMMC 存储的擦除超时（毫秒）。</para>
    /// </summary>
    public const int EraseTimeoutEmmcMs = 15000;

    /// <summary>
    /// Per-storage erase timeout for UFS storage in milliseconds.
    /// <para>UFS 存储的擦除超时（毫秒）。</para>
    /// </summary>
    public const int EraseTimeoutUfsMs = 30000;

    /// <summary>
    /// Per-storage erase timeout for NAND storage in milliseconds.
    /// <para>NAND 存储的擦除超时（毫秒）。</para>
    /// </summary>
    public const int EraseTimeoutNandMs = 60000;

    /// <summary>
    /// Per-storage erase timeout for SPI NOR storage in milliseconds.
    /// <para>SPI NOR 存储的擦除超时（毫秒）。</para>
    /// </summary>
    public const int EraseTimeoutSpinorMs = 120000;

    /// <summary>
    /// Maximum number of drain reads (each capped at the drain read timeout) performed when
    /// flushing pending data from the transport.
    /// <para>排出传输中待处理数据时执行的最大读取次数（每次读取受排出读取超时约束）。</para>
    /// </summary>
    public const int DrainMaxReads = 10;

    /// <summary>
    /// Timeout for each drain read in milliseconds.
    /// <para>每次排出读取的超时（毫秒）。</para>
    /// </summary>
    public const int DrainReadTimeoutMs = 100;

    /// <summary>
    /// Read chunk size for Firehose XML response accumulation.
    /// <para>Firehose XML 响应累积的读取分块大小。</para>
    /// </summary>
    public const int ResponseReadChunkBytes = 4096;

    /// <summary>
    /// Chunk size for streaming raw (binary) payloads over USB bulk endpoints.
    /// <para>通过 USB 批量端点流式传输原始（二进制）数据的分块大小。</para>
    /// </summary>
    public const int RawReadChunkBytes = 1024 * 1024;

    /// <summary>
    /// Sector size assumed for UFS and SPI NOR storage when auto-detection is unavailable.
    /// <para>自动检测不可用时 UFS 与 SPI NOR 存储采用的扇区大小。</para>
    /// </summary>
    public const long DefaultSectorSizeLarge = 4096;

    /// <summary>
    /// Sector size assumed for eMMC and other storage when auto-detection is unavailable.
    /// <para>自动检测不可用时 eMMC 及其他存储采用的扇区大小。</para>
    /// </summary>
    public const long DefaultSectorSizeSmall = 512;

    /// <summary>
    /// USB interface class for EDL devices.
    /// <para>EDL 设备的 USB 接口类。</para>
    /// </summary>
    public const byte EdlInterfaceClass = 0xFF;

    /// <summary>
    /// USB interface subclass for EDL devices.
    /// <para>EDL 设备的 USB 接口子类。</para>
    /// </summary>
    public const byte EdlInterfaceSubclass = 0x01;

    /// <summary>
    /// USB interface protocol for EDL devices.
    /// <para>EDL 设备的 USB 接口协议。</para>
    /// </summary>
    public const byte EdlInterfaceProtocol = 0x00;
}

/// <summary>
/// Sahara protocol command codes.
/// <para>Sahara 协议命令码。</para>
/// </summary>
internal enum SaharaCommand : uint
{
    /// <summary>
    /// HELLO command.
    /// <para>HELLO 命令。</para>
    /// </summary>
    Hello = EdlConstants.SaharaCmdHello,

    /// <summary>
    /// HELLO_RESPONSE command.
    /// <para>HELLO_RESPONSE 命令。</para>
    /// </summary>
    HelloResponse = EdlConstants.SaharaCmdHelloResponse,

    /// <summary>
    /// READ_DATA command.
    /// <para>READ_DATA 命令。</para>
    /// </summary>
    ReadData = EdlConstants.SaharaCmdReadData,

    /// <summary>
    /// END_OF_IMAGE command.
    /// <para>END_OF_IMAGE 命令。</para>
    /// </summary>
    EndOfImage = EdlConstants.SaharaCmdEndOfImage,

    /// <summary>
    /// DONE command.
    /// <para>DONE 命令。</para>
    /// </summary>
    Done = EdlConstants.SaharaCmdDone,

    /// <summary>
    /// DONE_RESPONSE command.
    /// <para>DONE_RESPONSE 命令。</para>
    /// </summary>
    DoneResponse = EdlConstants.SaharaCmdDoneResponse,

    /// <summary>
    /// RESET command.
    /// <para>RESET 命令。</para>
    /// </summary>
    Reset = EdlConstants.SaharaCmdReset,

    /// <summary>
    /// RESET_RESPONSE command.
    /// <para>RESET_RESPONSE 命令。</para>
    /// </summary>
    ResetResponse = EdlConstants.SaharaCmdResetResponse,

    /// <summary>
    /// MEMORY_DEBUG command.
    /// <para>MEMORY_DEBUG 命令。</para>
    /// </summary>
    MemoryDebug = EdlConstants.SaharaCmdMemoryDebug,

    /// <summary>
    /// MEMORY_READ command.
    /// <para>MEMORY_READ 命令。</para>
    /// </summary>
    MemoryRead = EdlConstants.SaharaCmdMemoryRead,

    /// <summary>
    /// COMMAND_READY command.
    /// <para>COMMAND_READY 命令。</para>
    /// </summary>
    CommandReady = EdlConstants.SaharaCmdCommandReady,

    /// <summary>
    /// SWITCH_MODE command.
    /// <para>SWITCH_MODE 命令。</para>
    /// </summary>
    SwitchMode = EdlConstants.SaharaCmdSwitchMode,

    /// <summary>
    /// EXECUTE command.
    /// <para>EXECUTE 命令。</para>
    /// </summary>
    Execute = EdlConstants.SaharaCmdExecute,

    /// <summary>
    /// EXECUTE_RESPONSE command.
    /// <para>EXECUTE_RESPONSE 命令。</para>
    /// </summary>
    ExecuteResponse = EdlConstants.SaharaCmdExecuteResponse,

    /// <summary>
    /// EXECUTE_DATA command.
    /// <para>EXECUTE_DATA 命令。</para>
    /// </summary>
    ExecuteData = EdlConstants.SaharaCmdExecuteData,

    /// <summary>
    /// MEMORY_DEBUG64 command.
    /// <para>MEMORY_DEBUG64 命令。</para>
    /// </summary>
    MemoryDebug64 = EdlConstants.SaharaCmdMemoryDebug64,

    /// <summary>
    /// MEMORY_READ64 command.
    /// <para>MEMORY_READ64 命令。</para>
    /// </summary>
    MemoryRead64 = EdlConstants.SaharaCmdMemoryRead64,

    /// <summary>
    /// READ_DATA64 command.
    /// <para>READ_DATA64 命令。</para>
    /// </summary>
    ReadData64 = EdlConstants.SaharaCmdReadData64,

    /// <summary>
    /// RESET_STATE command (protocol version 2.9).
    /// <para>RESET_STATE 命令（协议版本 2.9）。</para>
    /// </summary>
    ResetState = EdlConstants.SaharaCmdResetState,

    /// <summary>
    /// WRITE_DATA command (protocol version 3.0, host-driven image push).
    /// <para>WRITE_DATA 命令（协议版本 3.0，主机主动推送镜像）。</para>
    /// </summary>
    WriteData = EdlConstants.SaharaCmdWriteData,
}

/// <summary>
/// Sahara protocol operational modes.
/// <para>Sahara 协议操作模式。</para>
/// </summary>
public enum SaharaMode : uint
{
    /// <summary>
    /// Image transfer pending mode.
    /// <para>镜像传输待处理模式。</para>
    /// </summary>
    ImageTxPending = 0x00,

    /// <summary>
    /// Image transfer complete mode.
    /// <para>镜像传输完成模式。</para>
    /// </summary>
    ImageTxComplete = 0x01,

    /// <summary>
    /// Memory debug mode.
    /// <para>内存调试模式。</para>
    /// </summary>
    MemoryDebug = 0x02,

    /// <summary>
    /// Command mode.
    /// <para>命令模式。</para>
    /// </summary>
    Command = 0x03
}

/// <summary>
/// Sahara image identifiers.
/// <para>Sahara 镜像标识符。</para>
/// </summary>
public enum SaharaImageId : uint
{
    /// <summary>
    /// MBN image identifier.
    /// <para>MBN 镜像标识符。</para>
    /// </summary>
    Mbn = 0x01,

    /// <summary>
    /// SBL1 image identifier.
    /// <para>SBL1 镜像标识符。</para>
    /// </summary>
    Sbl1 = 0x04,

    /// <summary>
    /// TrustZone image identifier.
    /// <para>TrustZone 镜像标识符。</para>
    /// </summary>
    Tz = 0x05,

    /// <summary>
    /// RPM image identifier.
    /// <para>RPM 镜像标识符。</para>
    /// </summary>
    Rpm = 0x06,

    /// <summary>
    /// Apps image identifier.
    /// <para>Apps 镜像标识符。</para>
    /// </summary>
    Apps = 0x07,

    /// <summary>
    /// DSP image identifier.
    /// <para>DSP 镜像标识符。</para>
    /// </summary>
    Dsp = 0x0B,

    /// <summary>
    /// Firehose programmer image identifier.
    /// <para>Firehose 编程器镜像标识符。</para>
    /// </summary>
    Firehose = 0x0D,

    /// <summary>
    /// Alternative Firehose programmer image identifier.
    /// <para>备用 Firehose 编程器镜像标识符。</para>
    /// </summary>
    FirehoseAlt = 0x0E,

    /// <summary>
    /// Hash image identifier.
    /// <para>哈希镜像标识符。</para>
    /// </summary>
    Hash = 0x0F,
}

/// <summary>
/// EDL error codes.
/// <para>EDL 错误码。</para>
/// </summary>
internal enum EdlErrorCode
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
    InvalidParameter,

    /// <summary>
    /// Operation not supported.
    /// <para>不支持该操作。</para>
    /// </summary>
    NotSupported,

    /// <summary>
    /// Transport operation timed out.
    /// <para>传输操作超时。</para>
    /// </summary>
    TransportTimeout,

    /// <summary>
    /// Read port operation timed out.
    /// <para>读取端口操作超时。</para>
    /// </summary>
    ReadPortTimeout,

    /// <summary>
    /// Write port operation timed out.
    /// <para>写入端口操作超时。</para>
    /// </summary>
    WritePortTimeout,

    /// <summary>
    /// Component not initialized.
    /// <para>组件未初始化。</para>
    /// </summary>
    NotInitialized,

    /// <summary>
    /// File I/O error.
    /// <para>文件 I/O 错误。</para>
    /// </summary>
    FileIO,

    /// <summary>
    /// Insufficient memory.
    /// <para>内存不足。</para>
    /// </summary>
    NoMemory,

    /// <summary>
    /// Target returned NAK.
    /// <para>目标返回 NAK。</para>
    /// </summary>
    TargetNak,

    /// <summary>
    /// Sahara protocol error.
    /// <para>Sahara 协议错误。</para>
    /// </summary>
    SaharaProtocolError,

    /// <summary>
    /// Firehose protocol error.
    /// <para>Firehose 协议错误。</para>
    /// </summary>
    FirehoseProtocolError,

    /// <summary>
    /// Firehose configure negotiation failed.
    /// <para>Firehose 配置协商失败。</para>
    /// </summary>
    ConfigureFailed,

    /// <summary>
    /// Program operation failed.
    /// <para>编程操作失败。</para>
    /// </summary>
    ProgramFailed,

    /// <summary>
    /// Read operation failed.
    /// <para>读取操作失败。</para>
    /// </summary>
    ReadFailed,

    /// <summary>
    /// Erase operation failed.
    /// <para>擦除操作失败。</para>
    /// </summary>
    EraseFailed,

    /// <summary>
    /// Connection to device lost.
    /// <para>与设备的连接丢失。</para>
    /// </summary>
    ConnectionLost,

    /// <summary>
    /// Operation was cancelled.
    /// <para>操作已取消。</para>
    /// </summary>
    OperationCancelled,

    /// <summary>
    /// EDL device not found.
    /// <para>未找到 EDL 设备。</para>
    /// </summary>
    DeviceNotFound,

    /// <summary>
    /// Peek (memory read) operation failed.
    /// <para>Peek（内存读取）操作失败。</para>
    /// </summary>
    PeekFailed,

    /// <summary>
    /// UFS provisioning operation failed.
    /// <para>UFS 配置（provisioning）操作失败。</para>
    /// </summary>
    UfsProvisioningFailed,

    /// <summary>
    /// On-target (read-back / SHA256) verification failed.
    /// <para>目标端（回读 / SHA256）校验失败。</para>
    /// </summary>
    OnTargetVerificationFailed,

    /// <summary>
    /// Setting the active A/B partition failed.
    /// <para>设置活跃 A/B 槽位失败。</para>
    /// </summary>
    SetActivePartitionFailed,

    /// <summary>
    /// XML parsing failed.
    /// <para>XML 解析失败。</para>
    /// </summary>
    XmlParseError,

    /// <summary>
    /// The programmer image identity does not match the selected descriptor.
    /// <para>programmer 镜像身份与所选描述符不匹配。</para>
    /// </summary>
    ProgrammerMismatch,

    /// <summary>
    /// The device identity or catalog evidence cannot uniquely select a programmer yet.
    /// <para>设备身份或目录证据尚不足以唯一选定 programmer。</para>
    /// </summary>
    NeedsProgrammerSelection,
}

/// <summary>
/// Firehose response states.
/// <para>Firehose 响应状态。</para>
/// </summary>
public enum FirehoseResponseState
{
    /// <summary>
    /// Positive acknowledgment.
    /// <para>肯定确认。</para>
    /// </summary>
    Ack,

    /// <summary>
    /// Negative acknowledgment.
    /// <para>否定确认。</para>
    /// </summary>
    Nak,

    /// <summary>
    /// Log Message response.
    /// <para>日志消息响应。</para>
    /// </summary>
    Log,

    /// <summary>
    /// Raw mode (binary data) response.
    /// <para>原始模式（二进制数据）响应。</para>
    /// </summary>
    RawMode,

    /// <summary>
    /// Unknown response state.
    /// <para>未知响应状态。</para>
    /// </summary>
    Unknown
}

/// <summary>
/// Firehose power command reboot options.
/// <para>Firehose 电源命令的复位选项。</para>
/// </summary>
public enum RebootOption
{
    /// <summary>
    /// Normal reset (reboot into the system).
    /// <para>普通复位（重启进入系统）。</para>
    /// </summary>
    Reset,

    /// <summary>
    /// Power off the device.
    /// <para>关闭设备电源。</para>
    /// </summary>
    PowerOff,

    /// <summary>
    /// Reset back into EDL (emergency download) mode.
    /// <para>复位回 EDL（紧急下载）模式。</para>
    /// </summary>
    ResetToEdl
}