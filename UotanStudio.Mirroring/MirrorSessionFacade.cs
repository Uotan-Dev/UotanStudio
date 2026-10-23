using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Tcp;

namespace UotanStudio.Mirroring;

/// <summary>
/// Presents one mirror session to the editor regardless of whether it runs the scrcpy server or the pushed
/// ohscrcpy UI Test extension, so the view model keeps one connection type.
/// <para>把一条投屏会话统一呈现给编辑器，无论它运行 scrcpy 服务端还是推送的 ohscrcpy UI Test 扩展，视图模型
/// 只需一种连接类型。</para>
/// </summary>
/// <remarks>
/// <para>
/// Both paths deliver <see cref="ScrcpyVideoFrame"/> access units with the same flag bits, so the decode
/// and GPU presentation loops are shared. Control messages stay in the scrcpy encoding at this boundary:
/// the ohscrcpy path translates them with <see cref="ScrcpyToOhscrcpyControlAdapter"/>, and messages without
/// a UI Test equivalent (clipboard, UHID) are ignored, which is why clipboard features stay disabled for
/// HDC targets.
/// </para>
/// <para>
/// 两条路径都以相同标志位交付 <see cref="ScrcpyVideoFrame"/> 访问单元，因此解码与 GPU 呈现循环共用。控制消息在
/// 本边界保持 scrcpy 编码：ohscrcpy 路径用 <see cref="ScrcpyToOhscrcpyControlAdapter"/> 翻译，没有 UI Test
/// 对应物的消息（剪贴板、UHID）会被忽略，因此 HDC 目标的剪贴板功能保持关闭。
/// </para>
/// </remarks>
public sealed class MirrorSessionFacade : IDisposable
{
    private readonly ManagedMirrorSession? _scrcpy;
    private readonly ManagedOhscrcpySession? _ohscrcpy;
    private int _disposed;

    private MirrorSessionFacade(ManagedMirrorSession session)
    {
        _scrcpy = session;
    }

    private MirrorSessionFacade(ManagedOhscrcpySession session)
    {
        _ohscrcpy = session;
    }

    /// <summary>Wraps an ADB scrcpy session. <para>包装 ADB scrcpy 会话。</para></summary>
    /// <param name="session">Connected or connectable scrcpy session.
    /// <para>已连接或可连接的 scrcpy 会话。</para></param>
    /// <returns>The facade delegating to <paramref name="session"/>.
    /// <para>委托给 <paramref name="session"/> 的门面。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    public static MirrorSessionFacade FromScrcpy(ManagedMirrorSession session)
        => new(session ?? throw new ArgumentNullException(nameof(session)));

    /// <summary>Wraps an HDC ohscrcpy session. <para>包装 HDC ohscrcpy 会话。</para></summary>
    /// <param name="session">Connected or connectable ohscrcpy session.
    /// <para>已连接或可连接的 ohscrcpy 会话。</para></param>
    /// <returns>The facade delegating to <paramref name="session"/>.
    /// <para>委托给 <paramref name="session"/> 的门面。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    public static MirrorSessionFacade FromOhscrcpy(ManagedOhscrcpySession session)
        => new(session ?? throw new ArgumentNullException(nameof(session)));

