namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// ADB protocol constants aligned with AOSP (adb/protocol.h, adb.h).
/// <para>与 AOSP 对齐的 ADB 协议常量。</para>
/// </summary>
public static class AdbConstants
{
    /// <summary>
    /// Protocol version supported by this client (A_VERSION, 1.0.1).
    /// <para>此客户端支持的协议版本（A_VERSION，1.0.1）。</para>
    /// </summary>
    public const uint ProtocolVersion = 0x01000001;

    /// <summary>
    /// Maximum data payload in a single Message (A_MAX_PAYLOAD, 1 MiB).
    /// <para>单条消息的最大数据负载（A_MAX_PAYLOAD，1 MiB）。</para>
    /// </summary>
    public const uint MaxPayload = 0x00100000;

    /// <summary>
    /// Fixed ADB Message header size (24 bytes).
    /// <para>ADB 消息头固定大小（24 字节）。</para>
    /// </summary>
    public const int HeaderSize = 24;

    /// <summary>
    /// Features advertised by this client in the CNXN payload.
    /// <para>此客户端在 CNXN 负载中宣告的特性列表。</para>
    /// </summary>
    public const string Features =
        "shell_v2,cmd,stat_v2,ls_v2,fixed_push_mkdir,apex,abb,fixed_push_symlink_timestamp," +
        "abb_exec,remount_shell,track_app,sendrecv_v2,sendrecv_v2_brotli,sendrecv_v2_lz4," +
        "sendrecv_v2_zstd,sendrecv_v2_dry_run_send";

    /// <summary>
    /// USB class/subclass/protocol for ADB devices.
    /// <para>ADB 设备的 USB 类/子类/协议标识。</para>
    /// </summary>
    /// <summary>USB interface class code for ADB devices (0xFF). <para>ADB 设备的 USB 接口类代码。</para></summary>
    public const byte InterfaceClass = 0xFF;
    /// <summary>USB interface subclass code for ADB devices (0x42). <para>ADB 设备的 USB 接口子类代码。</para></summary>
    public const byte InterfaceSubclass = 0x42;
    /// <summary>USB interface protocol code for ADB devices (0x01). <para>ADB 设备的 USB 接口协议代码。</para></summary>
    public const byte InterfaceProtocol = 0x01;

    /// <summary>
    /// Builds the CNXN system-information payload.
    /// <para>构建 CNXN 系统信息负载。</para>
    /// </summary>
    public static string BuildConnectPayload() => $"host::features={Features}";
}

/// <summary>
/// ADB wire command identifiers (AOSP, byte-swapped for little-endian wire order).
/// <para>ADB 线命令标识符（AOSP，按小端线序字节交换）。</para>
/// </summary>
public enum AdbCommand : uint
{
    /// <summary>
    /// Authentication (TOKEN / SIGNATURE / RSAPUBLICKEY).
    /// <para>认证（TOKEN / SIGNATURE / RSAPUBLICKEY）。</para>
    /// </summary>
    Auth = 0x48545541,
    /// <summary>
    /// Close stream.
    /// <para>关闭流。</para>
    /// </summary>
    Clse = 0x45534C43,
    /// <summary>
    /// Connect handshake.
    /// <para>连接握手。</para>
    /// </summary>
    Cnxn = 0x4E584E43,
    /// <summary>
    /// Acknowledge / ready.
    /// <para>确认 / 就绪。</para>
    /// </summary>
    Okay = 0x59414B4F,
    /// <summary>
    /// Open a stream to a service.
    /// <para>打开到服务的流。</para>
    /// </summary>
    Open = 0x4E45504F,
    /// <summary>
    /// Sync (file transfer) stream.
    /// <para>同步（文件传输）流。</para>
    /// </summary>
    Sync = 0x434E5953,
    /// <summary>
    /// Write payload to a stream.
    /// <para>向流写入负载。</para>
    /// </summary>
    Wrte = 0x45545257,
}

/// <summary>
/// ADB authentication subtypes carried in the Arg0 field of AUTH messages.
/// <para>ADB 认证子类型，承载于 AUTH 消息的 Arg0 字段。</para>
/// </summary>
public enum AdbAuthType : uint
{
    /// <summary>
    /// Token challenge.
    /// <para>令牌质询。</para>
    /// </summary>
    Token = 1,
    /// <summary>
    /// Signature response.
    /// <para>签名响应。</para>
    /// </summary>
    Signature = 2,
    /// <summary>
    /// RSA public key.
    /// <para>RSA 公钥。</para>
    /// </summary>
    RsaPublicKey = 3,
}

/// <summary>
/// Shell v2 protocol frame kinds (AOSP shell_v2_protocol.h).
/// <para>Shell v2 协议帧类型。</para>
/// </summary>
public enum ShellFrameKind : byte
{
    /// <summary>
    /// STDIN data.
    /// <para>STDIN 数据。</para>
    /// </summary>
    Stdin = 0,
    /// <summary>
    /// STDOUT data.
    /// <para>STDOUT 数据。</para>
    /// </summary>
    Stdout = 1,
    /// <summary>
    /// STDERR data.
    /// <para>STDERR 数据。</para>
    /// </summary>
    Stderr = 2,
    /// <summary>
    /// Exit request carrying the exit code.
    /// <para>携带退出码的退出请求。</para>
    /// </summary>
    Exit = 3,
    /// <summary>
    /// Close STDIN.
    /// <para>关闭 STDIN。</para>
    /// </summary>
    CloseStdin = 4,
    /// <summary>
    /// Window size change.
    /// <para>窗口大小变更。</para>
    /// </summary>
    WindowSizeChange = 5,
}

/// <summary>
/// Sync wire command identifiers (AOSP sync.h / SYNC.TXT).
/// <para>Sync 线命令标识符。</para>
/// </summary>
public static class SyncIds
{
    /// <summary>Stat a remote file. 查询远端文件状态。</summary>
    public const string Stat = "STAT";
    /// <summary>List a remote directory. 列出远端目录。</summary>
    public const string List = "LIST";
    /// <summary>Directory entry. 目录条目。</summary>
    public const string Dent = "DENT";
    /// <summary>Receive (pull) a file. 接收（拉取）文件。</summary>
    public const string Recv = "RECV";
    /// <summary>Send (push) a file. 发送（推送）文件。</summary>
    public const string Send = "SEND";
    /// <summary>Data chunk. 数据块。</summary>
    public const string Data = "DATA";
    /// <summary>Transfer complete. 传输完成。</summary>
    public const string Done = "DONE";
    /// <summary>Quit sync session. 退出 sync 会话。</summary>
    public const string Quit = "QUIT";
    /// <summary>Success acknowledgment. 成功确认。</summary>
    public const string Okay = "OKAY";
    /// <summary>Failure response. 失败响应。</summary>
    public const string Fail = "FAIL";
}