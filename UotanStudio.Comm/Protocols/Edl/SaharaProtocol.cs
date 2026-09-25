using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Implements the Sahara protocol handshake for EDL (Emergency Download Mode) devices.
/// <para>实现 EDL 设备的 Sahara 协议握手。</para>
/// Protocol flow: passive HELLO read → HELLO_RESP → READ_DATA/READ_DATA64 loop → END_OF_IMAGE → DONE → DONE_RESPONSE → complete.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SaharaProtocol"/> class.
/// <para>初始化 <see cref="SaharaProtocol"/> 类的新实例。</para>
/// </remarks>
/// <param name="transport">The device transport. 设备传输。</param>
/// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
public sealed partial class SaharaProtocol(ICommTransport transport, int? timeoutMs = null)
{
    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _timeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private bool _disposed;

    /// <summary>
    /// Gets the negotiated maximum packet length.
    /// <para>获取协商的最大包长度。</para>
    /// </summary>
    public uint MaxPacketLength { get; private set; } = EdlConstants.DefaultMaxPacketLength;

    /// <summary>
    /// Gets or sets whether the QUD HELLO recovery is enabled. When the initial passive HELLO
    /// read times out (the QUD driver may have swallowed the device's first HELLO), an
    /// unsolicited 48-byte HELLO_RESPONSE is sent so the device re-sends its HELLO.
    /// <para>获取或设置是否启用 QUD HELLO 恢复。当初始被动 HELLO 读取超时（QUD 驱动可能
    /// 吞掉了设备的首个 HELLO）时，主动发送 48 字节的 HELLO_RESPONSE 让设备重发 HELLO。</para>
    /// </summary>
    public bool QudHelloRecoveryEnabled { get; set; }

    /// <summary>
    /// Gets whether the device is already running Firehose (skipped Sahara).
    /// <para>设备是否已在运行 Firehose（跳过了 Sahara）。</para>
    /// </summary>
    public bool FirehoseAlreadyRunning { get; private set; }

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