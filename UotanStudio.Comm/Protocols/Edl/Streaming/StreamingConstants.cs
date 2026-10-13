namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Diagnostic (DIAG) command identifiers, response codes, and buffer bounds for the Qualcomm
/// Streaming DLOAD protocol that takes over the transport after Sahara exec 0x05 switches a
/// command-mode target into the streaming downloader.
/// <para>Qualcomm Streaming DLOAD（流式下载）协议的 DIAG 命令号、响应码与缓冲区边界；Sahara
/// exec 0x05 把命令模式目标切换到流式下载器后，由该协议接管传输层。</para>
/// </summary>
/// <remarks>
/// Values are the on-wire constants of the streaming protocol; each constant documents its own meaning.
/// <para>取值为流式协议的线上常量，逐项含义见各自文档。</para>
/// </remarks>
public static class StreamingConstants
{
    /// <summary>
    /// DIAG hello request command identifier, sent with <see cref="HostMagic"/> by the host
    ///.
    /// <para>DIAG hello 请求命令号，主机随 <see cref="HostMagic"/> 一起发送。</para>
    /// </summary>
    public const byte DiagHelloCommand = 0x01;

    /// <summary>
    /// DIAG hello response command identifier expected from the target.
    /// <para>期望目标返回的 DIAG hello 响应命令号。</para>
    /// </summary>
    public const byte DiagHelloResponse = 0x02;

    /// <summary>
    /// Protocol version byte the host advertises inside the hello payload, followed by the
    /// compatible-version and padding bytes 0x23 0x23 0x23 0x20.
    /// <para>主机在 hello 负载中通告的协议版本字节，其后附上兼容版本与填充字节
    /// 0x23 0x23 0x23 0x20。</para>
    /// </summary>
    public const byte DiagHostVersion = 0x03;

    /// <summary>
    /// The fixed 32-character host magic string sent inside the hello payload
    ///.
    /// <para>hello 负载中固定的 32 字符主机魔数串。</para>
    /// </summary>
    public const string HostMagic = "QCOM fast download protocol host";

    /// <summary>
    /// DIAG memory-read request command identifier used by the Qualcomm (unpatched) loader; the
    /// request carries a little-endian uint32 address and a little-endian uint16 size, and the
    /// target answers with 512-byte pages.
    /// <para>Qualcomm（未打补丁）loader 使用的 DIAG 内存读命令号；请求携带小端 uint32 地址与
    /// 小端 uint16 长度，目标按 512 字节页应答。</para>
    /// </summary>
    public const byte DiagMemReadCommand = 0x03;

    /// <summary>
    /// DIAG memory-read response identifier acknowledged before every returned page
    ///.
    /// <para>每个返回页之前确认的 DIAG 内存读响应号。</para>
    /// </summary>
    public const byte DiagMemReadResponse = 0x04;

    /// <summary>
    /// DIAG flash-program command identifier; the frame carries a little-endian uint32 byte
    /// address followed by a 1024-byte data chunk.
    /// <para>DIAG 闪存编程命令号；帧包含小端 uint32 字节地址，其后跟 1024 字节数据块
    /// 。</para>
    /// </summary>
    public const byte DiagProgramCommand = 0x07;

    /// <summary>
    /// DIAG flash-program acknowledgement identifier required after every written chunk
    ///.
    /// <para>每个写入块之后必须确认的 DIAG 编程应答号。</para>
    /// </summary>
    public const byte DiagProgramResponse = 0x08;

    /// <summary>
    /// DIAG reset command identifier sent to leave the streaming session
    ///.
    /// <para>退出流式会话时发送的 DIAG 复位命令号。</para>
    /// </summary>
    public const byte DiagResetCommand = 0x0B;

    /// <summary>
    /// DIAG ARM-exec (patched loader) command identifier; the payload is 0x11, a mode byte, a
    /// small ARM blob and its little-endian parameters.
    /// <para>DIAG ARM 执行（打补丁 loader）命令号；负载为 0x11、一个模式字节、一段小型 ARM
    /// 机器码及其小端参数。</para>
    /// </summary>
    public const byte DiagArmExecuteCommand = 0x11;

    /// <summary>
    /// DIAG ARM-exec response marker byte the patched blobs store at the start of the reply
    /// payload.
    /// <para>打补丁机器码写入应答负载起始处的 DIAG ARM 执行响应标记字节
    /// 。</para>
    /// </summary>
    public const byte DiagArmExecuteResponse = 0x12;

