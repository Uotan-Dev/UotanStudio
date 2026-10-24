using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Transports.Tcp;

namespace UotanStudio.Mirroring;

/// <summary>
/// UI Test control channel of the DevEco-style mirror path: gesture RPC over the forwarded JSON socket and
/// key / text injection over the device shell.
/// <para>DevEco 风格投屏路径的 UI Test 控制通道：经转发 JSON socket 的手势 RPC，以及经设备 shell 的按键与
/// 文本注入。</para>
/// </summary>
/// <remarks>
/// <para>
/// The forwarded stream carries unseparated JSON documents in both directions: one request is written, one
/// response is read, and the parser keeps the unconsumed tail for the next call. A partially written request or
/// a partially read response leaves the request/response boundary unknown, so the channel never reuses a stream
/// after a transport failure; it disposes the stream and reports itself disconnected instead.
/// </para>
/// <para>
/// 转发的流在两个方向上都承载无分隔的 JSON 文档：写一条请求、读一条响应，解析器保留未消费的尾部供下一次调用。
/// 请求只写入一部分或响应只读取一部分都会让请求/响应边界变得未知，因此通道在传输失败后绝不复用该流，而是释放
/// 该流并把自身标记为已断开。
/// </para>
/// <para>
/// Gestures are serialized through one queue and one pump, so the daemon sees one call at a time; only
/// consecutive moves of the same API are coalesced and down/up boundaries are never dropped. Keys and text use a
/// separate bounded queue because they travel over the HDC shell rather than over the socket.
/// </para>
/// <para>
/// 手势经单个队列与单个泵串行化，使守护进程一次只看到一条调用；只合并同一 API 的连续 move，down/up 边界永不
/// 丢弃。按键与文本使用独立的有界队列，因为它们经 HDC shell 而非 socket 传输。
/// </para>
/// </remarks>
internal sealed class OhscrcpyControlChannel : IOhscrcpyControlChannel
{
    private const string HypiumModule = "com.ohos.devicetest.hypiumApiHelper";
    private const string HypiumApiMethod = "callHypiumApi";
    private const string GesturesMethod = "Gestures";
    private const string DriverInstance = "Driver#0";
    private const string DisplaySizeApi = "Driver.getDisplaySize";

    private const string TouchUpApi = "touchUp";
    private const string ButtonLeftUpApi = "ButtonLeftUp";
    private const string ButtonRightUpApi = "ButtonRightUp";
    private const string ButtonMiddleUpApi = "ButtonMiddleUp";
    private const string AxisUpApi = "AxisUp";
    private const string AxisDownApi = "AxisDown";
    private const string AxisStopApi = "AxisStop";

    /// <summary>Gesture API names accepted by the UI Test daemon.
    /// <para>UI Test 守护进程接受的手势 API 名称。</para></summary>
    private static readonly HashSet<string> GestureApis = new(StringComparer.Ordinal)
    {
        "touchDown",
        "touchMove",
        TouchUpApi,
        "ButtonLeftDown",
        "ButtonLeftMove",
        ButtonLeftUpApi,
        "ButtonRightDown",
        "ButtonRightMove",
        ButtonRightUpApi,
        "ButtonMiddleDown",
        "ButtonMiddleMove",
        ButtonMiddleUpApi,
        "MouseMove",
        AxisUpApi,
        AxisDownApi,
        AxisStopApi,
    };

    private readonly IHdcDeviceSession _session;
    private readonly string _connectKey;
    private readonly UiTestDeviceInfo _device;
    private readonly HdcForwardTunnel _tunnel;
    private readonly Action<string>? _logSink;
    private readonly Lock _gestureLock = new();
    private readonly LinkedList<GestureCall> _gestureQueue = new();
    private readonly SemaphoreSlim _gestureReady = new(0);
    private readonly Channel<string> _shellCommands = Channel.CreateBounded<string>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly CancellationTokenSource _lifetime = new();
    private Utf8JsonRpcPipe? _rpc;
    private Stream? _stream;
    private Task? _gesturePump;
    private Task? _shellPump;
    private HeldPress? _heldPress;
    private bool _gestureCompleted;
    private volatile bool _connected;
    private int _displayWidth;
    private int _displayHeight;
    private int _disposed;

