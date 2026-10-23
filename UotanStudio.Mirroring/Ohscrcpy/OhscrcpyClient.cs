using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace UotanStudio.Mirroring;

/// <summary>Options for one ohscrcpy connection. <para>单条 ohscrcpy 连接的选项。</para></summary>
/// <param name="DeviceName">Name shown by the mirror UI; the device protocol announces none.
/// <para>投屏界面显示的名称；设备协议本身不宣告名称。</para></param>
/// <param name="ConnectTimeoutMs">Default budget for the control handshake and the first video access unit, in
/// milliseconds. <para>控制握手与首个视频访问单元的默认预算（毫秒）。</para></param>
public sealed record OhscrcpyClientOptions(
    string DeviceName = "",
    int ConnectTimeoutMs = OhscrcpyConstants.VideoConnectTimeoutMs);

/// <summary>
/// ohscrcpy client: consumes the H.264 access units the pushed UI Test extension serves over gRPC/HTTP/2 and
/// translates scrcpy control messages into the UI Test input calls.
/// <para>ohscrcpy 客户端：消费推送的 UI Test 扩展经 gRPC/HTTP/2 提供的 H.264 访问单元，并把 scrcpy 控制消息
/// 翻译为 UI Test 输入调用。</para>
/// </summary>
/// <remarks>
/// <para>
/// The device side is not a scrcpy-compatible stream. A capture library runs as a UI Test extension and publishes
/// a standard gRPC server stream whose <c>payload["data"]</c> carries Annex-B H.264; there is no separate
/// codec-configuration message, so the parameter sets and geometry are derived from the first access unit.
/// Frames are handed to <see cref="ReadFrame"/> as <see cref="ScrcpyVideoFrame"/> values with the same flag bits
/// the scrcpy client uses, so the GPU decode pipeline needs no second implementation.
/// </para>
/// <para>
/// 设备端不是 scrcpy 兼容流。采集库作为 UI Test 扩展运行并发布标准 gRPC 服务端流，其 <c>payload["data"]</c>
/// 承载 Annex-B H.264；没有独立的编码配置消息，因此参数集与几何从首个访问单元推导。帧以
/// <see cref="ScrcpyVideoFrame"/> 交给 <see cref="ReadFrame"/>，标志位与 scrcpy 客户端一致，使 GPU 解码管线
/// 无需第二套实现。
/// </para>
/// <para>
/// One background reader pumps the gRPC response into a bounded channel, so exactly one thread may call
/// <see cref="ReadFrame"/>, which stays synchronous for the host decode loop. The control channel is optional and
/// its connect failure only disables input, never video.
/// </para>
/// <para>
/// 一个后台读取任务把 gRPC 响应泵入有界通道，因此只能有一个线程调用 <see cref="ReadFrame"/>；该方法保持同步以
/// 适配宿主解码循环。控制通道是可选的，其连接失败只会禁用输入，不影响视频。
/// </para>
/// </remarks>
internal sealed class OhscrcpyClient : IDisposable
{
    private const int PayloadCapacity = 8;

    private static readonly uint _h264FourCc = BinaryPrimitives.ReadUInt32BigEndian("H264"u8);