    /// <summary>
    /// Pushes the scrcpy server jar through an already-running ADB server and starts a mirror session over it.
    /// <para>经已运行的 ADB 服务端推送 scrcpy 服务端 jar，并在其上启动投屏会话。</para>
    /// </summary>
    /// <param name="server">ADB server services bound to the target serial.
    /// <para>绑定目标序列号的 ADB 服务端服务。</para></param>
    /// <param name="jarPath">Local path of the bundled <c>scrcpy-server</c> jar.
    /// <para>随附 <c>scrcpy-server</c> jar 的本地路径。</para></param>
    /// <param name="options">Server launch options; also selects which sockets the client opens.
    /// <para>服务端启动选项；同时决定客户端打开哪些 socket。</para></param>
    /// <param name="logSink">Optional sink receiving decoded server log text; may be called from a background thread.
    /// <para>可选的接收端，用于接收解码后的服务端日志文本；可能由后台线程调用。</para></param>
    /// <returns>The session owning the shell stream and the server-backed client; call
    /// <see cref="ManagedMirrorSession.Connect"/> next.
    /// <para>持有 shell 流与 server 客户端的会话；随后调用 <see cref="ManagedMirrorSession.Connect"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="server"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="jarPath"/> is empty.
    /// <para><paramref name="jarPath"/> 为空。</para></exception>
    /// <exception cref="IOException">The push or the shell open failed.
    /// <para>推送或 shell 打开失败。</para></exception>
    public static ManagedMirrorSession StartScrcpyOverAdbServer(AdbServerServices server, string jarPath,
        ScrcpyServerOptions options, Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);
        ArgumentNullException.ThrowIfNull(options);
        server.Push(jarPath, ScrcpyConstants.ServerDevicePath);
        var shell = server.Open("shell:" + options.BuildShellCommand());
        ScrcpyClient? client = null;
        try
        {
            client = new ScrcpyClient(server, options);
            return new ManagedMirrorSession(client, shell, null, null, logSink);
        }
        catch
        {
            client?.Dispose();
            shell.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pushes the scrcpy server jar over a native USB ADB session, starts the server and returns the owning session.
    /// <para>经原生 USB ADB 会话推送 scrcpy 服务端 jar，启动服务端并返回持有资源的会话。</para>
    /// </summary>
    /// <param name="session">Connected or connectable native USB ADB session; it is opened when not connected and
    /// disposed with the returned session.
    /// <para>已连接或可连接的原生 USB ADB 会话；未连接时会被打开，并随返回的会话一同释放。</para></param>
    /// <param name="jarPath">Local path of the bundled <c>scrcpy-server</c> jar.
    /// <para>随附 <c>scrcpy-server</c> jar 的本地路径。</para></param>
    /// <param name="options">Server launch options; also selects which sockets the client opens.
    /// <para>服务端启动选项；同时决定客户端打开哪些 socket。</para></param>
    /// <param name="lease">Optional device lease released after the ADB session.
    /// <para>可选的设备租约，在 ADB 会话之后释放。</para></param>
    /// <param name="logSink">Optional sink receiving decoded server log text; may be called from a background thread.
    /// <para>可选的接收端，用于接收解码后的服务端日志文本；可能由后台线程调用。</para></param>
    /// <returns>The unconnected session holding the client, the shell stream, the ADB session and the lease; call
    /// <see cref="ManagedMirrorSession.Connect"/> next.
    /// <para>持有客户端、shell 流、ADB 会话与租约的未连接会话；随后调用
    /// <see cref="ManagedMirrorSession.Connect"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="options"/> is
    /// <see langword="null"/>. <para><paramref name="session"/> 或 <paramref name="options"/> 为
    /// <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="jarPath"/> is empty, or <paramref name="session"/> is
    /// managed by an ADB server. <para><paramref name="jarPath"/> 为空，或 <paramref name="session"/> 由 ADB
    /// 服务端管理。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="jarPath"/> does not exist.
    /// <para><paramref name="jarPath"/> 不存在。</para></exception>
    /// <exception cref="IOException">The push, the shell open or the server launch failed.
    /// <para>推送、shell 打开或服务端启动失败。</para></exception>
    public static ManagedMirrorSession StartScrcpyOverUsbSession(IAdbDeviceSession session, string jarPath,
        ScrcpyServerOptions options, DeviceLease? lease = null, Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);
        ArgumentNullException.ThrowIfNull(options);
        if (session.UsesServer)
        {
            throw new ArgumentException(
                "A native USB ADB session is required. 需要原生 USB ADB 会话。", nameof(session));
        }

        if (!session.IsConnected)
        {
            session.Connect();
        }

        ScrcpyServerManager.PushServerAsync(session, jarPath).ConfigureAwait(false).GetAwaiter().GetResult();
        var shell = ScrcpyServerManager.StartServer(session, options).AsServiceStream();
        ScrcpyClient? client = null;
        try
        {
            client = new ScrcpyClient(session, options);
            return new ManagedMirrorSession(client, shell, session, lease, logSink);
        }
        catch
        {
            client?.Dispose();
            shell.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prepares the pushed UI Test capture extension on an HDC target, registers the loopback forwards and
    /// returns the session.
    /// <para>在 HDC 目标上准备推送的 UI Test 采集扩展，登记回环转发并返回会话。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device side is a capture library pushed to <c>/data/local/tmp</c> and loaded as a UI Test extension,
    /// not an installed package. The step order is fixed: probe the UI Test version and architecture, ensure the
    /// control daemon, start the video extension, map one loopback forward to the gRPC video endpoint and another
    /// to the UI Test JSON control socket, then create the gRPC transport, the control channel and the client.
    /// A direct session and a server-backed session follow the same order: file pushes use the session sync client
    /// and the forwards use <see cref="HdcForwardTunnel"/>, which falls back to the server <c>fport</c> command
    /// when the session exposes no native forward manager.
    /// </para>
    /// <para>
    /// 设备端是推送到 <c>/data/local/tmp</c> 并作为 UI Test 扩展加载的采集库，而不是已安装包。步骤顺序固定：
    /// 探测 UI Test 版本与架构、确保控制守护进程、启动视频扩展、把一个回环转发映射到 gRPC 视频端点、另一个
    /// 映射到 UI Test JSON 控制 socket，然后创建 gRPC 传输、控制通道与客户端。直连会话与 server 会话遵循同一
    /// 顺序：文件推送使用会话的 sync 客户端，转发使用 <see cref="HdcForwardTunnel"/>；会话没有原生转发管理器时
    /// 由它回退到 server 的 <c>fport</c> 命令。
    /// </para>
    /// </remarks>
    /// <param name="session">Connected or connectable HDC session, direct or server-backed; opened when not
    /// connected and disposed with the returned session. <para>已连接或可连接的 HDC 会话，可以是直连或 server
    /// 会话；未连接时会被打开，并随返回的会话一同释放。</para></param>
    /// <param name="deviceSerial">Target connect key used for log text and the forward registrations.
    /// <para>用于日志文本与转发登记的目标连接键。</para></param>
    /// <param name="options">Capture parameters; <see langword="null"/> uses the
    /// <see cref="OhscrcpyLaunchOptions"/> defaults. <para>采集参数；为 <see langword="null"/> 时使用
    /// <see cref="OhscrcpyLaunchOptions"/> 的默认值。</para></param>
    /// <param name="localForwardPort">Preferred loopback port for the video forward; zero picks a free port.
    /// <para>视频转发的首选回环端口；为零时自动选择空闲端口。</para></param>
    /// <param name="connectTimeoutMs">Budget for each forward registration and the loopback connect.
    /// <para>每次转发登记与回环连接的总预算。</para></param>
    /// <param name="lease">Optional device lease released after the HDC session.
    /// <para>可选的设备租约，在 HDC 会话之后释放。</para></param>
    /// <param name="logSink">Optional sink receiving probe, daemon and push diagnostics; may be called from a
    /// background thread. <para>可选的接收端，用于接收探测、守护进程与推送诊断；可能由后台线程调用。</para></param>
    /// <returns>The unconnected session; call <see cref="ManagedOhscrcpySession.Connect"/> next.
    /// <para>尚未连接的会话；随后调用 <see cref="ManagedOhscrcpySession.Connect"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="deviceSerial"/> is empty, or the session is not an
    /// <see cref="HdcSession"/>. <para><paramref name="deviceSerial"/> 为空，或会话不是
    /// <see cref="HdcSession"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localForwardPort"/> is outside 0..65535, or
    /// <paramref name="options"/> carries an out-of-range capture parameter.
    /// <para><paramref name="localForwardPort"/> 不在 0..65535 内，或 <paramref name="options"/> 携带越界的采集参数。</para></exception>
    /// <exception cref="FileNotFoundException">No pushed capture library or control agent matching the probed
    /// device exists in the bundled <c>ohos/device</c> directory.
    /// <para>随包分发的 <c>ohos/device</c> 目录中没有与已探测设备匹配的采集库或控制代理。</para></exception>
    /// <exception cref="IOException">The probe, the extension push, a daemon start or a forward registration
    /// failed; daemons started so far are stopped again before the exception is rethrown.
    /// <para>探测、扩展推送、守护进程启动或转发登记失败；抛出前会停止已经启动的守护进程。</para></exception>
    /// <exception cref="NotSupportedException">UI Test is missing or too old, or the session exposes no file
    /// transfer channel. <para>UI Test 缺失或版本过低，或会话没有可用的文件传输通道。</para></exception>
    public static ManagedOhscrcpySession StartOhscrcpyOverHdcSession(IHdcDeviceSession session,
        string deviceSerial, OhscrcpyLaunchOptions? options = null, int localForwardPort = 0,
        int connectTimeoutMs = 20_000, DeviceLease? lease = null, Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        ArgumentOutOfRangeException.ThrowIfNegative(localForwardPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localForwardPort, 65535);
        var launchOptions = options ?? new OhscrcpyLaunchOptions();
        launchOptions.Validate();
        if (!session.IsConnected)
        {
            session.Connect();
        }

        if (session is not HdcSession hdcSession)
        {
            throw new ArgumentException(
                "An HDC protocol session is required. 需要 HDC 协议会话。",
                nameof(session));
        }

        var device = OhscrcpyServerManager.Probe(hdcSession);
        var controlDaemon = OhscrcpyServerManager.EnsureControlDaemon(hdcSession, device, logSink);
        VideoDaemonHandle? videoDaemon = null;
        HdcForwardTunnel? videoTunnel = null;
        HdcForwardTunnel? controlTunnel = null;
        OhoscrcpyGrpcTransport? transport = null;
        IOhscrcpyControlChannel? control = null;
        OhscrcpyClient? client = null;
        try
        {
            videoDaemon = OhscrcpyServerManager.StartVideoDaemon(hdcSession, device, launchOptions, logSink);
            videoTunnel = new HdcForwardTunnel(
                hdcSession, deviceSerial, device.VideoEndpoint, localForwardPort, connectTimeoutMs);
            var videoPort = videoTunnel.Map();
            controlTunnel = new HdcForwardTunnel(
                hdcSession, deviceSerial, device.ControlEndpoint, 0, connectTimeoutMs);
            transport = new OhoscrcpyGrpcTransport(videoPort, videoTunnel);
            control = new OhscrcpyControlChannel(hdcSession, deviceSerial, device, controlTunnel, logSink);
            client = new OhscrcpyClient(transport, control, new OhscrcpyClientOptions(deviceSerial))
            {
                Log = logSink,
            };
            return new ManagedOhscrcpySession(client, hdcSession, videoDaemon, controlDaemon, lease, logSink);
        }
        catch
        {
            if (client is not null)
            {
                CleanupStep(client.Dispose);
            }
            else
            {
                if (control is not null)
                {
                    CleanupStep(control.Dispose);
                }

                if (transport is not null)
                {
                    CleanupStep(transport.Dispose);
                }
            }

            if (control is null && controlTunnel is not null)
            {
                CleanupStep(controlTunnel.Dispose);
            }

            if (videoTunnel is not null)
            {
                CleanupStep(videoTunnel.Dispose);
            }

            if (videoDaemon is not null)
            {
                var daemon = videoDaemon;
                CleanupStep(() => OhscrcpyServerManager.StopVideoDaemon(hdcSession, daemon, logSink));
            }

            CleanupStep(() => OhscrcpyServerManager.StopControlDaemon(hdcSession, controlDaemon, logSink));
            throw;
        }
    }

    private static void CleanupStep(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>Whether the session runs the HDC ohscrcpy path.
    /// <para>会话是否运行 HDC ohscrcpy 路径。</para></summary>
    /// <value><see langword="true"/> for ohscrcpy, <see langword="false"/> for scrcpy.
    /// <para>ohscrcpy 为 <see langword="true"/>，scrcpy 为 <see langword="false"/>。</para></value>
    public bool IsHdc => _ohscrcpy is not null;

    /// <summary>Device name reported to the editor. <para>报告给编辑器的设备名。</para></summary>
    /// <value>The name from the active session. <para>活动会话给出的名称。</para></value>
    public string DeviceName => _scrcpy?.DeviceName ?? _ohscrcpy!.DeviceName;

    /// <summary>Video stream metadata. <para>视频流元数据。</para></summary>
    /// <value>The codec and size, or <see langword="null"/> before the first configuration.
    /// <para>编码与尺寸；首个配置之前为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata => _scrcpy?.VideoMetadata ?? _ohscrcpy?.VideoMetadata;

    /// <summary>Audio codec metadata; always <see langword="null"/> on the ohscrcpy path.
    /// <para>音频编码元数据；ohscrcpy 路径始终为 <see langword="null"/>。</para></summary>
    /// <value>The scrcpy audio header, or <see langword="null"/>.
    /// <para>scrcpy 音频头部，或 <see langword="null"/>。</para></value>
    public ScrcpyAudioMetadata? AudioMetadata => _scrcpy?.AudioMetadata;

    /// <summary>Whether scrcpy refused the audio stream during the handshake.
    /// <para>scrcpy 是否在握手期间拒绝了音频流。</para></summary>
    /// <value><see langword="false"/> on the ohscrcpy path.
    /// <para>ohscrcpy 路径为 <see langword="false"/>。</para></value>
    public bool AudioDisabledByServer => _scrcpy?.AudioDisabledByServer ?? false;

    /// <summary>Whether control messages can be written. <para>是否可以写入控制消息。</para></summary>
    /// <value><see langword="true"/> while the active session holds its control channel.
    /// <para>活动会话持有控制通道时为 <see langword="true"/>。</para></value>
    public bool IsControlConnected => _scrcpy?.IsControlConnected ?? _ohscrcpy!.IsControlConnected;

    /// <summary>Opens the transport and completes the handshake. <para>打开传输并完成握手。</para></summary>
    /// <exception cref="InvalidDataException">The handshake failed. <para>握手失败。</para></exception>
    public void Connect()
    {
        if (_scrcpy is not null)
        {
            _scrcpy.Connect();
            return;
        }

        _ohscrcpy!.Connect();
    }

    /// <summary>Reads one framed video packet. <para>读取一个分帧视频包。</para></summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame the caller must dispose, or <see langword="null"/> on timeout.
    /// <para>由调用方释放的帧；超时时为 <see langword="null"/>。</para></returns>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs)
        => _scrcpy?.ReadFrame(timeoutMs) ?? _ohscrcpy!.ReadFrame(timeoutMs);

    /// <summary>Reads one audio packet; always <see langword="null"/> on the ohscrcpy path.
    /// <para>读取一个音频包；ohscrcpy 路径始终为 <see langword="null"/>。</para></summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The audio packet, or <see langword="null"/>. <para>音频包，或 <see langword="null"/>。</para></returns>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs) => _scrcpy?.ReadAudioFrame(timeoutMs);

    /// <summary>Reads one device → client message. <para>读取一条设备到客户端消息。</para></summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The scrcpy device message, or <see langword="null"/> when none arrived or the path is ohscrcpy.
    /// <para>scrcpy 设备消息；无消息或路径为 ohscrcpy 时为 <see langword="null"/>。</para></returns>
    public ScrcpyDeviceMessage? ReadDeviceMessage(int timeoutMs = 0) => _scrcpy?.ReadDeviceMessage(timeoutMs);

    /// <summary>
    /// Sends one scrcpy control message. The ohscrcpy path translates it and silently drops messages without
    /// an equivalent. <para>发送一条 scrcpy 控制消息。ohscrcpy 路径会翻译它，并静默丢弃没有对应物的消息。</para>
    /// </summary>
    /// <param name="message">A message from <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 生成的消息。</para></param>
    public void SendControl(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_scrcpy is not null)
        {
            _scrcpy.SendControl(message);
            return;
        }

        _ = _ohscrcpy!.SendControl(message);
    }

    /// <summary>Sends one BACK press. <para>发送一次返回键按压。</para></summary>
    public void SendBack()
    {
        if (_scrcpy is not null) _scrcpy.SendBack();
        else _ohscrcpy!.SendBack();
    }

    /// <summary>Toggles the device screen power. <para>切换设备屏幕电源状态。</para></summary>
    public void ToggleDisplayPower()
    {
        if (_scrcpy is not null) _scrcpy.ToggleDisplayPower();
        else _ohscrcpy!.ToggleDisplayPower();
    }

    /// <summary>
    /// Clipboard writes are unavailable on the ohscrcpy path and are ignored there.
    /// <para>ohscrcpy 路径不支持剪贴板写入，在该路径上会被忽略。</para>
    /// </summary>
    /// <param name="text">Clipboard text. <para>剪贴板文本。</para></param>
    /// <param name="paste">Whether the device also pastes. <para>设备是否同时粘贴。</para></param>
    /// <param name="sequence">Request sequence. <para>请求序号。</para></param>
    public void SendClipboard(string text, bool paste, ulong sequence = 0)
        => _scrcpy?.SendClipboard(text, paste, sequence);

    /// <summary>
    /// Clipboard reads are unavailable on the ohscrcpy path and are ignored there.
    /// <para>ohscrcpy 路径不支持剪贴板读取，在该路径上会被忽略。</para>
    /// </summary>
    /// <param name="copyKey">Copy or cut key. <para>复制或剪切键。</para></param>
    public void RequestClipboard(byte copyKey = ScrcpyConstants.CopyKeyCopy)
        => _scrcpy?.RequestClipboard(copyKey);

    /// <summary>Releases the active session. <para>释放活动会话。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _scrcpy?.Dispose();
        _ohscrcpy?.Dispose();
    }
}