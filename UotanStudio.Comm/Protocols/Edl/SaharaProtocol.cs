using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Implements the Sahara protocol handshake for EDL (Emergency Download Mode) devices. Protocol flow: passive HELLO read → HELLO_RESP → READ_DATA/READ_DATA64 loop → END_OF_IMAGE → DONE → DONE_RESPONSE → complete.
/// <para>实现 EDL（紧急下载模式）设备的 Sahara 协议握手。协议流程：被动读取 HELLO → HELLO_RESP → READ_DATA/READ_DATA64 循环 → END_OF_IMAGE → DONE → DONE_RESPONSE → 完成。</para></summary>
/// <remarks>Initializes a new instance of the <see cref="SaharaProtocol"/> class.
/// <para>初始化 <see cref="SaharaProtocol"/> 类的新实例。</para></remarks>
/// <param name="transport">The device transport.<para>设备传输层。</para></param>
/// <param name="timeoutMs">Optional timeout in milliseconds; <see langword="null"/> selects <see cref="EdlConstants.DefaultCommandTimeoutMs"/>.
/// <para>可选的超时（毫秒）；<see langword="null"/> 选择 <see cref="EdlConstants.DefaultCommandTimeoutMs"/>。</para></param>
public sealed partial class SaharaProtocol(ICommTransport transport, int? timeoutMs = null)
{
    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _timeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private bool _disposed;

    /// <summary>
    /// Gets the negotiated maximum packet length.
    /// <para>获取协商的最大包长度。</para>
    /// </summary>
    /// <value>The advertised maximum Sahara packet length in bytes, kept at
    /// <see cref="EdlConstants.DefaultMaxPacketLength"/> (4096).
    /// <para>对外声明的 Sahara 最大包字节数，取值为 <see cref="EdlConstants.DefaultMaxPacketLength"/>
    /// （4096）。</para></value>
    public uint MaxPacketLength { get; private set; } = EdlConstants.DefaultMaxPacketLength;

    /// <summary>
    /// Gets or sets whether the QUD HELLO recovery is enabled. When the initial passive HELLO
    /// read times out (the QUD driver may have swallowed the device's first HELLO), an
    /// unsolicited 48-byte HELLO_RESPONSE is sent so the device re-sends its HELLO.
    /// <para>获取或设置是否启用 QUD HELLO 恢复。当初始被动 HELLO 读取超时（QUD 驱动可能
    /// 吞掉了设备的首个 HELLO）时，主动发送 48 字节的 HELLO_RESPONSE 让设备重发 HELLO。</para>
    /// </summary>
    /// <value><see langword="true"/> lets <c>HandshakeAsync</c> send the unsolicited recovery
    /// HELLO_RESPONSE after the initial passive HELLO read times out; the default is
    /// <see langword="false"/>.
    /// <para>为 <see langword="true"/> 时，初始被动 HELLO 读取超时后 <c>HandshakeAsync</c> 会发送
    /// 主动的恢复 HELLO_RESPONSE；默认为 <see langword="false"/>。</para></value>
    public bool QudHelloRecoveryEnabled { get; set; }

    /// <summary>
    /// Gets or sets an explicit compatibility exception for a single Firehose image returning pending on completion.
    /// <para>获取或设置单个 Firehose 镜像在完成时返回 pending 的显式兼容例外。</para>
    /// </summary>
    /// <value>False by default; enable only for a verified device profile. Multiple images always require completion.
    /// <para>默认关闭；仅对已验证的设备配置启用。多个镜像始终要求完成状态。</para></value>
    public bool AllowSingleFirehosePendingCompletion { get; set; }

    /// <summary>
    /// Gets or sets whether an image-less handshake against a command-mode HELLO answers in command
    /// mode and returns <see cref="SaharaMode.Command"/> instead of raising the programmer-image error.
    /// <para>获取或设置在无镜像握手遇到命令模式 HELLO 时，是否以命令模式应答并返回
    /// <see cref="SaharaMode.Command"/>，而不是抛出 programmer 镜像错误。</para>
    /// </summary>
    /// <value>False by default so an image-less handshake keeps throwing; when true and the device
    /// HELLO reports <see cref="SaharaMode.Command"/>, the HELLO is acknowledged in command mode, the
    /// following COMMAND_READY is consumed, and the negotiated command mode is returned for a
    /// streaming DLOAD switch.
    /// <para>默认关闭，无镜像握手仍抛出；为 true 且设备 HELLO 报告 <see cref="SaharaMode.Command"/>
    /// 时，HELLO 以命令模式应答、消费随后的 COMMAND_READY，并返回协商的命令模式以供流式 DLOAD
    /// 切换。</para></value>
    internal bool AllowStreamDload { get; set; }

    /// <summary>
    /// Gets whether the device is already running Firehose (skipped Sahara).
    /// <para>设备是否已在运行 Firehose（跳过了 Sahara）。</para>
    /// </summary>
    /// <value><see langword="true"/> once <c>HandshakeAsync</c> detected a device already
    /// running Firehose and skipped Sahara, returning <see cref="SaharaMode.ImageTxComplete"/>.
    /// <para>当 <c>HandshakeAsync</c> 检测到设备已在运行 Firehose 并跳过 Sahara、直接返回
    /// <see cref="SaharaMode.ImageTxComplete"/> 后为 <see langword="true"/>。</para></value>
    public bool FirehoseAlreadyRunning { get; private set; }

    /// <summary>
    /// Gets the protocol version acknowledged to the device during the most recent HELLO exchange,
    /// or <see langword="null"/> before any HELLO was answered. Version 3 targets keep the
    /// READ_DATA pull-based image transfer; only the chip-identity query changes.
    /// <para>获取最近一次 HELLO 交换中向设备确认的协议版本；尚未应答 HELLO 时为
    /// <see langword="null"/>。版本 3 目标保留 READ_DATA 拉取式镜像传输，仅芯片身份查询方式改变。</para>
    /// </summary>
    /// <value>The acknowledged version, at most <see cref="EdlConstants.SaharaHostMaxProtocolVersion"/>.
    /// <para>已确认的版本号，至多为 <see cref="EdlConstants.SaharaHostMaxProtocolVersion"/>。</para></value>
    public uint? NegotiatedProtocolVersion { get; private set; }

    /// <summary>
    /// Raised when the handshake completes.
    /// <para>握手完成时触发。</para>
    /// </summary>
    public event EventHandler<SaharaMode>? HandshakeComplete;

    /// <summary>
    /// Raised for log messages.
    /// <para>日志消息事件。</para>
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Logs a Message via the <see cref="LogMessage"/> event.
    /// <para>通过 <see cref="LogMessage"/> 事件记录消息。</para>
    /// </summary>
    /// <param name="msg">The Message to log. 要记录的消息。</param>
    private void Log(string msg) => LogMessage?.Invoke(this, msg);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the protocol has been disposed.
    /// <para>如果协议已释放，则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SaharaProtocol));
        }
    }

    /// <summary>
    /// Disposes the protocol.
    /// <para>释放协议。</para>
    /// </summary>
    public void Dispose() => _disposed = true;
}