    private readonly IOhoscrcpyVideoTransport _video;
    private readonly IOhscrcpyControlChannel? _control;
    private readonly OhscrcpyClientOptions _options;
    private readonly Channel<byte[]> _payloads = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(PayloadCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly long _timestampOriginMs = Environment.TickCount64;

    private Stream? _stream;
    private Task? _reader;
    private Exception? _readerFailure;
    private byte[]? _pendingPayload;
    private long _lastTimestamp;
    private bool _emitSyntheticConfig;
    private bool _controlUnavailable;
    private bool _displayPowerOn = true;
    private int _disposed;

    /// <summary>Creates a client over the device video transport and the optional UI Test control channel.
    /// <para>基于设备视频传输与可选的 UI Test 控制通道创建客户端。</para>
    /// </summary>
    /// <param name="video">gRPC video transport bound to a loopback port Comm already forwarded.
    /// <para>绑定到 Comm 已转发回环端口的 gRPC 视频传输。</para></param>
    /// <param name="control">UI Test control channel, or <see langword="null"/> for a video-only client.
    /// <para>UI Test 控制通道；仅视频时为 <see langword="null"/>。</para></param>
    /// <param name="options">Connection options. <para>连接选项。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="video"/> or <paramref name="options"/> is
    /// <see langword="null"/>. <para><paramref name="video"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    internal OhscrcpyClient(IOhoscrcpyVideoTransport video, IOhscrcpyControlChannel? control,
        OhscrcpyClientOptions options)
    {
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _control = control;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Receives diagnostic text for non-fatal control failures; may be called from a background thread.
    /// <para>接收非致命控制失败的诊断文本；可能由后台线程调用。</para></summary>
    /// <value>The current sink, or <see langword="null"/> to discard diagnostics.
    /// <para>当前接收端；为 <see langword="null"/> 时丢弃诊断。</para></value>
    public Action<string>? Log { get; set; }

    /// <summary>Name shown by the mirror UI. <para>投屏界面显示的名称。</para></summary>
    /// <value>The configured device name. <para>配置的设备名。</para></value>
    public string DeviceName => _options.DeviceName;

    /// <summary>Whether the video stream is currently held. <para>当前是否持有视频流。</para></summary>
    /// <value><see langword="true"/> after a successful <see cref="Connect"/> and before disposal.
    /// <para>在成功的 <see cref="Connect"/> 之后、释放之前为 <see langword="true"/>。</para></value>
    public bool IsConnected => _stream is not null && Volatile.Read(ref _disposed) == 0;

    /// <summary>Whether input calls can reach the device. <para>输入调用是否能到达设备。</para></summary>
    /// <value><see langword="false"/> when control is missing, failed to connect or the client is disposed.
    /// <para>控制通道缺失、连接失败或客户端已释放时为 <see langword="false"/>。</para></value>
    public bool IsControlConnected
        => Volatile.Read(ref _disposed) == 0 && !_controlUnavailable && _control is { IsConnected: true };

    /// <summary>Video metadata derived from the first access unit. <para>由首个访问单元推导的视频元数据。</para></summary>
    /// <value>The H.264 codec and geometry, or <see langword="null"/> before <see cref="Connect"/>.
    /// <para>H.264 编码与几何；<see cref="Connect"/> 之前为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata { get; private set; }

    /// <summary>H.264 parameter sets derived from the first access unit.
    /// <para>由首个访问单元推导的 H.264 参数集。</para></summary>
    /// <value>The configuration, or <see langword="null"/> before <see cref="Connect"/>.
    /// <para>视频配置；<see cref="Connect"/> 之前为 <see langword="null"/>。</para></value>
    public OhscrcpyVideoConfig? VideoConfig { get; private set; }

    /// <summary>Number of stream configurations applied since construction; a consumer that compares it before
    /// and after a read detects a reconfiguration. <para>自构造以来已应用的码流配置数量；使用方在读取前后比较该值
    /// 即可检测重配置。</para></summary>
    /// <value>Starts at zero and increases once per configuration.
    /// <para>从零开始，每个配置加一。</para></value>
    public int ConfigEpoch { get; private set; }

    /// <summary>
    /// Opens the UI Test control channel and the gRPC video stream, then waits for the first H.264 access unit and
    /// derives the parameter sets from it.
    /// <para>打开 UI Test 控制通道与 gRPC 视频流，等待首个 H.264 访问单元并从中推导参数集。</para>
    /// </summary>
    /// <param name="timeoutMs">Overall budget for the control handshake and the first access unit; zero or a
    /// negative value uses <see cref="OhscrcpyClientOptions.ConnectTimeoutMs"/>.
    /// <para>控制握手与首个访问单元的总预算；零或负值使用 <see cref="OhscrcpyClientOptions.ConnectTimeoutMs"/>。</para></param>
    /// <exception cref="InvalidOperationException">The client is already connected.
    /// <para>客户端已连接。</para></exception>
    /// <exception cref="InvalidDataException">The stream produced no access unit within the budget, or its first
    /// access unit carried no usable geometry. <para>码流未在预算内产生访问单元，或首个访问单元没有可用几何。</para></exception>
    /// <exception cref="IOException">The device rejected the gRPC call or closed the stream during the wait.
    /// <para>设备拒绝 gRPC 调用或在等待期间关闭码流。</para></exception>
    public void Connect(int timeoutMs = 0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_stream is not null)
        {
            throw new InvalidOperationException(
                "The ohscrcpy client is already connected. ohscrcpy 客户端已连接。");
        }

        var budget = Math.Max(timeoutMs > 0 ? timeoutMs : _options.ConnectTimeoutMs, 1);
        var deadline = Environment.TickCount64 + budget;
        try
        {
            ConnectControl();
            var stream = _video.OpenVideoStream(_lifetime.Token);
            _stream = stream;
            _reader = Task.Run(() => ReadLoopAsync(stream, _lifetime.Token), CancellationToken.None);
            var first = WaitForFirstPayload(deadline, budget);
            ApplyStreamConfiguration(first);
            _pendingPayload = first;
        }
        catch
        {
            ResetAfterFailedConnect();
            throw;
        }
    }