    /// <summary>Creates a control channel over an already forwarded UI Test socket.
    /// <para>在已转发的 UI Test socket 上创建控制通道。</para>
    /// </summary>
    /// <param name="session">Session whose shell route injects keys and text; must not be <see langword="null"/>.
    /// <para>经其 shell 路由注入按键与文本的会话；不得为 <see langword="null"/>。</para></param>
    /// <param name="connectKey">Target connect key used for diagnostics; blank text is rejected.
    /// <para>用于诊断的目标连接键；空白文本会被拒绝。</para></param>
    /// <param name="device">Probed UI Test capabilities; must not be <see langword="null"/>.
    /// <para>已探测的 UI Test 能力；不得为 <see langword="null"/>。</para></param>
    /// <param name="tunnel">Forward whose stream serves the JSON RPC; must not be <see langword="null"/>.
    /// <para>其流承载 JSON RPC 的转发；不得为 <see langword="null"/>。</para></param>
    /// <param name="logSink">Diagnostic sink that may be called from pump threads, or <see langword="null"/>.
    /// <para>可能由泵线程调用的诊断回调；可为 <see langword="null"/>。</para></param>
    internal OhscrcpyControlChannel(
        IHdcDeviceSession session,
        string connectKey,
        UiTestDeviceInfo device,
        HdcForwardTunnel tunnel,
        Action<string>? logSink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(tunnel);
        _session = session;
        _connectKey = connectKey;
        _device = device;
        _tunnel = tunnel;
        _logSink = logSink;
    }

    /// <summary>Whether the JSON control socket is connected. <para>JSON 控制 socket 是否已连接。</para></summary>
    public bool IsConnected => _connected;

    /// <summary>Logical display width reported by the control handshake, or zero when unknown.
    /// <para>控制握手报告的逻辑显示宽度；未知时为零。</para></summary>
    public int DisplayWidth => Volatile.Read(ref _displayWidth);

    /// <summary>Logical display height reported by the control handshake, or zero when unknown.
    /// <para>控制握手报告的逻辑显示高度；未知时为零。</para></summary>
    public int DisplayHeight => Volatile.Read(ref _displayHeight);

