using UotanStudio.Comm.Contracts;

namespace UotanStudio.Mirroring;

/// <summary>
/// Owns one ohscrcpy mirror session: the client, the pushed video daemon, the UI Test control daemon, the HDC
/// session behind the forwards and the device lease, in that release order.
/// <para>持有一条 ohscrcpy 投屏会话：客户端、推送的视频守护进程、UI Test 控制守护进程、承载转发的 HDC 会话
/// 以及设备租约，并按该顺序释放。</para>
/// </summary>
/// <remarks>
/// The device side is a pushed UI Test extension rather than an installed package: the video daemon serves H.264
/// over gRPC and the control daemon owns the UI Test JSON socket. Frames are delivered as
/// <see cref="ScrcpyVideoFrame"/> with the same flag bits the scrcpy client uses, which lets the host decode both
/// device families with one GPU pipeline. Disposal stops only the processes this session started.
/// <para>设备端是推送的 UI Test 扩展而不是已安装包：视频守护进程经 gRPC 提供 H.264，控制守护进程持有 UI Test JSON
/// socket。帧以 <see cref="ScrcpyVideoFrame"/> 交付，标志位与 scrcpy 客户端一致，使宿主用同一套 GPU 管线解码
/// 两类设备。释放时只停止本会话启动的进程。</para>
/// </remarks>
public sealed class ManagedOhscrcpySession : IDisposable
{
    private readonly OhscrcpyClient _client;
    private readonly IHdcDeviceSession _hdcSession;
    private readonly VideoDaemonHandle _videoDaemon;
    private readonly ControlDaemonHandle _controlDaemon;
    private readonly IAsyncDisposable? _lease;
    private readonly Action<string>? _logSink;
    private int _disposed;