    /// <summary>
    /// Returns the next video frame, emitting one synthetic codec-configuration frame first when the stream carried
    /// parameter sets. <para>返回下一个视频帧；码流携带参数集时先发出一个合成的编码配置帧。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame owned by the caller, which must dispose it after decoding, or <see langword="null"/>
    /// when no access unit arrived before the timeout.
    /// <para>由调用方持有并在解码后释放的帧；超时未收到访问单元时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">The client is not connected.
    /// <para>客户端尚未连接。</para></exception>
    /// <exception cref="IOException">The device closed the stream, or the background reader failed.
    /// <para>设备关闭码流，或后台读取任务失败。</para></exception>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs)
    {
        var config = VideoConfig
            ?? throw new InvalidOperationException("The ohscrcpy client is not connected. ohscrcpy 客户端尚未连接。");
        if (_emitSyntheticConfig)
        {
            _emitSyntheticConfig = false;
            return new ScrcpyVideoFrame((long)ScrcpyConstants.PacketFlagConfig, config.BuildAnnexBConfiguration());
        }

        byte[]? payload;
        if (_pendingPayload is not null)
        {
            payload = _pendingPayload;
            _pendingPayload = null;
        }
        else
        {
            payload = TryReadPayload(timeoutMs, out var closed);
            if (payload is null)
            {
                if (closed)
                {
                    ThrowStreamEnded();
                }

                return null;
            }
        }

        return BuildFrame(payload);
    }

    /// <summary>
    /// Translates one scrcpy control message and sends it through the UI Test channel.
    /// <para>翻译一条 scrcpy 控制消息并经 UI Test 通道发送。</para>
    /// </summary>
    /// <param name="message">A message produced by <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 产生的消息。</para></param>
    /// <returns><see langword="true"/> when the message has a UI Test equivalent and was dispatched; unsupported
    /// messages and an unavailable control channel return <see langword="false"/>.
    /// <para>消息存在 UI Test 对应物并已派发时为 <see langword="true"/>；不支持的消息与不可用的控制通道返回
    /// <see langword="false"/>。</para></returns>
    public bool SendScrcpyControl(ReadOnlySpan<byte> message)
    {
        if (!ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var action))
        {
            return false;
        }

        if (action.Kind == OhscrcpyControlActionKind.RequestKeyFrame)
        {
            RequestKeyFrame();
            return true;
        }

        if (!IsControlConnected)
        {
            return false;
        }

