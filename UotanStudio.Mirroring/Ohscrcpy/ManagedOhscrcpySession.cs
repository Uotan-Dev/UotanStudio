using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Mirroring;

/// <summary>
/// Owns one ohscrcpy mirror session: the forwarded connection, the HDC session behind it and the device
/// lease, in that release order.
/// <para>持有一条 ohscrcpy 投屏会话：已转发的连接、其背后的 HDC 会话以及设备租约，并按该顺序释放。</para>
/// </summary>
/// <remarks>
/// <para>
/// The device service is an installed HAP, so unlike <see cref="ManagedMirrorSession"/> there is no pushed
/// binary and no long-lived shell stream. Frames are delivered as <see cref="ScrcpyVideoFrame"/> with the
/// same flag bits the scrcpy client uses, which lets the host decode both device families with one GPU
/// pipeline.
/// </para>
/// <para>
/// 设备端服务是已安装的 HAP，因此与 <see cref="ManagedMirrorSession"/> 不同：没有推送的二进制，也没有长驻
/// shell 流。帧以 <see cref="ScrcpyVideoFrame"/> 交付，标志位与 scrcpy 客户端一致，使宿主用同一套 GPU 管线
/// 解码两类设备。
/// </para>
/// </remarks>
public sealed class ManagedOhscrcpySession : IDisposable
{
    private readonly OhscrcpyClient _client;
    private readonly IOhscrcpyTunnel? _tunnel;
    private readonly IHdcDeviceSession? _hdcSession;
    private readonly DeviceLease? _lease;
    private int _disposed;
    private bool _displayPowerOn = true;

    /// <summary>Creates a session over an already-connected client and the resources behind it.
    /// <para>基于已连接的客户端及其背后资源创建会话。</para>
    /// </summary>
    /// <param name="client">Connected ohscrcpy client. <para>已连接的 ohscrcpy 客户端。</para></param>
    /// <param name="tunnel">Tunnel that owns the HDC port-forward mapping, disposed with the session.
    /// <para>持有 HDC 端口转发映射的通道，随会话一同释放。</para></param>
    /// <param name="hdcSession">HDC session that carries the tunnel, disposed with the session.
    /// <para>承载该通道的 HDC 会话，随会话一同释放。</para></param>
    /// <param name="lease">Device lease released after the HDC session.
    /// <para>在 HDC 会话之后释放的设备租约。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.
    /// <para><paramref name="client"/> 为 <see langword="null"/>。</para></exception>
    internal ManagedOhscrcpySession(OhscrcpyClient client, IOhscrcpyTunnel? tunnel = null,
        IHdcDeviceSession? hdcSession = null, DeviceLease? lease = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tunnel = tunnel;
        _hdcSession = hdcSession;
        _lease = lease;
    }

    /// <summary>Device name reported to the mirror UI. <para>报告给投屏界面的设备名。</para></summary>
    /// <value>The serial or alias supplied at construction. <para>构造时提供的序列号或别名。</para></value>
    public string DeviceName => _client.DeviceName;

    /// <summary>Video metadata from the most recent configuration packet.
    /// <para>最近一个配置包给出的视频元数据。</para></summary>
    /// <value>The codec and size, or <see langword="null"/> before the first configuration.
    /// <para>编码与尺寸；首个配置之前为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata => _client.VideoMetadata;

    /// <summary>Parsed configuration from the most recent configuration packet.
    /// <para>最近一个配置包解析出的配置。</para></summary>
    /// <value>The configuration, or <see langword="null"/> before the first configuration.
    /// <para>视频配置；首个配置之前为 <see langword="null"/>。</para></value>
    public OhscrcpyVideoConfig? VideoConfig => _client.VideoConfig;

    /// <summary>Number of configuration packets consumed since connecting.
    /// <para>本次连接以来已消费的配置包数量。</para></summary>
    /// <value>Starts at zero and increases per reconfiguration. <para>从零开始，每次重配置加一。</para></value>
    public int ConfigEpoch => _client.ConfigEpoch;

    /// <summary>Whether control packets can be written. <para>是否可以写入控制包。</para></summary>
    /// <value><see langword="true"/> while the connection is held. <para>持有连接时为 <see langword="true"/>。</para></value>
    public bool IsControlConnected => _client.IsControlConnected;