    /// <summary>Opens the forwarded socket, completes the display-size handshake and starts both pumps.
    /// <para>打开已转发的 socket、完成显示尺寸握手并启动两个泵。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel is disposed or already connected.
    /// <para>通道已释放或已连接。</para></exception>
    /// <exception cref="IOException">The forward, the handshake or the daemon reply failed. A control socket that
    /// never accepted a connection within <see cref="OhscrcpyConstants.ControlConnectTimeoutMs"/> is reported as a
    /// not-ready target, because a daemon that was just started or a leftover UI Test session can hold the socket.
    /// <para>转发、握手或守护进程回复失败。控制 socket 在
    /// <see cref="OhscrcpyConstants.ControlConnectTimeoutMs"/> 内始终没有接受连接时按"目标未就绪"上报，因为刚启动的
    /// 守护进程或残留的 UI Test 会话都可能占着该 socket。</para></exception>
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_rpc is not null)
        {
            throw new InvalidOperationException(
                "The UI Test control channel is already connected. UI Test 控制通道已连接。");
        }

        var stream = OpenControlStream();
        _stream = stream;
        TrySetReadTimeout(stream);
        var rpc = new Utf8JsonRpcPipe(stream);
        _rpc = rpc;
        Log($"Connecting the UI Test control socket ({_device.ControlEndpoint}, {_connectKey}). "
            + $"正在连接 UI Test 控制 socket（{_device.ControlEndpoint}，{_connectKey}）。");
        using (var response = rpc.Call(BuildDisplaySizeRequest(), _lifetime.Token))
        {
            if (TryReadDisplaySize(response.RootElement, out var width, out var height))
            {
                Volatile.Write(ref _displayWidth, width);
                Volatile.Write(ref _displayHeight, height);
                Log($"The UI Test control handshake reported a {width}x{height} display. "
                    + $"UI Test 控制握手报告的显示尺寸为 {width}x{height}。");
            }
            else
            {
                Log("The UI Test control handshake returned no display size; gestures keep video coordinates. "
                    + "UI Test 控制握手未返回显示尺寸；手势继续使用视频坐标。");
            }
        }

        _connected = true;
        _gesturePump = Task.Run(RunGesturePumpAsync);
        _shellPump = Task.Run(RunShellPumpAsync);
    }

    /// <summary>
    /// Opens the forwarded control socket, retrying connection-class failures until the control daemon is serving it.
    /// <para>打开已转发的控制 socket，对连接类失败重试，直到控制守护进程开始服务它。</para>
    /// </summary>
    /// <returns>The connected control stream. <para>已连接的控制流。</para></returns>
    /// <exception cref="IOException">The socket did not accept a connection within the control budget.
    /// <para>socket 在控制预算内没有接受连接。</para></exception>
    private Stream OpenControlStream()
    {
        var deadline = Environment.TickCount64 + OhscrcpyConstants.ControlConnectTimeoutMs;
        var delay = 200;
        Exception? last = null;
        while (true)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                return _tunnel.Open();
            }
            catch (Exception error) when (error is not ObjectDisposedException
                && error is not InvalidOperationException
                && OhscrcpyFailure.IsConnectionFailure(error))
            {
                last = error;
                if (Environment.TickCount64 >= deadline)
                {
                    break;
                }

                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 1_000);
            }
        }

        throw new IOException(
            OhscrcpyFailure.NotReadyPrefix
            + $"the UI Test control socket {_device.ControlEndpoint} for {_connectKey} did not accept a connection "
            + $"within {OhscrcpyConstants.ControlConnectTimeoutMs} ms: {last?.Message}. If DevEco Studio screen "
            + "mirroring or another mirror tab is open on this device, close it and start mirroring again. "
            + OhscrcpyFailure.NotReadyPrefix
            + $"设备 {_connectKey} 的 UI Test 控制 socket {_device.ControlEndpoint} 在 "
            + $"{OhscrcpyConstants.ControlConnectTimeoutMs} ms 内没有接受连接：{last?.Message}。如果该设备上还开着 DevEco "
            + "Studio 投屏或其它投屏标签，请先关闭，再重新开始投屏。",
            last);
    }

    /// <summary>Queues one gesture API call with raw display coordinates.
    /// <para>按原始显示坐标排队一次手势 API 调用。</para>
    /// </summary>
    /// <param name="api">One of the UI Test gesture API names. <para>UI Test 手势 API 名称之一。</para></param>
    /// <param name="x">Non-negative display X. <para>非负的显示坐标 X。</para></param>
    /// <param name="y">Non-negative display Y. <para>非负的显示坐标 Y。</para></param>
    /// <exception cref="ArgumentException"><paramref name="api"/> is blank or not a known gesture API.
    /// <para><paramref name="api"/> 为空白或不是已知的手势 API。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> or <paramref name="y"/> is negative.
    /// <para><paramref name="x"/> 或 <paramref name="y"/> 为负数。</para></exception>
    public void SendGesture(string api, int x, int y)
    {
        ValidateGesture(api, x, y);
        if (Volatile.Read(ref _disposed) != 0 || !_connected)
        {
            return;
        }

        EnqueueGesture(api, x, y);
    }

    /// <summary>Queues one wheel step as an axis gesture followed by a stop.
    /// <para>把一次滚轮步进排队为轴手势加停止。</para>
    /// </summary>
    /// <param name="up">Whether the wheel turned up. <para>滚轮是否向上。</para></param>
    /// <param name="x">Non-negative display X. <para>非负的显示坐标 X。</para></param>
    /// <param name="y">Non-negative display Y. <para>非负的显示坐标 Y。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> or <paramref name="y"/> is negative.
    /// <para><paramref name="x"/> 或 <paramref name="y"/> 为负数。</para></exception>
    public void SendScroll(bool up, int x, int y)
    {
        var api = up ? AxisUpApi : AxisDownApi;
        ValidateGesture(api, x, y);
        if (Volatile.Read(ref _disposed) != 0 || !_connected)
        {
            return;
        }

        EnqueueGesture(api, x, y);
        EnqueueGesture(AxisStopApi, x, y);
    }

    /// <summary>Queues one OpenHarmony key action through <c>uinput</c>.
    /// <para>经 <c>uinput</c> 排队一次 OpenHarmony 按键动作。</para>
    /// </summary>
    /// <param name="keyCode">OpenHarmony key code; negative values are rejected.
    /// <para>OpenHarmony 键码；负值会被拒绝。</para></param>
    /// <param name="action">Down, up or a full press. <para>按下、抬起或完整按压。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keyCode"/> is negative, or
    /// <paramref name="action"/> is not a defined value. <para><paramref name="keyCode"/> 为负数，或
    /// <paramref name="action"/> 不是已定义的值。</para></exception>
    public void SendKey(int keyCode, OhscrcpyKeyAction action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCode);
        var command = action switch
        {
            OhscrcpyKeyAction.Down => $"uinput -K -d {keyCode}",
            OhscrcpyKeyAction.Up => $"uinput -K -u {keyCode}",
            OhscrcpyKeyAction.Press => $"uinput -K -d {keyCode} -u {keyCode}",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        EnqueueShell(command);
    }

    /// <summary>Queues one two-key chord through <c>uinput</c>.
    /// <para>经 <c>uinput</c> 排队一次双键组合。</para>
    /// </summary>
    /// <param name="modifierKeyCode">Chord modifier, pressed first; negative values are rejected.
    /// <para>最先按下的组合键修饰键；负值会被拒绝。</para></param>
    /// <param name="keyCode">Chord key released after the modifier; negative values are rejected.
    /// <para>在修饰键之后释放的组合键按键；负值会被拒绝。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">Either key code is negative.
    /// <para>任一键码为负数。</para></exception>
    public void SendKeyChord(int modifierKeyCode, int keyCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(modifierKeyCode);
        ArgumentOutOfRangeException.ThrowIfNegative(keyCode);
        EnqueueShell(BuildKeyChordCommand(modifierKeyCode, keyCode));
    }

    /// <summary>Builds the two-key chord command shared by every chord injection.
    /// <para>构造每次组合键注入共用的双键命令。</para>
    /// </summary>
    /// <param name="modifierKeyCode">Chord modifier, pressed first. <para>最先按下的组合键修饰键。</para></param>
    /// <param name="keyCode">Chord key released after the modifier. <para>在修饰键之后释放的组合键按键。</para></param>
    /// <returns>The <c>uinput -K</c> command holding both keys. <para>同时按住两个键的 <c>uinput -K</c> 命令。</para></returns>
    internal static string BuildKeyChordCommand(int modifierKeyCode, int keyCode) =>
        $"uinput -K -d {modifierKeyCode} -d {keyCode} -u {modifierKeyCode} -u {keyCode}";

    /// <summary>Queues one UTF-8 text injection through <c>uinput</c>.
    /// <para>经 <c>uinput</c> 排队一次 UTF-8 文本注入。</para>
    /// </summary>
    /// <param name="text">Text to inject; its UTF-8 encoding must fit
    /// <see cref="OhscrcpyConstants.MaximumTextLength"/> bytes. <para>要注入的文本；其 UTF-8 编码必须不超过
    /// <see cref="OhscrcpyConstants.MaximumTextLength"/> 字节。</para></param>
    /// <exception cref="ArgumentException"><paramref name="text"/> encodes to more than
    /// <see cref="OhscrcpyConstants.MaximumTextLength"/> bytes. <para><paramref name="text"/> 的 UTF-8 编码超过
    /// <see cref="OhscrcpyConstants.MaximumTextLength"/> 字节。</para></exception>
    public void SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(text) > OhscrcpyConstants.MaximumTextLength)
        {
            throw new ArgumentException(
                $"Text exceeds the {OhscrcpyConstants.MaximumTextLength}-byte limit. 文本超过 {OhscrcpyConstants.MaximumTextLength} 字节上限。",
                nameof(text));
        }

        EnqueueShell("uinput -K -t " + OhscrcpyShell.Quote(text));
    }

    /// <summary>Releases the held gesture, stops both pumps and disposes the socket and the forward.
    /// <para>释放未完成的手势、停止两个泵，并释放 socket 与转发。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connected = false;
        ReleaseHeldGesture();
        lock (_gestureLock)
        {
            _gestureCompleted = true;
            _gestureReady.Release();
        }

        _shellCommands.Writer.TryComplete();
        _lifetime.Cancel();
        _rpc?.Dispose();
        _stream?.Dispose();
        var gestureStopped = WaitPump(_gesturePump, "Gesture");
        var shellStopped = WaitPump(_shellPump, "Shell");
        if (gestureStopped && shellStopped)
        {
            _lifetime.Dispose();
            _gestureReady.Dispose();
        }

        _tunnel.Dispose();
    }

    private void TrySetReadTimeout(Stream stream)
    {
        try
        {
            stream.ReadTimeout = OhscrcpyConstants.ControlRequestTimeoutMs;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Log($"The control stream rejected a read timeout; calls rely on socket closure. "
                + $"控制流拒绝设置读取超时；改由 socket 关闭结束调用。{ex.Message}");
        }
    }

    private async Task RunGesturePumpAsync()
    {
        var token = _lifetime.Token;
        while (true)
        {
            GestureCall? next;
            try
            {
                next = await DequeueGestureAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!next.HasValue)
            {
                return;
            }

            var gesture = next.Value;
            var rpc = _rpc;
            if (rpc is null)
            {
                return;
            }

            try
            {
                rpc.Call(BuildGestureRequest(gesture.Api, gesture.X, gesture.Y), token).Dispose();
                TrackPress(gesture);
            }
            catch (OhscrcpyControlApiException ex)
            {
                Log($"The device rejected the {gesture.Api} gesture. 设备拒绝了 {gesture.Api} 手势：{ex.Message}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"The gesture RPC failed and the control channel is disconnected. "
                    + $"手势 RPC 失败，控制通道已断开。{ex.Message}");
                _connected = false;
                return;
            }
        }
    }

    private async Task RunShellPumpAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (await _shellCommands.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_shellCommands.Reader.TryRead(out var command))
                {
                    RunShellCommand(command);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RunShellCommand(string command)
    {
        try
        {
            OhscrcpyShell.Run(_session, command);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            Log($"Input injection failed. 输入注入失败：{ex.Message}");
        }
    }

    private async ValueTask<GestureCall?> DequeueGestureAsync(CancellationToken token)
    {
        await _gestureReady.WaitAsync(token).ConfigureAwait(false);
        lock (_gestureLock)
        {
            if (_gestureQueue.First is not { } first)
            {
                return null;
            }

            _gestureQueue.RemoveFirst();
            return first.Value;
        }
    }

    private void EnqueueGesture(string api, int x, int y)
    {
        lock (_gestureLock)
        {
            if (_gestureCompleted)
            {
                return;
            }

            if (IsMoveApi(api) && _gestureQueue.Last is { } tail && string.Equals(tail.Value.Api, api, StringComparison.Ordinal))
            {
                tail.Value = new GestureCall(api, x, y);
                return;
            }

            _gestureQueue.AddLast(new GestureCall(api, x, y));
            _gestureReady.Release();
        }
    }

    private void TrackPress(GestureCall gesture)
    {
        lock (_gestureLock)
        {
            if (TryGetReleaseApi(gesture.Api, out var releaseApi))
            {
                _heldPress = new HeldPress(releaseApi, gesture.X, gesture.Y);
            }
            else if (IsReleaseApi(gesture.Api))
            {
                _heldPress = null;
            }
        }
    }

    private void ReleaseHeldGesture()
    {
        HeldPress? held;
        lock (_gestureLock)
        {
            held = _heldPress;
            _heldPress = null;
        }

        if (!held.HasValue || _rpc is not { } rpc)
        {
            return;
        }

        var press = held.Value;
        try
        {
            using var timeout = new CancellationTokenSource(OhscrcpyConstants.ControlRequestTimeoutMs);
            rpc.Call(BuildGestureRequest(press.ReleaseApi, press.X, press.Y), timeout.Token).Dispose();
            Log($"Released the unfinished {press.ReleaseApi} gesture. 已释放未完成的 {press.ReleaseApi} 手势。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            Log($"Releasing the unfinished gesture failed. 释放未完成手势失败：{ex.Message}");
        }
    }

    private void EnqueueShell(string command)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_shellCommands.Writer.TryWrite(command))
        {
            Log("The input queue is full and one command was dropped. 输入队列已满，丢弃了一条命令。");
        }
    }

    private bool WaitPump(Task? pump, string name)
    {
        if (pump is null)
        {
            return true;
        }

        try
        {
            if (pump.Wait(TimeSpan.FromSeconds(2)))
            {
                return true;
            }
        }
        catch (AggregateException ex)
        {
            Log($"The {name} pump ended with an error. {name} 泵异常结束：{ex.GetBaseException().Message}");
            return true;
        }

        Log($"The {name} pump did not stop within two seconds. {name} 泵未在两秒内结束。");
        return false;
    }

    private void Log(string message) => _logSink?.Invoke(message);

    private static void ValidateGesture(string api, int x, int y)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(api);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        if (!GestureApis.Contains(api))
        {
            throw new ArgumentException($"Unknown gesture API '{api}'. 未知手势 API“{api}”。", nameof(api));
        }
    }

    private static bool IsMoveApi(string api) => api.EndsWith("Move", StringComparison.Ordinal);

    private static bool IsReleaseApi(string api) => api switch
    {
        TouchUpApi or ButtonLeftUpApi or ButtonRightUpApi or ButtonMiddleUpApi or AxisStopApi => true,
        _ => false,
    };

    private static bool TryGetReleaseApi(string api, out string releaseApi)
    {
        releaseApi = api switch
        {
            "touchDown" or "touchMove" => TouchUpApi,
            "ButtonLeftDown" or "ButtonLeftMove" => ButtonLeftUpApi,
            "ButtonRightDown" or "ButtonRightMove" => ButtonRightUpApi,
            "ButtonMiddleDown" or "ButtonMiddleMove" => ButtonMiddleUpApi,
            AxisUpApi or AxisDownApi => AxisStopApi,
            _ => "",
        };
        return releaseApi.Length != 0;
    }

    private static bool TryReadDisplaySize(JsonElement root, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !TryReadInteger(result, "x", out var reportedWidth)
            || !TryReadInteger(result, "y", out var reportedHeight)
            || reportedWidth <= 0
            || reportedHeight <= 0)
        {
            return false;
        }

        width = reportedWidth;
        height = reportedHeight;
        return true;
    }

    private static bool TryReadInteger(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (property.TryGetInt32(out value))
        {
            return true;
        }

        if (!property.TryGetDouble(out var number) || double.IsNaN(number) || number is < 0 or > int.MaxValue)
        {
            return false;
        }

        value = (int)Math.Round(number, MidpointRounding.AwayFromZero);
        return true;
    }

    private static byte[] BuildDisplaySizeRequest()
    {
        var buffer = new ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("module", HypiumModule);
            writer.WriteString("method", HypiumApiMethod);
            writer.WriteStartObject("params");
            writer.WriteString("api", DisplaySizeApi);
            writer.WriteStartArray("args");
            writer.WriteEndArray();
            writer.WriteString("this", DriverInstance);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildGestureRequest(string api, int x, int y)
    {
        var buffer = new ArrayBufferWriter<byte>(96);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("module", HypiumModule);
            writer.WriteString("method", GesturesMethod);
            writer.WriteStartObject("params");
            writer.WriteString("api", api);
            writer.WriteStartObject("args");
            writer.WriteNumber("x", x);
            writer.WriteNumber("y", y);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>One queued gesture call. <para>一条已排队的手势调用。</para></summary>
    private readonly record struct GestureCall(string Api, int X, int Y);

    /// <summary>The press still in progress, tracked so disposal can release it.
    /// <para>仍在进行中的按压，用于在释放时结束它。</para></summary>
    private readonly record struct HeldPress(string ReleaseApi, int X, int Y);

    /// <summary>
    /// Serializes request/response pairs over one control stream and parses the reply incrementally, so TCP
    /// fragmentation does not need a message delimiter.
    /// <para>在单条控制流上串行化请求/响应，并以增量方式解析回复，使 TCP 分片不需要消息分隔符。</para>
    /// </summary>
    private sealed class Utf8JsonRpcPipe(Stream stream) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private byte[] _buffer = new byte[4096];
        private int _count;
        private Stream? _stream = stream;
        private int _disposed;

        /// <summary>Sends one request and returns its parsed response document.
        /// <para>发送一条请求并返回解析后的响应文档。</para>
        /// </summary>
        /// <param name="request">Complete UTF-8 JSON request. <para>完整的 UTF-8 JSON 请求。</para></param>
        /// <param name="cancellationToken">Cancels the gate wait. <para>取消等待串行化门。</para></param>
        /// <returns>The response document, which the caller disposes.
        /// <para>响应文档，由调用方释放。</para></returns>
        /// <exception cref="OhscrcpyControlApiException">The device replied with a non-null <c>exception</c>.
        /// <para>设备返回了非空的 <c>exception</c>。</para></exception>
        /// <exception cref="IOException">The socket failed, closed or exceeded the response bound.
        /// <para>socket 失败、关闭或响应超出上限。</para></exception>
        public JsonDocument Call(byte[] request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _gate.Wait(cancellationToken);
            try
            {
                var target = _stream ?? throw new IOException(
                    "The UI Test control socket is closed. UI Test 控制 socket 已关闭。");
                try
                {
                    target.Write(request, 0, request.Length);
                    target.Flush();
                    while (true)
                    {
                        var document = TryReadResponse();
                        if (document is not null)
                        {
                            if (document.RootElement.ValueKind == JsonValueKind.Object
                                && document.RootElement.TryGetProperty("exception", out var error)
                                && error.ValueKind != JsonValueKind.Null)
                            {
                                var message = error.ToString();
                                document.Dispose();
                                throw new OhscrcpyControlApiException(message);
                            }

                            return document;
                        }

                        if (_count == _buffer.Length)
                        {
                            GrowBuffer();
                        }

                        var read = target.Read(_buffer, _count, _buffer.Length - _count);
                        if (read <= 0)
                        {
                            throw new IOException(
                                "The device closed the UI Test control socket. 设备关闭了 UI Test 控制 socket。");
                        }

                        _count += read;
                    }
                }
                catch (Exception ex) when (ex is not OhscrcpyControlApiException)
                {
                    // The request/response boundary is uncertain; this stream is never reused.
                    _stream?.Dispose();
                    _stream = null;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Disposes the stream; the gate stays undisposed because a pump may still hold it.
        /// <para>释放流；串行化门不释放，因为泵可能仍持有它。</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stream?.Dispose();
            _stream = null;
        }

        private void GrowBuffer()
        {
            if (_buffer.Length >= OhscrcpyConstants.MaximumControlMessageLength)
            {
                throw new IOException(
                    $"The UI Test control response exceeds the {OhscrcpyConstants.MaximumControlMessageLength}-byte limit. "
                    + $"UI Test 控制响应超过 {OhscrcpyConstants.MaximumControlMessageLength} 字节上限。");
            }

            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, OhscrcpyConstants.MaximumControlMessageLength));
        }

        private JsonDocument? TryReadResponse()
        {
            var skip = 0;
            while (skip < _count && _buffer[skip] is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                skip++;
            }

            if (skip > 0)
            {
                Buffer.BlockCopy(_buffer, skip, _buffer, 0, _count - skip);
                _count -= skip;
            }

            if (_count == 0)
            {
                return null;
            }

            var reader = new Utf8JsonReader(_buffer.AsSpan(0, _count), isFinalBlock: false, state: default);
            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                return null;
            }

            var used = (int)reader.BytesConsumed;
            Buffer.BlockCopy(_buffer, used, _buffer, 0, _count - used);
            _count -= used;
            return document;
        }
    }
}

/// <summary>
/// The UI Test daemon answered a control request with an application-level <c>exception</c> property.
/// <para>UI Test 守护进程用应用级 <c>exception</c> 属性回复了一条控制请求。</para>
/// </summary>
internal sealed class OhscrcpyControlApiException(string message) : IOException(message);