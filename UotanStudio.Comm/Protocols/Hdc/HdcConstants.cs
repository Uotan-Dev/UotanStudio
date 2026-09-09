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
    /// Default HDC server TCP port (DEFAULT_PORT).
    /// <para>默认 HDC 服务端 TCP 端口（DEFAULT_PORT）。</para>
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
    /// Stable / legacy maximum payload size (60 KiB, compatible with older versions).
    /// <para>稳定版/旧版最大负载大小（60 KiB，兼容旧版本）。</para>
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
    /// <returns>The connect payload string. 连接负载字符串。</returns>
    public static string BuildConnectPayload() => $"ohos::{Features}";
}

/// <summary>
/// HDC wire command identifiers (OpenHarmony, define_enum.h HdcCommand).
/// <para>HDC 线命令标识符（OpenHarmony，define_enum.h HdcCommand）。</para>
/// </summary>
public enum HdcCommand : ushort
{
    /// <summary>Help / usage. 帮助/用法。</summary>
    Help = 0,
    /// <summary>Session handshake. 会话握手。</summary>
    Handshake = 1,
    /// <summary>Channel close. 通道关闭。</summary>
    ChannelClose = 2,
    /// <summary>Target discover broadcast. 目标发现广播。</summary>
    TargetDiscover = 4,
    /// <summary>Target list. 目标列表。</summary>
    TargetList = 5,
    /// <summary>Connect to any available target. 连接到任意可用目标。</summary>
    TargetAny = 6,
    /// <summary>Connect to a specific target. 连接到指定目标。</summary>
    TargetConnect = 7,
    /// <summary>Disconnect from a target. 断开目标连接。</summary>
    TargetDisconnect = 8,
    /// <summary>Echo request. 回显请求。</summary>
    Echo = 9,
    /// <summary>Raw echo request. 原始回显请求。</summary>
    EchoRaw = 10,
    /// <summary>Peer announces task creation; there is no response payload.</summary>
    WakeupSlaveTask = 12,
    /// <summary>Enable keep-alive. 启用保活。</summary>
    EnableKeepalive = 11,
    /// <summary>Check HDC server status. 检查 HDC 服务端状态。</summary>
    CheckServer = 13,
    /// <summary>Check device status. 检查设备状态。</summary>
    CheckDevice = 14,
    /// <summary>Wait for device. 等待设备。</summary>
    WaitFor = 15,
    /// <summary>Start a service. 启动服务。</summary>
    ServiceStart = 17,
    /// <summary>SSL/TLS handshake. SSL/TLS 握手。</summary>
    SslHandshake = 20,

    // Unity commands (1000+)

    /// <summary>Execute a command. 执行命令。</summary>
    UnityExecute = 1001,
    /// <summary>Remount partitions. 重新挂载分区。</summary>
    UnityRemount = 1002,
    /// <summary>Reboot device. 重启设备。</summary>
    UnityReboot = 1003,
    /// <summary>Query / set run mode. 查询/设置运行模式。</summary>
    UnityRunmode = 1004,
    /// <summary>Stream hilog output. 流式输出 hilog。</summary>
    UnityHilog = 1005,
    /// <summary>Execute as root. 以 root 执行。</summary>
    UnityRootRun = 1007,
    /// <summary>Lists debuggable processes.</summary>
    JdwpList = 1008,
    /// <summary>Tracks changes to debuggable processes.</summary>
    JdwpTrack = 1009,
    /// <summary>Initialize bugreport. 初始化 bugreport。</summary>
    UnityBugreportInit = 1011,
    /// <summary>Bugreport data transfer. bugreport 数据传输。</summary>
    UnityBugreportData = 1012,
    /// <summary>Extended execute command. 扩展执行命令。</summary>
    UnityExecuteEx = 1200,

    // Shell (2000+)

    /// <summary>Initialize shell session. 初始化 shell 会话。</summary>
    ShellInit = 2000,
    /// <summary>Shell data stream. shell 数据流。</summary>
    ShellData = 2001,

    // Forward (2500+)

    /// <summary>Initialize port forwarding. 初始化端口转发。</summary>
    ForwardInit = 2500,
    /// <summary>Check forward status. 检查转发状态。</summary>
    ForwardCheck = 2501,
    /// <summary>Forward check result. 转发检查结果。</summary>
    ForwardCheckResult = 2502,
    /// <summary>Activate slave channel. 激活从通道。</summary>
    ForwardActiveSlave = 2503,
    /// <summary>Activate master channel. 激活主通道。</summary>
    ForwardActiveMaster = 2504,
    /// <summary>Forward data. 转发数据。</summary>
    ForwardData = 2505,
    /// <summary>Free forward context. 释放转发上下文。</summary>
    ForwardFreeContext = 2506,
    /// <summary>List forward rules. 列出转发规则。</summary>
    ForwardList = 2507,
    /// <summary>Remove forward rule. 移除转发规则。</summary>
    ForwardRemove = 2508,
    /// <summary>Forward success acknowledgment. 转发成功确认。</summary>
    ForwardSuccess = 2509,

