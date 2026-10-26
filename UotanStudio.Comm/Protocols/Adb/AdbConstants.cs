namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Constants of the ADB wire protocol.
/// <para>ADB 线上协议常量。</para>
/// </summary>
internal static class AdbConstants
{
    /// <summary>
    /// Protocol version supported by this client (A_VERSION).
    /// <para>此客户端支持的协议版本（A_VERSION）。</para>
    /// </summary>
    public const uint ProtocolVersion = 0x01000001;

    /// <summary>
    /// Oldest STLS protocol version accepted from the peer (A_STLS_VERSION_MIN).
    /// <para>接受对端 STLS 协议版本的下限（A_STLS_VERSION_MIN）。</para>
    /// </summary>
    public const uint StlsVersionMin = 0x01000000;

    /// <summary>
    /// STLS protocol version this client replies with (A_STLS_VERSION).
    /// <para>本客户端回复的 STLS 协议版本（A_STLS_VERSION）。</para>
    /// </summary>
    public const uint StlsVersion = 0x01000000;

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
    /// <returns>The CNXN system-information payload string. <para>CNXN 系统信息负载字符串。</para></returns>
    public static string BuildConnectPayload() => $"host::features={Features}";
}

/// <summary>
/// ADB wire command identifiers, byte-swapped for little-endian wire order.
/// <para>ADB 线上命令标识符，已按小端线序做字节交换。</para>
/// </summary>
internal enum AdbCommand : uint
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
    /// Secure transport layer offer (A_STLS): the peer asks for the TLS upgrade after receiving our CNXN.
    /// <para>安全传输层提议（A_STLS）：对端在收到主机 CNXN 后请求 TLS 升级。</para>
    /// </summary>
    Stls = 0x534C5453,
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
internal enum AdbAuthType : uint
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
/// Shell v2 protocol frame kinds.
/// <para>Shell v2 协议帧类型。</para>
/// </summary>
internal enum ShellFrameKind : byte
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
/// Sync wire command identifiers.
/// <para>Sync 线命令标识符。</para>
/// </summary>
internal static class SyncIds
{
    /// <summary>Stat a remote file. <para>查询远端文件状态。</para></summary>
    public const string Stat = "STAT";
    /// <summary>List a remote directory. <para>列出远端目录。</para></summary>
    public const string List = "LIST";
    /// <summary>Directory entry. <para>目录条目。</para></summary>
    public const string Dent = "DENT";
    /// <summary>Receive (pull) a file. <para>接收（拉取）文件。</para></summary>
    public const string Recv = "RECV";
    /// <summary>Send (push) a file. <para>发送（推送）文件。</para></summary>
    public const string Send = "SEND";
    /// <summary>Data chunk. <para>数据块。</para></summary>
    public const string Data = "DATA";
    /// <summary>Transfer complete. <para>传输完成。</para></summary>
    public const string Done = "DONE";
    /// <summary>Quit sync session. <para>退出 sync 会话。</para></summary>
    public const string Quit = "QUIT";
    /// <summary>Success acknowledgment. <para>成功确认。</para></summary>
    public const string Okay = "OKAY";
    /// <summary>Failure response. <para>失败响应。</para></summary>
    public const string Fail = "FAIL";

    /// <summary>Stat a remote file via sync v2 (<c>ID_STAT_V2</c>). <para>以 sync v2 查询远端文件状态。</para></summary>
    public const string StatV2 = "STA2";
    /// <summary>Lstat a remote path via sync v2 (<c>ID_LSTAT_V2</c>). <para>以 sync v2 查询远端路径状态（不跟随符号链接）。</para></summary>
    public const string LstatV2 = "LST2";
    /// <summary>List a remote directory via sync v2 (<c>ID_LIST_V2</c>). <para>以 sync v2 列出远端目录。</para></summary>
    public const string ListV2 = "LIS2";
    /// <summary>Sync v2 directory entry (<c>ID_DENT_V2</c>). <para>sync v2 目录条目。</para></summary>
    public const string DentV2 = "DNT2";
    /// <summary>Send (push) a file via sync v2 (<c>ID_SEND_V2</c>). <para>以 sync v2 推送文件。</para></summary>
    public const string SendV2 = "SND2";
    /// <summary>Receive (pull) a file via sync v2 (<c>ID_RECV_V2</c>). <para>以 sync v2 拉取文件。</para></summary>
    public const string RecvV2 = "RCV2";
}

/// <summary>
/// Sync v2 setup flag bits carried in the <c>sync_send_v2</c>/<c>sync_recv_v2</c> message.
/// <para>sync v2 的 setup 旗标位，承载于 <c>sync_send_v2</c>/<c>sync_recv_v2</c> 消息。</para>
/// </summary>
internal static class SyncFlags
{
    /// <summary>Brotli-compressed data stream. <para>Brotli 压缩数据流。</para></summary>
    public const uint Brotli = 1;
    /// <summary>LZ4-frame-compressed data stream. <para>LZ4 帧压缩数据流。</para></summary>
    public const uint Lz4 = 2;
    /// <summary>Zstd-compressed data stream. <para>Zstd 压缩数据流。</para></summary>
    public const uint Zstd = 4;
    /// <summary>Push dry run: validate without writing the file. <para>推送试运行：只校验不写文件。</para></summary>
    public const uint DryRun = 0x8000_0000;
}