    /// <summary>Creates a session over an already-connected client and every resource behind it.
    /// <para>基于已连接的客户端及其背后的全部资源创建会话。</para>
    /// </summary>
    /// <param name="client">Ohscrcpy client owning the video transport and control channel.
    /// <para>持有视频传输与控制通道的 ohscrcpy 客户端。</para></param>
    /// <param name="hdcSession">HDC session that carries the loopback forwards, direct or server-backed,
    /// disposed with the session. <para>承载回环转发的 HDC 会话，可以是直连或 server 会话，随会话一同释放。</para></param>
    /// <param name="videoDaemon">Pushed capture extension started by this session.
    /// <para>本会话启动的推送采集扩展。</para></param>
    /// <param name="controlDaemon">UI Test control daemon ensured by this session.
    /// <para>本会话确保的 UI Test 控制守护进程。</para></param>
    /// <param name="lease">Device lease released after the HDC session.
    /// <para>在 HDC 会话之后释放的设备租约。</para></param>
    /// <param name="logSink">Optional diagnostics sink; may be called from the thread that disposes the session.
    /// <para>可选的诊断接收端；可能由释放会话的线程调用。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.
    /// <para><paramref name="client"/> 为 <see langword="null"/>。</para></exception>
    internal ManagedOhscrcpySession(OhscrcpyClient client, IHdcDeviceSession hdcSession,
        VideoDaemonHandle videoDaemon, ControlDaemonHandle controlDaemon, IAsyncDisposable? lease,
        Action<string>? logSink)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _hdcSession = hdcSession;
        _videoDaemon = videoDaemon;
        _controlDaemon = controlDaemon;
        _lease = lease;
        _logSink = logSink;
    }

    /// <summary>Device name reported to the mirror UI. <para>报告给投屏界面的设备名。</para></summary>
    /// <value>The serial or alias supplied at construction. <para>构造时提供的序列号或别名。</para></value>
    public string DeviceName => _client.DeviceName;

    /// <summary>Video metadata derived from the first access unit.
    /// <para>由首个访问单元推导的视频元数据。</para></summary>
    /// <value>The codec and size, or <see langword="null"/> before the connection.
    /// <para>编码与尺寸；连接之前为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata => _client.VideoMetadata;

    /// <summary>H.264 parameter sets derived from the first access unit.
    /// <para>由首个访问单元推导的 H.264 参数集。</para></summary>
    /// <value>The configuration, or <see langword="null"/> before the connection.
    /// <para>视频配置；连接之前为 <see langword="null"/>。</para></value>
    public OhscrcpyVideoConfig? VideoConfig => _client.VideoConfig;

    /// <summary>Number of stream configurations applied since construction.
    /// <para>自构造以来已应用的码流配置数量。</para></summary>
    /// <value>Starts at zero and increases per configuration.
    /// <para>从零开始，每个配置加一。</para></value>
    public int ConfigEpoch => _client.ConfigEpoch;

    /// <summary>Whether input calls can reach the device. <para>输入调用是否能到达设备。</para></summary>
    /// <value><see langword="true"/> while the UI Test control channel is connected.
    /// <para>UI Test 控制通道已连接时为 <see langword="true"/>。</para></value>
    public bool IsControlConnected => _client.IsControlConnected;

    /// <summary>Opens the control channel and the video stream and waits for the first access unit.
    /// <para>打开控制通道与视频流，并等待首个访问单元。</para>
    /// </summary>
    /// <param name="timeoutMs">Overall budget in milliseconds; zero or a negative value uses the client option.
    /// <para>总预算（毫秒）；零或负值使用客户端选项。</para></param>
    public void Connect(int timeoutMs = 0) => _client.Connect(timeoutMs);

    /// <summary>Reads the next video frame or codec-configuration packet.
    /// <para>读取下一个视频帧或编码配置包。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame the caller must dispose, or <see langword="null"/> on timeout.
    /// <para>由调用方释放的帧；超时时为 <see langword="null"/>。</para></returns>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs) => _client.ReadFrame(timeoutMs);

    /// <summary>Translates and sends a scrcpy control message through the UI Test channel.
    /// <para>经 UI Test 通道翻译并发送一条 scrcpy 控制消息。</para>
    /// </summary>
    /// <param name="message">Message from <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 生成的消息。</para></param>
    /// <returns><see langword="true"/> when the message was mapped and dispatched.
    /// <para>消息已映射并派发时为 <see langword="true"/>。</para></returns>
    public bool SendControl(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _client.SendScrcpyControl(message);
    }

    /// <summary>Sends one BACK press. <para>发送一次返回键按压。</para></summary>
    public void SendBack() => _client.SendBack();

    /// <summary>Sends one HOME press. <para>发送一次主页键按压。</para></summary>
    public void SendHome() => _client.SendHome();

    /// <summary>Sends one POWER press, which toggles the device screen.
    /// <para>发送一次电源键按压，用于切换设备屏幕状态。</para></summary>
    public void SendPowerKey() => _client.SendPowerKey();

    /// <summary>Sends one volume key press. <para>发送一次音量键按压。</para></summary>
    /// <param name="up">Whether to raise rather than lower the volume. <para>是否提高而非降低音量。</para></param>
    public void SendVolumeKey(bool up) => _client.SendVolumeKey(up);

    /// <summary>Toggles the device screen by sending the power key.
    /// <para>通过发送电源键切换设备屏幕。</para></summary>
    public void ToggleDisplayPower() => _client.ToggleDisplayPower();

    /// <summary>Asks the device extension for a new IDR frame.
    /// <para>请求设备扩展产生新的 IDR 帧。</para></summary>
    public void RequestKeyFrame() => _client.RequestKeyFrame();

    /// <summary>
    /// Releases the client, the video daemon, the control daemon, the HDC session and the device lease in order;
    /// safe to call more than once and never throws, so a failing step cannot skip the later ones.
    /// <para>按顺序释放客户端、视频守护进程、控制守护进程、HDC 会话与设备租约；可安全地重复调用且不抛异常，
    /// 单个步骤失败不会跳过后续步骤。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TryCleanup(_client.Dispose, "client");
        TryCleanup(
            () => OhscrcpyServerManager.StopVideoDaemon(_hdcSession, _videoDaemon, _logSink),
            "video daemon");
        TryCleanup(
            () => OhscrcpyServerManager.StopControlDaemon(_hdcSession, _controlDaemon, _logSink),
            "control daemon");
        TryCleanup(_hdcSession.Dispose, "HDC session");
        var lease = _lease;
        if (lease is not null)
        {
            TryCleanup(() => lease.DisposeAsync().AsTask().GetAwaiter().GetResult(), "device lease");
        }
    }

    private void TryCleanup(Action cleanup, string step)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                _logSink?.Invoke($"Ohscrcpy session cleanup failed for the {step}: {ex.Message}");
            }
            catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
            {
            }
        }
    }
}