    /// <summary>Completes the connection handshake begun by the host.
    /// <para>完成由宿主发起的连接握手。</para>
    /// </summary>
    /// <param name="timeoutMs">Budget for the heartbeat challenge. <para>心跳挑战的总预算。</para></param>
    /// <exception cref="InvalidDataException">The service did not answer the challenge.
    /// <para>服务未应答挑战。</para></exception>
    public void Connect(int timeoutMs = 10_000) => _client.Connect(timeoutMs);

    /// <summary>Reads the next video frame or codec-configuration packet.
    /// <para>读取下一个视频帧或编码配置包。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-packet timeout in milliseconds. <para>单包读取超时（毫秒）。</para></param>
    /// <returns>The frame the caller must dispose, or <see langword="null"/> on timeout.
    /// <para>由调用方释放的帧；超时时为 <see langword="null"/>。</para></returns>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs) => _client.ReadFrame(timeoutMs);

    /// <summary>Translates and sends a scrcpy control message through the ohscrcpy adapter.
    /// <para>经 ohscrcpy 适配器翻译并发送一条 scrcpy 控制消息。</para>
    /// </summary>
    /// <param name="message">Message from <see cref="ScrcpyControlMessageWriter"/>. <para>由 <see cref="ScrcpyControlMessageWriter"/> 生成的消息。</para></param>
    /// <returns><see langword="true"/> when the message was mapped and sent.
    /// <para>消息已映射并发送时为 <see langword="true"/>。</para></returns>
    public bool SendControl(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _client.SendScrcpyControl(message);
    }

    /// <summary>Sends one complete ohscrcpy control packet. <para>发送一个完整的 ohscrcpy 控制包。</para></summary>
    /// <param name="packet">Packet from <see cref="OhscrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="OhscrcpyControlMessageWriter"/> 生成的包。</para></param>
    public void SendOhscrcpyControl(byte[] packet) => _client.SendPacket(packet);