    /// <summary>
    /// DIAG close (qclose) command identifier that finalizes a section or ends the stream
    ///.
    /// <para>结束当前段或整个流式会话的 DIAG 关闭（qclose）命令号。</para>
    /// </summary>
    public const byte DiagCloseCommand = 0x15;

    /// <summary>
    /// DIAG close response identifier confirming the stream section was closed
    ///.
    /// <para>确认流式段已关闭的 DIAG 关闭响应号。</para>
    /// </summary>
    public const byte DiagCloseResponse = 0x16;

    /// <summary>
    /// DIAG secure-mode negotiation command identifier; the second byte selects untrusted (0x00)
    /// or trusted (0x01) mode.
    /// <para>DIAG 安全模式协商命令号；第二字节选择不受信（0x00）或受信（0x01）模式
    /// 。</para>
    /// </summary>
    public const byte DiagSecureCommand = 0x17;

    /// <summary>
    /// DIAG secure-mode response identifier confirming the negotiated state.
    /// <para>确认已协商安全状态的 DIAG 安全模式响应号。</para>
    /// </summary>
    public const byte DiagSecureResponse = 0x18;

    /// <summary>
    /// DIAG flash-mode partition-table command identifier; the frame carries a mode byte
    /// (0x00 keep, 0x01 override) followed by the raw partition table bytes.
    /// <para>DIAG 闪存模式分区表命令号；帧包含一个模式字节（0x00 保留、0x01 覆盖），其后是
    /// 原始分区表字节。</para>
    /// </summary>
    public const byte DiagFlashModeTableCommand = 0x19;

    /// <summary>
    /// DIAG flash-mode partition-table response identifier; the byte following it is the table
    /// status (0 accepted, 1 differs but override accepted, 2 format unrecognized, 3 erase
    /// failed).
    /// <para>DIAG 闪存模式分区表响应号；其后一字节为表状态（0 接受、1 不同但允许覆盖、
    /// 2 格式无法识别、3 擦除失败）。</para>
    /// </summary>
    public const byte DiagFlashModeTableResponse = 0x1A;

    /// <summary>
    /// DIAG multi-image section-header command identifier.
    /// <para>DIAG 多镜像段头命令号。</para>
    /// </summary>
    public const byte DiagSectionHeaderCommand = 0x1B;

    /// <summary>
    /// Multi-image sub-command selecting a user-defined partition inside the section header
    ///.
    /// <para>段头中选择用户自定义分区的多镜像子命令号。</para>
    /// </summary>
    public const byte DiagMultiImageUserDefined = 0x0E;

    /// <summary>
    /// DIAG section-header response identifier acknowledging the partition selection
    ///.
    /// <para>确认分区选择的 DIAG 段头响应号。</para>
    /// </summary>
    public const byte DiagSectionHeaderResponse = 0x1C;

    /// <summary>
    /// Successful (ACK) status code of the protocol's response table.
    /// <para>协议响应码表中的成功（ACK）状态码。</para>
    /// </summary>
    public const byte ResponseAck = 0x00;

    /// <summary>
    /// NAK status code: the destination address is invalid.
    /// <para>NAK 状态码：目标地址无效。</para>
    /// </summary>
    public const byte NakInvalidDest = 0x02;

    /// <summary>
    /// NAK status code: the operation length is invalid.
    /// <para>NAK 状态码：操作长度无效。</para>
    /// </summary>
    public const byte NakInvalidLen = 0x03;

    /// <summary>
    /// NAK status code: the packet was too short for the command.
    /// <para>NAK 状态码：报文对该命令过短。</para>
    /// </summary>
    public const byte NakEarlyEnd = 0x04;

    /// <summary>
    /// NAK status code: the command is invalid.
    /// <para>NAK 状态码：命令无效。</para>
    /// </summary>
    public const byte NakInvalidCmd = 0x05;

    /// <summary>
    /// NAK status code: the operation did not succeed.
    /// <para>NAK 状态码：操作未成功。</para>
    /// </summary>
    public const byte NakFailed = 0x07;

    /// <summary>
    /// NAK status code: the image identifier was wrong.
    /// <para>NAK 状态码：镜像标识错误。</para>
    /// </summary>
    public const byte NakWrongIid = 0x08;

    /// <summary>
    /// NAK status code: the read-back verify did not match.
    /// <para>NAK 状态码：回读校验不匹配。</para>
    /// </summary>
    public const byte NakVerifyFailed = 0x0A;

    /// <summary>
    /// NAK status code: the security code is incorrect.
    /// <para>NAK 状态码：安全码不正确。</para>
    /// </summary>
    public const byte NakInvalidSecCode = 0x0C;

