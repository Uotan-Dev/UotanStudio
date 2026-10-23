namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// HDC protocol constants aligned with OpenHarmony (hdc_define.h, define_enum.h).
/// <para>与 OpenHarmony 对齐的 HDC 协议常量。</para>
/// </summary>
public static class HdcConstants
{
    /// <summary>
    /// Protocol version (VER_PROTOCOL).
    /// <para>协议版本（VER_PROTOCOL）。</para>
    /// </summary>
    public const byte ProtocolVersion = 0x01;

    /// <summary>
    /// HDC server and UDP discovery port (DEFAULT_PORT). The device daemon's TCP port is dynamic and must be read
    /// from <c>persist.hdc.port</c> or the UDP discovery reply, so this value must not be used as a device default.
    /// <para>HDC server 与 UDP 发现端口（DEFAULT_PORT）。设备守护进程的 TCP 端口是动态的，必须从
    /// <c>persist.hdc.port</c> 或 UDP 发现应答读取，因此该值不得用作设备默认端口。</para>
    /// </summary>
    public const int DefaultPort = 8710;

    /// <summary>
    /// Two-byte packet flag for daemon channels ("HW").
    /// <para>TCP/服务通道的两字节包标识（"HW"）。</para>
    /// </summary>
    public const string PacketFlag = "HW";

    /// <summary>
    /// Two-byte USB packet fragment flag ("UB").
    /// <para>USB 分片包标识（"UB"）。</para>
    /// </summary>
    public const string UsbPacketFlag = "UB";

    /// <summary>
    /// Handshake banner string, ≤ 11 bytes ("OHOS HDC").
    /// <para>握手横幅字符串，≤ 11 字节（"OHOS HDC"）。</para>
    /// </summary>
    public const string HandshakeMessage = "OHOS HDC";

    /// <summary>
    /// Handshake failure indicator ("HS FAILED").
    /// <para>握手失败指示（"HS FAILED"）。</para>
    /// </summary>
    public const string HandshakeFailed = "HS FAILED";

    /// <summary>
    /// Maximum payload size in bytes (511 KiB).
    /// <para>单条消息的最大负载大小（511 KiB）。</para>
    /// </summary>
    public const int MaxPayloadSize = 511 * 1024;

    /// <summary>
    /// Stable / legacy maximum payload size (60 KiB, compatible with older server releases).
    /// <para>稳定版/较早协议的最大负载大小（60 KiB，与更早的 server 版本兼容）。</para>
    /// </summary>
    public const int MaxPayloadSizeStable = 60 * 1024;

    /// <summary>
    /// Maximum connect-key length in bytes.
    /// <para>最大连接密钥长度（字节）。</para>
    /// </summary>
    public const int MaxConnectKeySize = 32;

    /// <summary>
    /// Heartbeat interval in milliseconds.
    /// <para>心跳间隔（毫秒）。</para>
    /// </summary>
    public const int HeartbeatIntervalMs = 5000;

    /// <summary>
    /// Fixed daemon PayloadHead size (flag 2 + reserve 2 + version 1 + headSize 2 + dataSize 4).
    /// <para>设备 PayloadHead 固定大小为 11 字节；sessionId 仅在 USBHead 中。</para>
    /// </summary>
    public const int HeaderSize = 11;

    /// <summary>
    /// Fixed USBHead header size in bytes (flag 2 + option 1 + sessionId 4 + dataSize 4 = 11).
    /// <para>USBHead 固定大小（flag 2 + option 1 + sessionId 4 + dataSize 4 = 11 字节）。</para>
    /// </summary>
    public const int UsbHeaderSize = 11;

    /// <summary>
    /// Huawei USB vendor ID (0x12D1).
    /// <para>华为 USB 厂商 ID（0x12D1）。</para>
    /// </summary>
    public const ushort HuaweiVendorId = 0x12D1;

    /// <summary>
    /// Huawei HDC USB product ID (0x107E).
    /// <para>华为 HDC USB 产品 ID（0x107E）。</para>
    /// </summary>
    public const ushort HuaweiHdcProductId = 0x107E;

    /// <summary>
    /// USB interface class code for HDC devices (vendor-specific, 0xFF).
    /// <para>HDC 设备的 USB 接口类代码（厂商自定义，0xFF）。</para>
    /// </summary>
    public const byte InterfaceClass = 0xFF;

    /// <summary>
    /// Supported HDC feature strings advertised during connection.
    /// <para>连接时宣告的 HDC 特性列表。</para>
    /// </summary>
    public const string Features = "file,sync,app,shell,forward";

    /// <summary>
    /// Builds the connect system-information payload containing version and feature strings.
    /// <para>构建包含版本与特性字符串的连接系统信息负载。</para>
    /// </summary>
    /// <returns>The connect payload string. <para>连接负载字符串。</para></returns>
    public static string BuildConnectPayload() => $"ohos::{Features}";
}