        switch (action.Kind)
        {
            case OhscrcpyControlActionKind.Gesture:
                _control!.SendGesture(action.Gesture, action.X, action.Y);
                return true;
            case OhscrcpyControlActionKind.ScrollUp:
                _control!.SendScroll(true, action.X, action.Y);
                return true;
            case OhscrcpyControlActionKind.ScrollDown:
                _control!.SendScroll(false, action.X, action.Y);
                return true;
            case OhscrcpyControlActionKind.KeyDown:
                _control!.SendKey(action.KeyCode, OhscrcpyKeyAction.Down);
                return true;
            case OhscrcpyControlActionKind.KeyUp:
                _control!.SendKey(action.KeyCode, OhscrcpyKeyAction.Up);
                return true;
            case OhscrcpyControlActionKind.KeyPress:
                _control!.SendKey(action.KeyCode, OhscrcpyKeyAction.Press);
                return true;
            case OhscrcpyControlActionKind.KeyChord:
                _control!.SendKeyChord(action.KeyCode, action.SecondaryKeyCode);
                return true;
            case OhscrcpyControlActionKind.Text:
                _control!.SendText(action.Text);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Sends one BACK press. <para>发送一次返回键按压。</para></summary>
    public void SendBack() => SendKeyPress(OhscrcpyConstants.KeyCodeBack);

    /// <summary>Sends one HOME press. <para>发送一次主页键按压。</para></summary>
    public void SendHome() => SendKeyPress(OhscrcpyConstants.KeyCodeHome);

    /// <summary>Sends one POWER press, which toggles the device screen.
    /// <para>发送一次电源键按压，用于切换设备屏幕状态。</para></summary>
    public void SendPowerKey() => SendKeyPress(OhscrcpyConstants.KeyCodePower);

    /// <summary>Sends one volume key press. <para>发送一次音量键按压。</para></summary>
    /// <param name="up">Whether to raise rather than lower the volume. <para>是否提高而非降低音量。</para></param>
    public void SendVolumeKey(bool up) => SendKeyPress(
        up ? OhscrcpyConstants.KeyCodeVolumeUp : OhscrcpyConstants.KeyCodeVolumeDown);

    /// <summary>
    /// Toggles the device screen by sending the power key. The control protocol exposes no absolute power state,
    /// so the client remembers the last state it requested. <para>通过发送电源键切换设备屏幕。控制协议不提供绝对
    /// 电源状态，因此客户端记录上一次请求的状态。</para>
    /// </summary>
    public void ToggleDisplayPower()
    {
        _displayPowerOn = !_displayPowerOn;
        SendPowerKey();
    }

    /// <summary>Asks the device extension for a new IDR frame. <para>请求设备扩展产生新的 IDR 帧。</para></summary>
    /// <exception cref="InvalidOperationException">The client is not connected.
    /// <para>客户端尚未连接。</para></exception>
    /// <exception cref="IOException">The device rejected the unary request.
    /// <para>设备拒绝该一元请求。</para></exception>
    public void RequestKeyFrame()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The ohscrcpy client is not connected. ohscrcpy 客户端尚未连接。");
        }