    // File (3000+)

    /// <summary>Initialize file transfer. 初始化文件传输。</summary>
    FileInit = 3000,
    /// <summary>Check file transfer. 检查文件传输。</summary>
    FileCheck = 3001,
    /// <summary>Begin file transfer. 开始文件传输。</summary>
    FileBegin = 3002,
    /// <summary>File data chunk. 文件数据块。</summary>
    FileData = 3003,
    /// <summary>Finish file transfer. 完成文件传输。</summary>
    FileFinish = 3004,
    /// <summary>Sideload package. 侧载安装包。</summary>
    FileSideload = 3005,
    /// <summary>Set file mode. 设置文件权限。</summary>
    FileMode = 3006,
    /// <summary>Directory mode. 目录模式。</summary>
    DirMode = 3007,

    // App (3500+)

    /// <summary>Initialize app management. 初始化应用管理。</summary>
    AppInit = 3500,
    /// <summary>Check app status. 检查应用状态。</summary>
    AppCheck = 3501,
    /// <summary>Begin app install. 开始应用安装。</summary>
    AppBegin = 3502,
    /// <summary>App data transfer. 应用数据传输。</summary>
    AppData = 3503,
    /// <summary>Finish app install. 完成应用安装。</summary>
    AppFinish = 3504,
    /// <summary>Uninstall app. 卸载应用。</summary>
    AppUninstall = 3505,

    // Flash (4000+)

    /// <summary>Initialize flash daemon update. 初始化刷机守护更新。</summary>
    FlashdUpdateInit = 4000,
    /// <summary>Initialize flash operation. 初始化刷写操作。</summary>
    FlashdFlashInit = 4001,
    /// <summary>Check flash status. 检查刷写状态。</summary>
    FlashdCheck = 4002,
    /// <summary>Begin flash. 开始刷写。</summary>
    FlashdBegin = 4003,
    /// <summary>Flash data transfer. 刷写数据传输。</summary>
    FlashdData = 4004,
    /// <summary>Finish flash. 完成刷写。</summary>
    FlashdFinish = 4005,
    /// <summary>Erase partition. 擦除分区。</summary>
    FlashdErase = 4006,
    /// <summary>Format partition. 格式化分区。</summary>
    FlashdFormat = 4007,
    /// <summary>Flash progress report. 刷写进度报告。</summary>
    FlashdProgress = 4008,

    // Heartbeat

    /// <summary>Heartbeat keep-alive message. 心跳保活消息。</summary>
    HeartbeatMsg = 5000,
}

/// <summary>
/// HDC connection transport types.
/// <para>HDC 连接传输类型。</para>
/// </summary>
public enum HdcConnType : byte
{
    /// <summary>USB transport. USB 传输。</summary>
    Usb = 0,
    /// <summary>TCP/IP transport. TCP/IP 传输。</summary>
    Tcp = 1,
    /// <summary>Serial transport. 串口传输。</summary>
    Serial = 2,
    /// <summary>Bluetooth transport. 蓝牙传输。</summary>
    Bt = 3,
    /// <summary>Unknown transport. 未知传输。</summary>
    Unknown = 4,
}

/// <summary>
/// HDC connection status flags.
/// <para>HDC 连接状态标志。</para>
/// </summary>
public enum HdcConnStatus : byte
{
    /// <summary>Status unknown. 状态未知。</summary>
    Unknown = 0,
    /// <summary>Ready for connection. 准备就绪。</summary>
    Ready = 1,
    /// <summary>Connected. 已连接。</summary>
    Connected = 2,
    /// <summary>Device offline. 设备离线。</summary>
    Offline = 3,
    /// <summary>Unauthorized (key not confirmed). 未授权（密钥未确认）。</summary>
    Unauthorized = 4,
}

/// <summary>
/// USB option byte values for HDC USB framing.
/// <para>HDC USB 帧的选项字节值。</para>
/// </summary>
public enum HdcUsbOption : byte
{
    /// <summary>Full header follows. 后续为完整头部。</summary>
    Header = 1,
    /// <summary>Reset / abort. 重置/中止。</summary>
    Reset = 2,
}

/// <summary>
/// HDC message severity levels.
/// <para>HDC 消息严重性级别。</para>
/// </summary>
public enum HdcMessageLevel : byte
{
    /// <summary>Failure. 失败。</summary>
    Fail = 0,
    /// <summary>Informational. 信息。</summary>
    Info = 1,
    /// <summary>Success. 成功。</summary>
    Ok = 2,
}