/// <summary>
/// HDC wire command identifiers (OpenHarmony, define_enum.h HdcCommand).
/// <para>HDC 线命令标识符（OpenHarmony，define_enum.h HdcCommand）。</para>
/// </summary>
public enum HdcCommand : ushort
{
    /// <summary>Help / usage. <para>帮助/用法。</para></summary>
    Help = 0,
    /// <summary>Session handshake. <para>会话握手。</para></summary>
    Handshake = 1,
    /// <summary>Channel close. <para>通道关闭。</para></summary>
    ChannelClose = 2,
    /// <summary>Target discover broadcast. <para>目标发现广播。</para></summary>
    TargetDiscover = 4,
    /// <summary>Target list. <para>目标列表。</para></summary>
    TargetList = 5,
    /// <summary>Connect to any available target. <para>连接到任意可用目标。</para></summary>
    TargetAny = 6,
    /// <summary>Connect to a specific target. <para>连接到指定目标。</para></summary>
    TargetConnect = 7,
    /// <summary>Disconnect from a target. <para>断开目标连接。</para></summary>
    TargetDisconnect = 8,
    /// <summary>Echo request. <para>回显请求。</para></summary>
    Echo = 9,
    /// <summary>Raw echo request. <para>原始回显请求。</para></summary>
    EchoRaw = 10,
    /// <summary>
    /// Peer announces task creation; there is no response payload.
    /// <para>对端宣告任务创建；没有响应负载。</para>
    /// </summary>
    WakeupSlaveTask = 12,
    /// <summary>Enable keep-alive. <para>启用保活。</para></summary>
    EnableKeepalive = 11,
    /// <summary>Check HDC server status. <para>检查 HDC 服务端状态。</para></summary>
    CheckServer = 13,
    /// <summary>Check device status. <para>检查设备状态。</para></summary>
    CheckDevice = 14,
    /// <summary>Wait for device. <para>等待设备。</para></summary>
    WaitFor = 15,
    /// <summary>Start a service. <para>启动服务。</para></summary>
    ServiceStart = 17,
    /// <summary>SSL/TLS handshake. <para>SSL/TLS 握手。</para></summary>
    SslHandshake = 20,

    /// <summary>Execute a command. <para>执行命令。</para></summary>
    UnityExecute = 1001,
    /// <summary>Remount partitions. <para>重新挂载分区。</para></summary>
    UnityRemount = 1002,
    /// <summary>Reboot device. <para>重启设备。</para></summary>
    UnityReboot = 1003,
    /// <summary>Query / set run mode. <para>查询/设置运行模式。</para></summary>
    UnityRunmode = 1004,
    /// <summary>Stream hilog output. <para>流式输出 hilog。</para></summary>
    UnityHilog = 1005,
    /// <summary>Execute as root. <para>以 root 执行。</para></summary>
    UnityRootRun = 1007,
    /// <summary>Lists debuggable processes. <para>列出可调试进程。</para></summary>
    JdwpList = 1008,
    /// <summary>Tracks changes to debuggable processes. <para>跟踪可调试进程的变更。</para></summary>
    JdwpTrack = 1009,
    /// <summary>Initialize bugreport. <para>初始化 bugreport。</para></summary>
    UnityBugreportInit = 1011,
    /// <summary>Bugreport data transfer. <para>bugreport 数据传输。</para></summary>
    UnityBugreportData = 1012,
    /// <summary>Extended execute command. <para>扩展执行命令。</para></summary>
    UnityExecuteEx = 1200,

    /// <summary>Initialize shell session. <para>初始化 shell 会话。</para></summary>
    ShellInit = 2000,
    /// <summary>Shell data stream. <para>shell 数据流。</para></summary>
    ShellData = 2001,

    /// <summary>Initialize port forwarding. <para>初始化端口转发。</para></summary>
    ForwardInit = 2500,
    /// <summary>Check forward status. <para>检查转发状态。</para></summary>
    ForwardCheck = 2501,
    /// <summary>Forward check result. <para>转发检查结果。</para></summary>
    ForwardCheckResult = 2502,
    /// <summary>Activate slave channel. <para>激活从通道。</para></summary>
    ForwardActiveSlave = 2503,
    /// <summary>Activate master channel. <para>激活主通道。</para></summary>
    ForwardActiveMaster = 2504,
    /// <summary>Forward data. <para>转发数据。</para></summary>
    ForwardData = 2505,
    /// <summary>Free forward context. <para>释放转发上下文。</para></summary>
    ForwardFreeContext = 2506,
    /// <summary>List forward rules. <para>列出转发规则。</para></summary>
    ForwardList = 2507,
    /// <summary>Remove forward rule. <para>移除转发规则。</para></summary>
    ForwardRemove = 2508,
    /// <summary>Forward success acknowledgment. <para>转发成功确认。</para></summary>
    ForwardSuccess = 2509,