    /// <summary>Sends one BACK press. <para>发送一次返回键按压。</para></summary>
    public void SendBack() => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlBackKey));

    /// <summary>Sends one HOME press. <para>发送一次主页键按压。</para></summary>
    public void SendHome() => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlHomeKey));

    /// <summary>Sends one POWER press, which toggles the device screen.
    /// <para>发送一次电源键按压，用于切换设备屏幕状态。</para></summary>
    public void SendPowerKey() => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlPowerKey));

    /// <summary>Sends one volume key press. <para>发送一次音量键按压。</para></summary>
    /// <param name="up">Whether to raise rather than lower the volume. <para>是否提高而非降低音量。</para></param>
    public void SendVolumeKey(bool up) => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(
        up ? OhscrcpyConstants.ControlVolumeUp : OhscrcpyConstants.ControlVolumeDown));

    /// <summary>Sends one touch event in video pixel coordinates.
    /// <para>以视频像素坐标发送一条触摸事件。</para>
    /// </summary>
    /// <param name="action">One of the touch sub-types. <para>触摸子类型之一。</para></param>
    /// <param name="x">Video pixel X. <para>视频像素 X。</para></param>
    /// <param name="y">Video pixel Y. <para>视频像素 Y。</para></param>
    /// <param name="pointerId">Pointer identifier. <para>指针标识。</para></param>
    public void SendTouch(byte action, int x, int y, int pointerId)
        => _client.SendPacket(OhscrcpyControlMessageWriter.Touch(action, x, y, pointerId));

    /// <summary>Sends UTF-8 text to the focused input field.
    /// <para>向焦点输入框发送 UTF-8 文本。</para></summary>
    /// <param name="text">Text to inject. <para>要注入的文本。</para></param>
    public void SendText(string text) => _client.SendPacket(OhscrcpyControlMessageWriter.Text(text));

    /// <summary>Sends one vertical scroll step. <para>发送一次纵向滚动。</para></summary>
    /// <param name="x">Video pixel X. <para>视频像素 X。</para></param>
    /// <param name="y">Video pixel Y. <para>视频像素 Y。</para></param>
    /// <param name="axisValue">Signed scroll amount. <para>带符号滚动量。</para></param>
    public void SendScroll(int x, int y, float axisValue)
        => _client.SendPacket(OhscrcpyControlMessageWriter.Scroll(x, y, axisValue));

    /// <summary>
    /// Toggles the device screen by sending the power key. The protocol exposes no absolute power state, so
    /// this session remembers the last state it requested and relies on the caller keeping in step.
    /// <para>通过发送电源键切换设备屏幕。协议不提供绝对电源状态，因此本会话记录上一次请求的状态，并要求
    /// 调用方保持一致。</para>
    /// </summary>
    public void ToggleDisplayPower()
    {
        _displayPowerOn = !_displayPowerOn;
        SendPowerKey();
    }

    /// <summary>Asks the device for its installable application list.
    /// <para>向设备请求可管理的应用列表。</para>
    /// </summary>
    public void RequestAppList()
        => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlListApps));

    /// <summary>Pauses or resumes encoder output for back-pressure.
    /// <para>为背压暂停或恢复编码输出。</para></summary>
    /// <param name="paused">Whether encoder output pauses. <para>是否暂停编码输出。</para></param>
    public void SetEncoderPaused(bool paused) => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(
        paused ? OhscrcpyConstants.ControlPauseEncoder : OhscrcpyConstants.ControlResumeEncoder));

    /// <summary>Rebuilds the device encoder and restarts the stream from a fresh key frame.
    /// <para>重建设备编码器并从新的关键帧重新开始码流。</para>
    /// </summary>
    public void RestartEncoder()
        => _client.SendPacket(OhscrcpyControlMessageWriter.Simple(OhscrcpyConstants.ControlRestartEncoder));

    /// <summary>Changes capture resolution, bitrate and frame rate.
    /// <para>改变采集分辨率、码率与帧率。</para>
    /// </summary>
    /// <param name="maxShortEdge">Maximum length of the shorter capture edge, in pixels.
    /// <para>采集短边上限（像素）。</para></param>
    /// <param name="bitrate">Target bitrate in bits per second. <para>目标码率（比特/秒）。</para></param>
    /// <param name="frameRate">Target frame rate. <para>目标帧率。</para></param>
    public void ChangeVideoParams(int maxShortEdge, int bitrate, int frameRate)
        => _client.SendPacket(OhscrcpyControlMessageWriter.ChangeVideoParams(maxShortEdge, bitrate, frameRate));

    /// <summary>Removes one device-status message that arrived while reading frames.
    /// <para>取出一条在读取帧期间到达的设备状态消息。</para>
    /// </summary>
    /// <param name="message">The oldest queued message, or <see langword="null"/> when none is pending.
    /// <para>队列中最旧的消息；无待处理消息时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a message was dequeued.
    /// <para>取出了消息时为 <see langword="true"/>。</para></returns>
    public bool TryDequeueDeviceMessage(out OhscrcpyDeviceMessage? message)
        => _client.TryDequeueDeviceMessage(out message);

    /// <summary>Removes one application list from the messages received so far.
    /// <para>从已收到的消息中取出一份应用列表。</para>
    /// </summary>
    /// <param name="entries">Parsed entries, or an empty list when no application list is pending.
    /// <para>已解析的条目；没有待处理应用列表时为空列表。</para></param>
    /// <returns><see langword="true"/> when an application list was dequeued.
    /// <para>取出了应用列表时为 <see langword="true"/>。</para></returns>
    public bool TryDequeueAppList(out IReadOnlyList<OhscrcpyAppEntry> entries)
    {
        while (_client.TryDequeueDeviceMessage(out var message))
        {
            if (message is { SubType: OhscrcpyConstants.DeviceStatusAppList })
            {
                entries = OhscrcpyPayload.ParseAppList(message.Payload);
                return true;
            }
        }

        entries = [];
        return false;
    }

    /// <summary>
    /// Releases the connection, the tunnel, the HDC session and the device lease in order; safe to call
    /// more than once. <para>按顺序释放连接、通道、HDC 会话与设备租约；可安全地重复调用。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _client.Dispose();
        }
        finally
        {
            try
            {
                _tunnel?.Dispose();
            }
            finally
            {
                try
                {
                    _hdcSession?.Dispose();
                }
                finally
                {
                    if (_lease is not null)
                    {
                        _lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
            }
        }
    }
}