    /// <summary>
    /// NAK status code: the command arrived out of sequence.
    /// <para>NAK 状态码：命令顺序错误。</para>
    /// </summary>
    public const byte NakCmdOutSeq = 0x0F;

    /// <summary>
    /// NAK status code: the feature bits are incompatible.
    /// <para>NAK 状态码：特性位不兼容。</para>
    /// </summary>
    public const byte NakBadFeatureBits = 0x11;

    /// <summary>
    /// NAK status code: the target is out of space.
    /// <para>NAK 状态码：目标空间不足。</para>
    /// </summary>
    public const byte NakNoSpace = 0x12;

    /// <summary>
    /// Byte size of the raw on-device partition table transferred with
    /// <see cref="DiagFlashModeTableCommand"/>.
    /// <para>随 <see cref="DiagFlashModeTableCommand"/> 传输的原始设备分区表字节大小
    /// 。</para>
    /// </summary>
    public const int PartitionTableBytes = 512;

    /// <summary>
    /// Program chunk payload size in bytes; the final short chunk is padded with 0xFF
    ///.
    /// <para>编程块的负载字节数；最后一个不足块以 0xFF 补齐。</para>
    /// </summary>
    public const int WriteChunkBytes = 1024;

    /// <summary>
    /// Page size of the Qualcomm loader memory-read loop.
    /// <para>Qualcomm loader 内存读循环的页大小。</para>
    /// </summary>
    public const int MemoryReadPageBytes = 512;

    /// <summary>
    /// Block size of the patched loader ARM memory-read loop.
    /// <para>打补丁 loader ARM 内存读循环的块大小。</para>
    /// </summary>
    public const int PatchedMemoryBlockBytes = 1000;

    /// <summary>
    /// Largest DIAG packet the host frames or accepts, matching
    /// <see cref="EdlConstants.DefaultMaxPacketLength"/> and the protocol's
    /// <c>MAX_PACKET_LEN</c>.
    /// <para>主机封装或接收的最大 DIAG 报文长度，对应 <see cref="EdlConstants.DefaultMaxPacketLength"/>
    /// 与协议的 <c>MAX_PACKET_LEN</c>。</para>
    /// </summary>
    public const int MaxPacketBytes = 4096;

    /// <summary>
    /// Host-side ceiling for one streaming memory-read request. The protocol
    /// bounds nothing, so this is a local guard against an unbounded dump rather than a claim
    /// about target behaviour.
    /// <para>主机侧单次流式内存读请求上限。协议未做任何限制，因此这只是防止无限转储的
    /// 本地护栏，而非对目标行为的断言。</para>
    /// </summary>
    public const int MaxMemoryReadBytes = 1024 * 1024;

    /// <summary>
    /// Buffer size pushed into the NAND flash buffer for one codeword by the bad-block writer.
    /// <para>坏块写入器为一个码字推入 NAND flash 缓冲区的字节数。</para>
    /// </summary>
    public const int BadMarkBufferSize = 0x220;

    /// <summary>
    /// Fill byte left at the first byte of the bad-mark buffer
    /// and the memory-read error pattern.
    /// <para>坏块标记缓冲区首字节的填充值，也是内存读错误填充值。</para>
    /// </summary>
    public const byte BadMarkFillByte = 0xEB;

    /// <summary>
    /// Smallest sector size accepted from the target's hello table or from an
    /// ONFI parameter page; a raw-nand codeword is never smaller than the
    /// 512-byte base sector.
    /// <para>接受来自目标 hello 扇区表或 ONFI 参数页的
    /// 最小扇区尺寸；基准扇区固定为 512 字节，裸 NAND 码字不会更小。</para>
    /// </summary>
    public const int MinReportedSectorBytes = 512;

    /// <summary>
    /// Largest sector size accepted from the target, bounding a device-supplied multiplier before
    /// it scales a read window or allocates a buffer; real ONFI pages stop well below this.
    /// <para>接受自目标的最大扇区尺寸，用于在设备提供的数值参与读窗口缩放或缓冲分配之前先设上限；
    /// 真实 ONFI 页远小于该值。</para>
    /// </summary>
    public const int MaxReportedSectorBytes = 65536;

    /// <summary>
    /// Largest pages-per-block value accepted from an ONFI parameter page
    /// before it is used as an address or capacity multiplier; erase blocks in the known
    /// tables span 64 to 256 pages.
    /// <para>接受自 ONFI 参数页的最大每块页数，之后才会把它用作地址或容量
    /// 乘数；已知表中的擦除块为 64 到 256 页。</para>
    /// </summary>
    public const uint MaxReportedPagesPerBlock = 4096;
}