    /// <summary>Initialize file transfer. <para>初始化文件传输。</para></summary>
    FileInit = 3000,
    /// <summary>Check file transfer. <para>检查文件传输。</para></summary>
    FileCheck = 3001,
    /// <summary>Begin file transfer. <para>开始文件传输。</para></summary>
    FileBegin = 3002,
    /// <summary>File data chunk. <para>文件数据块。</para></summary>
    FileData = 3003,
    /// <summary>Finish file transfer. <para>完成文件传输。</para></summary>
    FileFinish = 3004,
    /// <summary>Sideload package. <para>侧载安装包。</para></summary>
    FileSideload = 3005,
    /// <summary>Set file mode. <para>设置文件权限。</para></summary>
    FileMode = 3006,
    /// <summary>Directory mode. <para>目录模式。</para></summary>
    DirMode = 3007,

    /// <summary>Initialize app management. <para>初始化应用管理。</para></summary>
    AppInit = 3500,
    /// <summary>Check app status. <para>检查应用状态。</para></summary>
    AppCheck = 3501,
    /// <summary>Begin app install. <para>开始应用安装。</para></summary>
    AppBegin = 3502,
    /// <summary>App data transfer. <para>应用数据传输。</para></summary>
    AppData = 3503,
    /// <summary>Finish app install. <para>完成应用安装。</para></summary>
    AppFinish = 3504,
    /// <summary>Uninstall app. <para>卸载应用。</para></summary>
    AppUninstall = 3505,

    /// <summary>Initialize flash daemon update. <para>初始化刷机守护更新。</para></summary>
    FlashdUpdateInit = 4000,
    /// <summary>Initialize flash operation. <para>初始化刷写操作。</para></summary>
    FlashdFlashInit = 4001,
    /// <summary>Check flash status. <para>检查刷写状态。</para></summary>
    FlashdCheck = 4002,
    /// <summary>Begin flash. <para>开始刷写。</para></summary>
    FlashdBegin = 4003,
    /// <summary>Flash data transfer. <para>刷写数据传输。</para></summary>
    FlashdData = 4004,
    /// <summary>Finish flash. <para>完成刷写。</para></summary>
    FlashdFinish = 4005,
    /// <summary>Erase partition. <para>擦除分区。</para></summary>
    FlashdErase = 4006,
    /// <summary>Format partition. <para>格式化分区。</para></summary>
    FlashdFormat = 4007,
    /// <summary>Flash progress report. <para>刷写进度报告。</para></summary>
    FlashdProgress = 4008,

    /// <summary>Heartbeat keep-alive message. <para>心跳保活消息。</para></summary>
    HeartbeatMsg = 5000,
}

/// <summary>
/// HDC connection transport types.
/// <para>HDC 连接传输类型。</para>
/// </summary>
internal enum HdcConnType : byte
{
    /// <summary>USB transport. USB 传输。</summary>
    Usb = 0,
    /// <summary>TCP/IP transport. TCP/IP 传输。</summary>
    Tcp = 1,
    /// <summary>
    /// Serial transport.
    /// <para>串口传输。</para>
    /// </summary>
    Serial = 2,
    /// <summary>
    /// Bluetooth transport.
    /// <para>蓝牙传输。</para>
    /// </summary>
    Bt = 3,
    /// <summary>
    /// Unknown transport.
    /// <para>未知传输。</para>
    /// </summary>
    Unknown = 4,
}

/// <summary>
/// HDC connection status flags.
/// <para>HDC 连接状态标志。</para>
/// </summary>
internal enum HdcConnStatus : byte
{
    /// <summary>
    /// Status unknown.
    /// <para>状态未知。</para>
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Ready for connection.
    /// <para>准备就绪。</para>
    /// </summary>
    Ready = 1,
    /// <summary>
    /// Connected.
    /// <para>已连接。</para>
    /// </summary>
    Connected = 2,
    /// <summary>
    /// Device offline.
    /// <para>设备离线。</para>
    /// </summary>
    Offline = 3,
    /// <summary>
    /// Unauthorized (key not confirmed).
    /// <para>未授权（密钥未确认）。</para>
    /// </summary>
    Unauthorized = 4,
}

/// <summary>
/// USB option byte values for HDC USB framing.
/// <para>HDC USB 帧的选项字节值。</para>
/// </summary>
internal enum HdcUsbOption : byte
{
    /// <summary>
    /// Full header follows.
    /// <para>后续为完整头部。</para>
    /// </summary>
    Header = 1,
    /// <summary>
    /// Reset / abort.
    /// <para>重置/中止。</para>
    /// </summary>
    Reset = 2,
}

/// <summary>
/// HDC message severity levels.
/// <para>HDC 消息严重性级别。</para>
/// </summary>
internal enum HdcMessageLevel : byte
{
    /// <summary>
    /// Failure.
    /// <para>失败。</para>
    /// </summary>
    Fail = 0,
    /// <summary>
    /// Informational.
    /// <para>信息。</para>
    /// </summary>
    Info = 1,
    /// <summary>
    /// Success.
    /// <para>成功。</para>
    /// </summary>
    Ok = 2,
}