        _video.RequestKeyFrame(_lifetime.Token);
    }

    /// <summary>Releases the worker, the video transport, the control channel and the lifetime; safe to call more
    /// than once. <para>释放读取任务、视频传输、控制通道与生命周期；可安全地重复调用。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TryRun(_lifetime.Cancel);
        DisposeQuietly(Interlocked.Exchange(ref _stream, null));
        DisposeQuietly(_video);
        _payloads.Writer.TryComplete();
        WaitForReader();
        DisposeQuietly(_control);
        DisposeQuietly(_lifetime);
    }

    private void ConnectControl()
    {
        if (_control is null)
        {
            return;
        }

        try
        {
            _control.Connect();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _controlUnavailable = true;
            Log?.Invoke($"The UI Test control channel is unavailable; touch input is disabled: {ex.Message}");
        }
    }

    private byte[] WaitForFirstPayload(long deadline, int budgetMs)
    {
        var requestedKeyFrame = false;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break;
            }

            var slice = requestedKeyFrame ? remaining : Math.Max(remaining / 2, 1);
            var payload = TryReadPayload((int)Math.Min(slice, int.MaxValue), out var closed);
            if (payload is not null)
            {
                return payload;
            }

            if (closed)
            {
                ThrowStreamEnded();
            }

            if (requestedKeyFrame)
            {
                continue;
            }

            requestedKeyFrame = true;
            TryRequestKeyFrame();
        }

        throw new InvalidDataException(
            $"The device gRPC video stream produced no H.264 access unit within {budgetMs} ms. "
            + "设备 gRPC 视频流未在时限内产生 H.264 访问单元。");
    }

    private void TryRequestKeyFrame()
    {
        try
        {
            _video.RequestKeyFrame(_lifetime.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !_lifetime.IsCancellationRequested)
        {
            Log?.Invoke($"The device did not accept the key-frame request: {ex.Message}");
        }
    }

    private void ApplyStreamConfiguration(byte[] firstPayload)
    {
        var hasParameterSets = OhscrcpyProtocol.TryGetParameterSets(firstPayload, out var sps, out var pps);
        var width = 0;
        var height = 0;
        var parsed = hasParameterSets && OhscrcpyProtocol.TryParseSpsDimensions(sps, out width, out height);
        if (!parsed)
        {
            width = _control?.DisplayWidth ?? 0;
            height = _control?.DisplayHeight ?? 0;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(
                "The first device access unit carried no usable H.264 geometry and the UI Test channel reported "
                + "no display size. "
                + "首个设备访问单元没有可用的 H.264 几何，UI Test 通道也未报告显示尺寸。");
        }

        VideoConfig = new OhscrcpyVideoConfig(width, height, sps, pps);
        VideoMetadata = new ScrcpyVideoMetadata(_h264FourCc, width, height);
        ConfigEpoch++;
        _emitSyntheticConfig = hasParameterSets;
    }

    private ScrcpyVideoFrame BuildFrame(byte[] payload)
    {
        var timestamp = NextTimestamp();
        var flags = OhscrcpyProtocol.IsKeyFrame(payload) ? ScrcpyConstants.PacketFlagKeyFrame : 0UL;
        return new ScrcpyVideoFrame((long)((ulong)timestamp | flags), payload);
    }

    private long NextTimestamp()
    {
        var elapsed = (Environment.TickCount64 - _timestampOriginMs) * 1000L;
        var last = Volatile.Read(ref _lastTimestamp);
        var timestamp = elapsed > last ? elapsed : last + 1;
        Volatile.Write(ref _lastTimestamp, timestamp);
        return timestamp;
    }

    private byte[]? TryReadPayload(int timeoutMs, out bool closed)
    {
        closed = false;
        if (_payloads.Reader.TryRead(out var payload))
        {
            return payload;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);
        try
        {
            if (!_payloads.Reader.WaitToReadAsync(linked.Token).AsTask().GetAwaiter().GetResult())
            {
                closed = true;
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            closed = _lifetime.IsCancellationRequested;
            return null;
        }

        return _payloads.Reader.TryRead(out payload) ? payload : null;
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var message = await OhscrcpyGrpcProtocol.ReadMessageAsync(
                    stream, OhscrcpyConstants.MaximumGrpcMessageLength, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (!OhscrcpyGrpcProtocol.TryParseReply(message, out var reply) || reply is null)
                {
                    throw new InvalidDataException(
                        "The device gRPC video stream sent a malformed ReplyMessage. "
                        + "设备 gRPC 视频流发送了格式不符的 ReplyMessage。");
                }

                if (reply.VideoPayload is not { Length: > 0 } payload)
                {
                    continue;
                }

                await _payloads.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Volatile.Write(ref _readerFailure, ex);
        }
        finally
        {
            _payloads.Writer.TryComplete();
        }
    }

    [DoesNotReturn]
    private void ThrowStreamEnded()
    {
        var failure = Volatile.Read(ref _readerFailure);
        if (failure is not null)
        {
            throw failure;
        }

        throw new IOException("The device closed the gRPC video stream. 设备关闭了 gRPC 视频流。");
    }

    private void ResetAfterFailedConnect()
    {
        TryRun(_lifetime.Cancel);
        DisposeQuietly(Interlocked.Exchange(ref _stream, null));
        _payloads.Writer.TryComplete();
        WaitForReader();
    }

    private void WaitForReader()
    {
        var reader = Volatile.Read(ref _reader);
        if (reader is null)
        {
            return;
        }

        try
        {
            reader.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
    }

    private void SendKeyPress(int keyCode)
    {
        if (!IsControlConnected)
        {
            return;
        }

        _control!.SendKey(keyCode, OhscrcpyKeyAction.Press);
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}