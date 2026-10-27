using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Media.Decoding.VideoToolbox;
using UotanStudio.Mirroring;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// scrcpy screen-mirror document: deploys the server, decodes the H.264/H.265
/// video stream and hands frames to GPU presentation or the bitmap fallback.
/// <para>scrcpy 投屏文档：部署服务端、解码视频流，并交接到 GPU 呈现器或位图回退。</para>
/// </summary>
/// <remarks>
/// <para>
/// 解码循环运行在专用线程：scrcpy 帧解包 → <see cref="IVideoDecoder"/> 平台解码 →
/// 紧凑平面交接 → GPU 颜色转换与显示；无 GPU 呈现器时回退到 CPU 转换与位图写入。
/// 回调契约要求在解码回调内完成复制（帧随后被解码器释放）。
/// </para>
/// <para>
/// The decode loop runs on a dedicated thread: scrcpy frame demux →
/// <see cref="IVideoDecoder"/> platform decode → compact plane handoff → GPU
/// presentation, or CPU conversion and a bitmap fallback. The decoder
/// callback contract requires the copy to happen inside the callback
/// (the frame is disposed right after it returns).
/// </para>
/// </remarks>
public partial class ScreenMirrorViewModel : Document, ITool
{
    private const int KeyFrameMinimumIntervalMilliseconds = 750;
    private const int KeyFrameRequestRepeatMilliseconds = 2000;
    private const int SessionThreadJoinMilliseconds = 2000;
    private const int InputInjectionRecheckMilliseconds = 3000;

    private MirrorSessionFacade? _client;
    private DetectedDevice? _mirrorDevice;
    private long _lastInputInjectionProbeTicks;
    private int _inputInjectionRecheckPending;
    private int _starting;
    private long _lastKeyFrameRequestTicks;
    private readonly Lock _frameLock = new();
    private int _renderLoggedThrottle;
    private Views.Editors.ScreenMirrorPixelBuffer? _pendingFrame;
    private Views.Editors.ScreenMirrorPixelBuffer? _spareFrame;
    private bool _renderScheduled;
    internal Views.Editors.ScreenMirrorGpuPresenter GpuPresenter { get; private set; } = new();
    internal PixelSize VideoPixelSize => GpuPresenter.IsAvailable && GpuPresenter.PixelSize is { Width: > 0 } size ? size : Frame?.PixelSize ?? default;
    internal PixelSize InjectionVideoSize => Views.Editors.ScreenMirrorCoordinates.ResolveInjectionSize(StreamMetadata, VideoPixelSize);

    /// <summary>
    /// Whether any video output has arrived, including a GPU-composited frame that never materializes
    /// as a bitmap; the waiting overlay is hidden once this becomes true.
    /// <para>是否已收到任何视频输出，包括不会生成位图的 GPU 合成帧；一旦为真即隐藏等待覆盖文字。</para>
    /// </summary>
    internal bool HasPresentedVideo => StreamMetadata is not null || Frame is not null ||
        GpuPresenter.Draws > 0 || GpuPresenter.CompositedFrames > 0;

    private CancellationTokenSource? _cts;
    private Thread? _decodeThread;
    private Thread? _audioThread;
    private Thread? _controlThread;
    private volatile bool _disposed;
    private int _sessionGeneration;

    /// <summary>Raised on the audio thread after the output sink opened successfully.
    /// <para>输出接收端成功打开后在音频线程触发。</para>
    /// </summary>
    public event EventHandler? AudioSinkOpened;

    /// <summary>
    /// Clipboard text copied on the device or returned for an explicit request, raised from the control-message
    /// pump thread; subscribers marshal to the UI thread before touching the local clipboard.
    /// <para>设备端复制或请求回传的剪贴板文本，从控制消息泵线程触发；订阅方负责切回 UI 线程写入本地剪贴板。</para>
    /// </summary>
    public event EventHandler<string>? DeviceClipboardReceived;

    /// <summary>Gets the total decoded audio frames; 0 while audio is off or unsupported.
    /// <para>获取累计解码音频帧数；音频关闭或编码不受支持时为 0。</para>
    /// </summary>
    public long AudioDecodedFrames => Interlocked.Read(ref _audioDecodedFrames);

    /// <summary>Gets the decoded audio sample rate; 0 until the first decoded frame.
    /// <para>获取解码音频采样率；首帧解码前为 0。</para>
    /// </summary>
    public int AudioSampleRate => Volatile.Read(ref _audioSampleRate);

    /// <summary>Gets the decoded audio channel count; 0 until the first decoded frame.
    /// <para>获取解码音频声道数；首帧解码前为 0。</para>
    /// </summary>
    public int AudioChannels => Volatile.Read(ref _audioChannels);

    /// <summary>Gets whether the session requested an audio stream.
    /// <para>获取本会话是否请求了音频流。</para>
    /// </summary>
    public bool AudioEnabled => StreamOptions?.Audio ?? false;

    /// <summary>Raised on the UI thread when a GPU frame is available or the fallback bitmap is updated; not a display-present timestamp.
    /// <para>GPU 帧可用或回退位图更新时在 UI 线程触发；不是显示呈现时间戳。</para>
    /// </summary>
    public event EventHandler? FramePresented;

    private long _receivedFrames, _decodedFrames, _uploadedFrames, _receivedBytes;
    private int _frameFailureLogged;
    private long _audioDecodedFrames;
    private int _decodeLoggedThrottle;
    private long _lastIdleTrace;
    private int _exportFrameLogged;
    private int _packetLogged;
    private int _decodeErrorLogged;
    private int _audioSampleRate, _audioChannels;
    private string _decoderBackend = string.Empty;
    private ScrcpyServerOptions? _streamOptions;
    /// <summary>Session parameters and counters; frame rates are calculated from counter deltas.</summary>
    internal (long Received, long Decoded, long Uploaded, long Bytes) ReadCounters()
        => (Interlocked.Read(ref _receivedFrames), Interlocked.Read(ref _decodedFrames),
            Interlocked.Read(ref _uploadedFrames) + GpuPresenter.Draws, Interlocked.Read(ref _receivedBytes));
    internal string DecoderBackend => _decoderBackend;
    private IVideoDecoder? _pixelFormatDecoder;

    /// <summary>
    /// Gets the pixel layout the active decoder delivers right now, taken from the decoder instead of
    /// inferred from its display name; it flips when a GPU export fails mid-session.
    /// <para>获取当前激活解码器正在交付的像素布局，取自解码器本身而非其显示名称；会话中途 GPU 导出失败时该值随之改变。</para>
    /// </summary>
    internal VideoPixelFormat MirrorFrameFormat => _pixelFormatDecoder is { } decoder
        ? VideoDecoderFactory.GetFramePixelFormat(decoder)
        : VideoPixelFormat.Nv12;

    /// <summary>
    /// Gets the conversion descriptor for the properties panel, or an empty string when the localized
    /// software-conversion default applies.
    /// <para>获取属性面板使用的转换描述；适用本地化的软件转换默认值时返回空字符串。</para>
    /// </summary>
    internal string MirrorConversion
    {
        get
        {
            if (_pixelFormatDecoder is not { } decoder || MirrorFrameFormat != VideoPixelFormat.Bgra32) return string.Empty;
            return MirrorDecoderRecovery.BackendOf(VideoDecoderFactory.GetActiveDecoder(decoder)) == VideoDecoderBackend.MediaFoundationGpu
                ? "D3D11 → BGRA"
                : "VideoToolbox → BGRA";
        }
    }
    internal ScrcpyServerOptions? StreamOptions => _streamOptions;
    internal ScrcpyVideoMetadata? StreamMetadata => _client?.VideoMetadata;
    internal ScrcpyAudioMetadata? StreamMetadataAudio => _client?.AudioMetadata;
    internal bool IsStopped => _disposed;
    internal bool AudioDisabledByServer => _client?.AudioDisabledByServer == true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private WriteableBitmap? _frame;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    /// <summary>
    /// Gets whether the device refused input injection, so touch and key control cannot reach it until the user
    /// enables the ROM's debug injection permission. The mirror keeps showing video while the header explains why
    /// the device does not react to clicks.
    /// <para>获取设备是否拒绝输入注入：在用户打开 ROM 的调试注入权限之前，触摸与按键都无法送达设备。投屏继续显示
    /// 画面，标题栏则说明点击没有反应的原因。</para>
    /// </summary>
    [ObservableProperty]
    private bool _inputInjectionBlocked;

    /// <summary>
    /// Resolves the device by serial and starts mirroring (deploy/connect runs on a background thread).
    /// <para>按序列号解析设备并启动投屏（部署/连接在后台线程执行）。</para>
    /// </summary>
    /// <param name="serial">The target device serial. <para>目标设备序列号。</para></param>
    public void StartForDevice(string serial)
    {
        if (_disposed || _client is not null || Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return;

        _serial = serial;
        InputInjectionBlocked = false;
        ApplyMirrorSettingsCommand.NotifyCanExecuteChanged();
        StatusText = GetLocalizedString("ScreenMirror_Preparing");
        _startTask = Task.Run(async () =>
        {
            try
            {
                var candidates = App.DeviceMonitor.Scan()
                    .Where(candidate => candidate.Serial == serial &&
                        candidate.Protocol is Services.Devices.DeviceProtocolKind.Adb or Services.Devices.DeviceProtocolKind.Hdc)
                    .ToArray();
                var device = candidates.FirstOrDefault(candidate => candidate.Protocol == Services.Devices.DeviceProtocolKind.Adb)
                    ?? candidates.FirstOrDefault(candidate => candidate.Protocol == Services.Devices.DeviceProtocolKind.Hdc)
                    ?? throw new InvalidOperationException($"Device {serial} is not connected.");

                _mirrorDevice = device;
                UsesScrcpyServer = device.Protocol != Services.Devices.DeviceProtocolKind.Hdc;
                MirrorSessionFacade facade;
                if (device.Protocol == Services.Devices.DeviceProtocolKind.Hdc)
                {
                    _streamOptions = null;
                    _activeSettings = PendingSettings.Copy();
                    facade = MirrorSessionFacade.FromOhscrcpy(
                        App.DeviceMonitor.StartOhscrcpyMirror(device, CreateHdcLaunchOptions(_activeSettings)));
                }
                else
                {
                    StatusText = GetLocalizedString("ScreenMirror_Installing");
                    var artifact = await Services.Scrcpy.ScrcpyServerService.Shared
                        .ResolveOrProvisionAsync().ConfigureAwait(false);
                    Services.LogService.Instance.Write(Services.LogLevel.Info, "ScreenMirror",
                        $"scrcpy server {artifact.Version} ({artifact.Source}) at {artifact.Path}.");
                    var options = CreateOptionsFromSettings(artifact.Version, SupportsScrcpyAudio(device));
                    _streamOptions = options;
                    facade = MirrorSessionFacade.FromScrcpy(App.DeviceMonitor.StartMirror(
                        device, artifact.Path, options));
                }

                if (_disposed)
                {
                    facade.Dispose();
                    return;
                }
                _client = facade;
                if (_disposed)
                {
                    _client = null;
                    facade.Dispose();
                    return;
                }
                StatusText = GetLocalizedString("ScreenMirror_Connecting");
                BeginStreaming();
                ReportInputInjection(device);
            }
            catch (Exception ex)
            {
                StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + Services.UserError.Describe(ex);
                Services.LogService.Instance.Write(
                    Services.LogLevel.Error, "ScreenMirror", $"Start failed: {ex.Message}");
                _client?.Dispose(); _client = null;
            }
            finally { Interlocked.Exchange(ref _starting, 0); }
        });
    }

    /// <summary>
    /// Reads the device's input-injection grant once per session and raises <see cref="InputInjectionBlocked"/> when
    /// the ROM withholds it, so the header names the developer option instead of leaving a silent, untappable picture.
    /// A probe that cannot read the package dump leaves the state untouched.
    /// <para>每个会话读取一次设备的输入注入授权；ROM 未授权时置位 <see cref="InputInjectionBlocked"/>，让标题栏直接
    /// 指出应开启的开发者选项，而不是留下一个无法点击也没有提示的画面。读不到包转储时保持原状态。</para>
    /// </summary>
    /// <param name="device">The device this session mirrors. <para>本会话投屏的设备。</para></param>
    private void ReportInputInjection(DetectedDevice device)
    {
        if (_streamOptions?.Control != true)
        {
            return;
        }

        bool? allowed;
        try
        {
            allowed = App.DeviceMonitor.ProbeInputInjection(device, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.LogService.Instance.Write(Services.LogLevel.Debug, "ScreenMirror",
                $"Input-injection probe for {device.Serial} is unavailable: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _lastInputInjectionProbeTicks, Environment.TickCount64);
        }

        if (allowed != false)
        {
            return;
        }

        InputInjectionBlocked = true;
        Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror",
            $"Device {device.Serial} does not grant android.permission.INJECT_EVENTS to the shell identity; " +
            "enable USB debugging (Security settings) in Developer options to restore touch and key control.");
    }

    /// <summary>
    /// Re-runs the input-injection probe at most once per <see cref="InputInjectionRecheckMilliseconds"/> while the
    /// warning is visible, so enabling the ROM's debug permission clears the header on the next tap instead of
    /// requiring a session restart. The probe runs on the pool and never blocks the input path.
    /// <para>警告可见期间每 <see cref="InputInjectionRecheckMilliseconds"/> 最多重跑一次注入探测，使打开 ROM 的调试
    /// 注入权限后下一次点击即可清除提示，而不必重启会话。探测在线程池上执行，从不阻塞输入路径。</para>
    /// </summary>
    private void RecheckInputInjection()
    {
        if (!InputInjectionBlocked || _mirrorDevice is not { } device || _client is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastInputInjectionProbeTicks) < InputInjectionRecheckMilliseconds ||
            Interlocked.Exchange(ref _inputInjectionRecheckPending, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastInputInjectionProbeTicks, now);
        _ = Task.Run(() =>
        {
            try
            {
                if (App.DeviceMonitor.ProbeInputInjection(device, CancellationToken.None) == true)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_disposed)
                        {
                            InputInjectionBlocked = false;
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Services.LogService.Instance.Write(Services.LogLevel.Debug, "ScreenMirror",
                    $"Input-injection recheck for {device.Serial} is unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _inputInjectionRecheckPending, 0);
            }
        });
    }

    private void BeginStreaming()
    {
        if (_disposed) { _client?.Dispose(); return; }
        var client = _client ?? throw new InvalidOperationException("Missing scrcpy client.");
        client.Connect();
        if (_disposed) { client.Dispose(); return; }

        DeviceName = client.DeviceName;
        StatusText = GetLocalizedString("ScreenMirror_Mirroring");

        GpuPresenter.CompositionRestored += OnCompositionRestored;
        if (_disposed)
        {
            GpuPresenter.CompositionRestored -= OnCompositionRestored;
            client.Dispose();
            return;
        }

        _cts = new CancellationTokenSource();
        var decodeClient = client;
        var decodeToken = _cts.Token;
        var generation = Interlocked.Increment(ref _sessionGeneration);
        _decodeThread = new Thread(() => DecodeLoop(decodeClient, decodeToken, generation))
        {
            IsBackground = true,
            Name = "UotanStudio.ScreenMirror",
        };
        _decodeThread.Start();
        if (decodeClient.AudioMetadata is not null)
        {

            _audioThread = new Thread(() => AudioLoop(decodeClient, decodeToken))
            {
                IsBackground = true,
                Name = "UotanStudio.ScreenMirror.Audio",
            };
            _audioThread.Start();
        }
        if (decodeClient.IsControlConnected)
        {
            _controlThread = new Thread(() => ControlLoop(decodeClient, decodeToken))
            {
                IsBackground = true,
                Name = "UotanStudio.ScreenMirror.Control",
            };
            _controlThread.Start();
        }
    }

    private void ControlLoop(MirrorSessionFacade client, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !_disposed)
        {
            ScrcpyDeviceMessage? message;
            try
            {
                message = client.ReadDeviceMessage(200);
            }
            catch (Exception)
            {
                return;
            }

            if (message is null)
            {
                continue;
            }

            if (message.Type == ScrcpyConstants.DeviceTypeClipboard && message.Clipboard is { Length: > 0 } text)
            {
                DeviceClipboardReceived?.Invoke(this, text);
            }
        }
    }

    /// <summary>
    /// Cleanup entry invoked by the workspace when the document closes: stops
    /// the decode loop and disconnects every socket.
    /// <para>工作区文档关闭时的清理入口：停止解码循环并断开所有 socket。</para>
    /// </summary>
    public void Cleanup()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GpuPresenter.Dispose();
        StopSessionCore();
        _mirrorDevice = null;
        InputInjectionBlocked = false;
        StatusText = GetLocalizedString("ScreenMirror_Stopped");
    }

    /// <summary>
    /// Stops the current server session while keeping this document reusable: cancels the loops, reports the stop in
    /// <see cref="StatusText"/>, clears the presented frame and hands the blocking teardown to the thread pool,
    /// without marking the view model disposed.
    /// <para>停止当前服务端会话但保留文档可复用：取消循环、在 <see cref="StatusText"/> 中报告已停止、清除已呈现帧，
    /// 并把阻塞式拆除交给线程池，不把视图模型标记为已释放。</para>
    /// </summary>
    /// <remarks>
    /// Only the state reset and the frame release run on the calling thread. Waiting for the video, audio and control
    /// pumps and closing the client, the borrowed ADB session and the device lease wait on socket timeouts and native
    /// teardown, so they run in <see cref="TearDownSession"/> in the order a synchronous teardown used: stop the
    /// pumps, release the client, then the token source.
    /// <para>只有状态复位与帧释放在调用线程执行。等待视频、音频与控制泵结束，以及关闭客户端、归还借用的 ADB 会话与设备
    /// 租约，都要等 socket 超时和原生拆除，因此由 <see cref="TearDownSession"/> 按同步拆除的顺序在后台执行：
    /// 先停泵、再释放客户端、最后释放令牌源。</para>
    /// </remarks>
    private void StopSessionCore()
    {
        MarkSessionStopped();
        GpuPresenter.CompositionRestored -= OnCompositionRestored;
        Interlocked.Increment(ref _sessionGeneration);

        var cts = _cts;
        var client = _client;
        var decodeThread = _decodeThread;
        var audioThread = _audioThread;
        var controlThread = _controlThread;
        _cts = null;
        _client = null;
        _decodeThread = null;
        _audioThread = null;
        _controlThread = null;
        _startTask = null;

        cts?.Cancel();
        lock (_frameLock)
        {
            _pendingFrame = null;
            _spareFrame = null;
        }

        ReleasePresentedFrame();

        if (client is null && cts is null && decodeThread is null && audioThread is null && controlThread is null)
        {
            return;
        }

        _ = Task.Factory.StartNew(
            () => TearDownSession(client, cts, decodeThread, audioThread, controlThread),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Whether a decoder callback still belongs to the session this document is running now.
    /// <para>判断一次解码回调是否仍属于本文档当前运行的会话。</para>
    /// </summary>
    /// <param name="generation">Generation captured when the session started.
    /// <para>会话启动时捕获的代际。</para></param>
    /// <returns><see langword="true"/> while no stop or newer session advanced the generation.
    /// <para>没有停止或更新的会话推进代际时为 <see langword="true"/>。</para></returns>
    private bool IsCurrentSession(int generation) => generation == Volatile.Read(ref _sessionGeneration);

    /// <summary>
    /// Clears the presented bitmap on the UI thread and raises <see cref="FramePresented"/> once.
    /// <para>在 UI 线程清除已呈现位图并触发一次 <see cref="FramePresented"/>。</para>
    /// </summary>
    private void ReleasePresentedFrame()
    {
        var oldFrame = Frame;
        void Release()
        {
            if (ReferenceEquals(Frame, oldFrame))
            {
                Frame = null;
            }

            oldFrame?.Dispose();
            FramePresented?.Invoke(this, EventArgs.Empty);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Release();
        }
        else
        {
            Dispatcher.UIThread.Post(Release);
        }
    }

    /// <summary>
    /// Completes one stopped session on a background thread: waits for its pumps to finish, then releases the client
    /// and the token source in that order. The borrowed shared ADB session is returned through the client's lease, so
    /// a session that another borrower still uses stays open.
    /// <para>在后台线程完成一次已停止会话的拆除：先等其泵线程结束，再按该顺序释放客户端与令牌源。借用的共享 ADB
    /// 会话由客户端的租约归还，因此仍有其它借用方使用的会话不会被关闭。</para>
    /// </summary>
    /// <param name="client">The stopped session facade to release, or <see langword="null"/>.
    /// <para>要释放的已停止会话门面；可为 <see langword="null"/>。</para></param>
    /// <param name="cts">The stopped session's token source, disposed last.
    /// <para>已停止会话的令牌源，最后释放。</para></param>
    /// <param name="decodeThread">The video pump, or <see langword="null"/> before it started.
    /// <para>视频泵线程；未启动时为 <see langword="null"/>。</para></param>
    /// <param name="audioThread">The audio pump, or <see langword="null"/> when audio is off.
    /// <para>音频泵线程；音频关闭时为 <see langword="null"/>。</para></param>
    /// <param name="controlThread">The control pump, or <see langword="null"/> when the control channel is closed.
    /// <para>控制泵线程；控制通道关闭时为 <see langword="null"/>。</para></param>
    private static void TearDownSession(MirrorSessionFacade? client, CancellationTokenSource? cts,
        Thread? decodeThread, Thread? audioThread, Thread? controlThread)
    {
        JoinSessionThread(decodeThread, "video");
        JoinSessionThread(audioThread, "audio");
        JoinSessionThread(controlThread, "control");
        try
        {
            client?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror",
                $"Releasing a stopped mirror session failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Waits a bounded time for one stopped pump thread and reports a thread that is still running.
    /// <para>在有限时间内等待一个已停止的泵线程，并报告仍未结束的线程。</para>
    /// </summary>
    /// <param name="thread">The pump thread to join, or <see langword="null"/>.
    /// <para>要等待的泵线程；可为 <see langword="null"/>。</para></param>
    /// <param name="name">Pump name used in the diagnostic line. <para>诊断行里使用的泵名称。</para></param>
    private static void JoinSessionThread(Thread? thread, string name)
    {
        if (thread is null)
        {
            return;
        }

        try
        {
            if (!thread.Join(SessionThreadJoinMilliseconds))
            {
                Services.LogService.Instance.Write(Services.LogLevel.Debug, "ScreenMirror",
                    $"The {name} loop did not finish within {SessionThreadJoinMilliseconds} ms after the session stopped.");
            }
        }
        catch (ThreadStateException error)
        {
            Services.LogService.Instance.Write(Services.LogLevel.Debug, "ScreenMirror",
                $"The {name} loop could not be joined: {error.Message}");
        }
    }

    /// <summary>
    /// Leaves the mirroring status behind once no session is running, so the header never claims to mirror a
    /// device that has already stopped.
    /// <para>会话不再运行时离开「投屏中」状态，使标题栏不会对已经停止的设备继续声称正在投屏。</para>
    /// </summary>
    private void MarkSessionStopped()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _mirrorDevice = null;
            InputInjectionBlocked = false;
            StatusText = GetLocalizedString("ScreenMirror_Stopped");
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _mirrorDevice = null;
            InputInjectionBlocked = false;
            StatusText = GetLocalizedString("ScreenMirror_Stopped");
        });
    }

    /// <summary>
    /// Retargets this document to another ADB device, stopping the previous session first. Called on
    /// the UI thread when the device-list selection changes.
    /// <para>把本文档切换到另一台 ADB 设备，先停止旧会话。设备列表选中项变化时在 UI 线程调用。</para>
    /// </summary>
    /// <param name="serial">Serial of the new mirror target.
    /// <para>新投屏目标的序列号。</para></param>
    internal void RetargetTo(string serial)
    {
        if (_disposed || string.IsNullOrWhiteSpace(serial) || _starting != 0)
        {
            return;
        }

        if (string.Equals(_serial, serial, StringComparison.Ordinal) && _client is not null)
        {
            return;
        }

        StopSessionCore();
        _serial = serial;
        StartForDevice(serial);
    }

    /// <summary>
    /// Stops the current session and reports that the selected protocol cannot use scrcpy mirroring.
    /// <para>停止当前会话，并报告所选协议无法使用 scrcpy 投屏。</para>
    /// </summary>
    /// <param name="resourceKey">Localized notice key for the selected protocol.
    /// <para>所选协议对应的本地化提示键。</para></param>
    internal void ShowUnsupported(string resourceKey)
    {
        if (_disposed)
        {
            return;
        }

        StopSessionCore();
        _serial = null;
        _mirrorDevice = null;
        InputInjectionBlocked = false;
        StatusText = GetLocalizedString(resourceKey);
    }

    /// <summary>
    /// Injects a touch event (video pixel coordinates).
    /// <para>注入触摸事件（视频像素坐标）。</para>
    /// </summary>
    /// <param name="action">Motion action: down/up/move. <para>动作：按下/抬起/移动。</para></param>
    /// <param name="pointerId">Pointer id (<see cref="ScrcpyConstants.PointerIdMouse"/> for mouse). <para>指针 ID（鼠标为 <see cref="ScrcpyConstants.PointerIdMouse"/>）。</para></param>
    /// <param name="x">Video pixel X. <para>视频像素坐标 X。</para></param>
    /// <param name="y">Video pixel Y. <para>视频像素坐标 Y。</para></param>
    /// <param name="width">Decoded frame width. <para>解码帧宽度。</para></param>
    /// <param name="height">Decoded frame height. <para>解码帧高度。</para></param>
    /// <param name="buttons">Current button state mask. <para>当前按键状态掩码。</para></param>
    /// <param name="actionButton">Button that changed on press/release; zero for movement or cancellation.</param>
    public void InjectTouch(byte action, long pointerId, int x, int y, int width, int height, uint buttons, uint actionButton = 0)
    {
        RecheckInputInjection();
        SendControl(ScrcpyControlMessageWriter.InjectTouchEvent(action, pointerId, x, y, width, height,
            pressure: action is ScrcpyConstants.ActionUp or 3 ? (ushort)0 : ushort.MaxValue,
            actionButton: actionButton, buttons: buttons));
    }

    /// <summary>
    /// Injects a scroll event (video pixel coordinates).
    /// <para>注入滚轮事件（视频像素坐标）。</para>
    /// </summary>
    /// <param name="x">Video pixel X. <para>视频像素坐标 X。</para></param>
    /// <param name="y">Video pixel Y. <para>视频像素坐标 Y。</para></param>
    /// <param name="width">Decoded frame width. <para>解码帧宽度。</para></param>
    /// <param name="height">Decoded frame height. <para>解码帧高度。</para></param>
    /// <param name="delta">Scroll ticks: positive scrolls down. <para>滚动刻度：正数向下滚动。</para></param>
    public void InjectScroll(int x, int y, int width, int height, float delta)
        => SendControl(ScrcpyControlMessageWriter.InjectScrollEvent(
            x, y, width, height,
            0,

            -delta));

    /// <summary>
    /// Injects an Android keycode event.
    /// <para>注入 Android 键码事件。</para>
    /// </summary>
    /// <param name="action">Action: down/up. <para>动作：按下/抬起。</para></param>
    /// <param name="keyCode">Android keycode. <para>Android 键码。</para></param>
    /// <param name="metaState">Modifier mask. <para>修饰键掩码。</para></param>
    public void InjectKeyCode(byte action, int keyCode, int metaState)
        => SendControl(ScrcpyControlMessageWriter.InjectKeyCode(action, keyCode, 0, metaState));

    /// <summary>
    /// Injects UTF-8 text.
    /// <para>注入 UTF-8 文本。</para>
    /// </summary>
    /// <param name="text">Text to inject (≤300 bytes). <para>要注入的文本（≤300 字节）。</para></param>
    public void InjectText(string text)
        => SendControl(ScrcpyControlMessageWriter.InjectText(text));

    /// <summary>
    /// Writes text to the device clipboard, optionally pasting into the focused field; ignored while the control
    /// channel is unavailable, matching touch injection.
    /// <para>把文本写入设备剪贴板，可选择直接粘贴到焦点输入框。控制通道未就绪时静默忽略，与触摸注入一致。</para>
    /// </summary>
    /// <param name="text">Text to send (300 UTF-8 byte cap). <para>要送往设备的文本（UTF-8 上限 300 字节）。</para></param>
    /// <param name="paste">Whether the device also pastes it. <para>是否在设备端同时粘贴。</para></param>
    public void SendClipboard(string text, bool paste) => SendSession(client => client.SendClipboard(text, paste));

    /// <summary>
    /// Requests the device clipboard; the answer arrives on <see cref="DeviceClipboardReceived"/>.
    /// <para>请求设备回传剪贴板；结果经 <see cref="DeviceClipboardReceived"/> 送达。</para>
    /// </summary>
    public void RequestClipboard() => SendSession(client => client.RequestClipboard());

    /// <summary>
    /// Asks the device for an immediate key frame after a composition surface was rebuilt, the host became visible
    /// again or the video stream stayed silent, so presentation does not wait for the next natural key frame; the
    /// ohscrcpy adapter maps the scrcpy reset-video control message to a request-key-frame action.
    /// <para>合成表面重建、宿主重新可见或视频流静默后请求设备立即发送关键帧，使呈现不必等待下一个自然关键帧；
    /// ohscrcpy 适配器会把 scrcpy 的重置视频控制消息映射为请求关键帧动作。</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the request was written to the control channel; <see langword="false"/> when the
    /// session has no control channel or the previous request is still inside the minimum interval.
    /// <para>请求已写入控制通道时为 <see langword="true"/>；会话没有控制通道，或距上次请求不足最小间隔时为
    /// <see langword="false"/>。</para>
    /// </returns>
    internal bool RequestKeyFrame()
    {
        if (_client is not { IsControlConnected: true } || !TryReserveKeyFrameRequest())
        {
            return false;
        }

        SendControl(ScrcpyControlMessageWriter.ResetVideo());
        return true;
    }

    /// <summary>
    /// Re-establishes the picture after the host window or the dock document became visible again: re-asserts the
    /// hosted composition visual, re-letterboxes it and asks the device for a key frame; UI thread only.
    /// <para>宿主窗口或停靠文档重新可见后重建画面：重新挂上托管的合成可视对象、按信箱模式布局并向设备请求关键帧；
    /// 仅限 UI 线程。</para>
    /// </summary>
    internal void RecoverPresentation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        Views.Editors.MirrorTrace.Write($"vm recover covering={GpuPresenter.CompositionCoversVideo} presentation={GpuPresenter.PresentationPath} keyframe={RequestKeyFrameInternal()}");
        GpuPresenter.RecoverCompositionHost();
        RequestKeyFrame();
    }

    private bool RequestKeyFrameInternal()
        => _client is { IsControlConnected: true };

    private void OnCompositionRestored(object? sender, EventArgs e)
    {
        Views.Editors.MirrorTrace.Write("composition restored event");
        RequestKeyFrame();
    }

    /// <summary>
    /// Claims the single key-frame slot of the current interval, so repeated activations and reattachments cannot
    /// flood the control channel with reset-video messages.
    /// <para>占用当前时隙的唯一关键帧请求名额，使反复激活与重新挂载不会用重置视频消息刷满控制通道。</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the caller may send the request now.
    /// <para>调用方现在可以发送请求时为 <see langword="true"/>。</para>
    /// </returns>
    private bool TryReserveKeyFrameRequest()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastKeyFrameRequestTicks);
        if (previous != 0 && now - previous < KeyFrameMinimumIntervalMilliseconds)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastKeyFrameRequestTicks, now, previous) == previous;
    }

    /// <summary>Sends one Android BACK press. <para>发送一次 Android「返回」按压。</para></summary>
    public void SendBack() => SendSession(client => client.SendBack());

    /// <summary>Sends one Android HOME press. <para>发送一次 Android「主屏幕」按压。</para></summary>
    public void SendHome() => SendKeyTap(3);

    /// <summary>Sends one Android APP_SWITCH (recents) press. <para>发送一次 Android「多任务」按压。</para></summary>
    public void SendRecents() => SendKeyTap(187);

    /// <summary>Sends one volume up or down press. <para>发送一次音量加或音量减按压。</para></summary>
    /// <param name="up">Whether to raise (true) or lower the volume. <para>调高音量为 true，调低为 false。</para></param>
    public void AdjustVolume(bool up) => SendKeyTap(up ? 24 : 25);

    private void SendKeyTap(int keyCode)
    {
        InjectKeyCode(ScrcpyConstants.ActionDown, keyCode, 0);
        InjectKeyCode(ScrcpyConstants.ActionUp, keyCode, 0);
    }

    /// <summary>Toggles the device screen power. <para>切换设备屏幕电源状态。</para></summary>
    public void ToggleDisplayPower() => SendSession(client => client.ToggleDisplayPower());

    private void SendSession(Action<MirrorSessionFacade> send)
    {
        var client = _client;
        if (client is null || !client.IsControlConnected)
        {
            return;
        }

        try
        {
            send(client);
        }
        catch
        {

        }
    }

    private void SendControl(byte[] message)
    {
        var client = _client;
        if (client is null || !client.IsControlConnected)
        {
            return;
        }

        try
        {
            client.SendControl(message);
        }
        catch
        {

        }
    }

    private void DecodeLoop(MirrorSessionFacade client, CancellationToken token, int generation)
    {
        Views.Editors.MirrorTrace.Write($"decode loop start gen={generation}");
        try
        {
            var metadata = client.VideoMetadata ?? throw new InvalidDataException("Missing scrcpy video metadata.");
            var codec = metadata.CodecName.TrimEnd('\0', ' ').ToUpperInvariant() switch
            {
                "H264" => VideoCodecId.H264,
                "H265" or "HEVC" => VideoCodecId.H265,
                "AV1" or "AV01" => VideoCodecId.Av1,
                _ => throw new NotSupportedException($"Unsupported video codec '{metadata.CodecName}'."),
            };
            var probeDeadline = Environment.TickCount64 + 3000;
            while (!GpuPresenter.CompositionProbeCompleted && Environment.TickCount64 < probeDeadline) Thread.Sleep(25);
            var options = ActiveDecoderOptions;
            var rebuilds = 0;
            while (!token.IsCancellationRequested)
            {
                using var decoder = VideoDecoderFactory.Create(codec, options);
                decoder.Initialize(codec, metadata.Width, metadata.Height, frame => OnDecodedFrame(frame, generation));
                var active = VideoDecoderFactory.GetActiveDecoder(decoder);
                if (active is VaApiVideoDecoder vaApi && OperatingSystem.IsLinux()) EnableVaApiZeroCopy(vaApi, generation);
                else if (active is MediaFoundationGpuVideoDecoder mediaFoundation && OperatingSystem.IsWindows()) EnableSharedTextureExport(mediaFoundation, generation);
                else if (active is VideoToolboxVideoDecoder videoToolbox && OperatingSystem.IsMacOS()) EnableIosurfaceExport(videoToolbox, generation);
                _decoderBackend = VideoDecoderFactory.GetBackendName(decoder);
                _pixelFormatDecoder = decoder;
                Views.Editors.MirrorTrace.Write($"decoder ready backend={_decoderBackend} format={MirrorFrameFormat} gpuExport={options.MediaFoundationPreferGpuExport} presentation={GpuPresenter.PresentationPath}");
                if (VideoDecoderFactory.GetFallbackReason(decoder) is { } reason)
                    Services.LogService.Instance.Write(Services.LogLevel.Warn,
                        "ScreenMirror", $"GPU initialization failed; using CPU: {reason}");
                byte[]? configuration = null;
                var failures = 0;
                var skipped = 0;
                var awaitBoundary = rebuilds > 0;
                var boundaryKeyFrameRequestTicks = Environment.TickCount64;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        ScrcpyVideoFrame? packet = client.ReadFrame(1000);
                        if (packet is null)
                        {
                            if (Environment.TickCount64 - _lastIdleTrace >= 1000)
                            {
                                _lastIdleTrace = Environment.TickCount64;
                                Views.Editors.MirrorTrace.Write($"decode idle: no packet (decoded={Interlocked.Read(ref _decodedFrames)} received={Interlocked.Read(ref _receivedFrames)})");
                            }
                            try { decoder.Poll(); failures = 0; }
                            catch (Exception error) when (MirrorDecoderRecovery.IsRecoverable(error))
                            {
                                if (++failures < MirrorDecoderRecovery.ConsecutiveFailuresBeforeRebuild) continue;
                                throw;
                            }

                            continue;
                        }

                        try
                        {
                            Interlocked.Add(ref _receivedBytes, packet.PayloadLength);
                            if (awaitBoundary && !packet.IsCodecConfig)
                            {
                                skipped++;
                                var now = Environment.TickCount64;
                                if (now - boundaryKeyFrameRequestTicks >= KeyFrameRequestRepeatMilliseconds)
                                {
                                    boundaryKeyFrameRequestTicks = now;
                                    RequestKeyFrame();
                                }

                                continue;
                            }
                            awaitBoundary = false;
                            if (packet.IsCodecConfig)
                            {
                                if (packet.PayloadLength > 1024 * 1024) throw new InvalidDataException("Excessive scrcpy codec configuration.");
                                configuration = packet.Data;
                                continue;
                            }
                            var timestampTicks = checked(packet.PresentationTimestampMicroseconds * 10);
                            Interlocked.Increment(ref _receivedFrames);
                            if (_packetLogged++ % 60 == 0)
                                Views.Editors.MirrorTrace.Write($"decode packet #{_packetLogged} skipped={skipped} failures={failures} decoded={Interlocked.Read(ref _decodedFrames)} extra={configuration?.Length}");
                            try
                            {
                                var decodeStarted = Environment.TickCount64;
                                if (configuration is null) decoder.Decode(packet.Data.AsSpan(0, packet.PayloadLength), timestampTicks);
                                else
                                {
                                    var length = checked(configuration.Length + packet.PayloadLength);
                                    if (length > 32 * 1024 * 1024) throw new InvalidDataException("Excessive scrcpy access unit.");
                                    var combined = ArrayPool<byte>.Shared.Rent(length);
                                    try
                                    {
                                        configuration.CopyTo(combined, 0);
                                        packet.Data.AsSpan(0, packet.PayloadLength).CopyTo(combined.AsSpan(configuration.Length));
                                        decoder.Decode(combined.AsSpan(0, length), timestampTicks);
                                        configuration = null;
                                    }
                                    finally { ArrayPool<byte>.Shared.Return(combined); }
                                }
                                var decodeElapsed = Environment.TickCount64 - decodeStarted;
                                if (decodeElapsed > 200)
                                    Views.Editors.MirrorTrace.Write($"decode slow {decodeElapsed} ms failures={failures} decoded={Interlocked.Read(ref _decodedFrames)}");
                                if (failures != 0) failures = 0;
                            }
                            catch (Exception error) when (MirrorDecoderRecovery.IsRecoverable(error))
                            {
                                if (_decodeErrorLogged++ < 10)
                                    Views.Editors.MirrorTrace.Write($"decode recoverable failures={failures + 1}: {error.GetType().Name}: {error.Message}");
                                if (++failures < MirrorDecoderRecovery.ConsecutiveFailuresBeforeRebuild) continue;
                                throw;
                            }
                        }
                        finally
                        {
                            if (!packet.IsCodecConfig) packet.Dispose();
                        }
                    }
                    return;
                }
                catch (Exception error) when (!token.IsCancellationRequested
                        && failures >= MirrorDecoderRecovery.ConsecutiveFailuresBeforeRebuild
                        && rebuilds < MirrorDecoderRecovery.RebuildAttemptsPerSession
                        && MirrorDecoderRecovery.Degrade(options, MirrorDecoderRecovery.BackendOf(active)) is { } degraded)
                {
                    options = degraded;
                    rebuilds++;
                    Views.Editors.MirrorTrace.Write($"decode rebuild #{rebuilds}: {error.GetType().Name}: {error.Message}");
                    Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror",
                        $"Video decoding failed after {failures} frames ({error.Message}); rebuilding the decoder without hardware acceleration, {skipped} packets skipped while waiting for a key frame.");
                    SendControl(ScrcpyControlMessageWriter.ResetVideo());
                }
            }
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            Views.Editors.MirrorTrace.Write($"decode loop failed: {exception.GetType().Name}: {exception.Message}");
            Services.LogService.Instance.Write(
                Services.LogLevel.Error, "ScreenMirror", $"Decode failed: {exception}");
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed) StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + Services.UserError.Describe(exception);
            });
        }
        catch (Exception) when (token.IsCancellationRequested) { Views.Editors.MirrorTrace.Write("decode loop cancelled"); }
    }

    private void OnDecodedFrame(DecodedVideoFrame frame, int generation)
    {
        if (!IsCurrentSession(generation))
        {
            return;
        }

        try
        {
            OnDecodedFrameCore(frame);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportFrameFailure("decode callback", error);
        }
    }

    private void OnDecodedFrameCore(DecodedVideoFrame frame)
    {
        Interlocked.Increment(ref _decodedFrames);
        if (_disposed) return;
        if (_decodeLoggedThrottle++ % 60 == 0)
            Views.Editors.MirrorTrace.Write($"decoded frame {frame.Width}x{frame.Height} {frame.PixelFormat} available={GpuPresenter.IsAvailable} format={MirrorFrameFormat}");
        if (GpuPresenter.IsAvailable && GpuPresenter.Publish(frame, _activeSettings.ScreenMirrorColorMatrix, _activeSettings.ScreenMirrorColorRange))
        {
            ScheduleRender();
            return;
        }
        Views.Editors.ScreenMirrorPixelBuffer next;
        lock (_frameLock)
        {
            if (_disposed) return;

            next = _pendingFrame ?? _spareFrame ?? new Views.Editors.ScreenMirrorPixelBuffer();
            if (_pendingFrame is not null) _pendingFrame = null;
            else _spareFrame = null;
        }
        next.Update(frame, _activeSettings.ScreenMirrorColorMatrix, _activeSettings.ScreenMirrorColorRange);
        ScheduleRenderLocked(next);
    }

    private void ReportFrameFailure(string stage, Exception error)
    {
        if (Interlocked.Exchange(ref _frameFailureLogged, 1) != 0)
        {
            return;
        }

        Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror",
            $"Dropped a video frame after a {stage} failure; the session stays connected: {error}");
    }

    /// <summary>Routes exported VA surfaces to the GPU presenter; the presenter clears the flag when it cannot present them.
    /// <para>把导出的 VA 表面路由到 GPU 呈现器；呈现器在无法呈现时会清除该标志。</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void EnableVaApiZeroCopy(VaApiVideoDecoder decoder, int generation)
    {
        decoder.DmabufFrameCallback = frame => PublishDmabufFrame(frame, decoder, generation);
        decoder.GpuExportEnabled = true;
    }

    /// <summary>Hands a zero-copy exported surface to the GPU presenter, disabling export when it cannot be presented.
    /// <para>把零拷贝导出表面交给 GPU 呈现器；无法呈现时禁用导出路径。</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void PublishDmabufFrame(VaDmaBufVideoFrame frame, VaApiVideoDecoder decoder, int generation)
    {
        try
        {
            if (!IsCurrentSession(generation))
            {
                frame.Dispose();
                return;
            }

            Interlocked.Increment(ref _decodedFrames);
            if (_disposed) { frame.Dispose(); return; }
            if (!GpuPresenter.PublishDmabuf(frame))
            {

                decoder.GpuExportEnabled = false;
                frame.Dispose();
                return;
            }
            ScheduleRender();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            frame.Dispose();
            ReportFrameFailure("dma-buf presentation", error);
        }
    }

    /// <summary>Routes Windows shared-texture frames to the composition presenter; export is disabled when it cannot be presented.
    /// <para>把 Windows 共享纹理帧交给合成呈现器；无法呈现时禁用导出。</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void EnableSharedTextureExport(MediaFoundationGpuVideoDecoder decoder, int generation)
    {
        GpuPresenter.CompositionFailed += (_, _) =>
        {
            Views.Editors.MirrorTrace.Write("composition failed event: disabling shared texture export");
            decoder.GpuExportEnabled = false;
        };
        decoder.GpuExportFrameCallback = frame => PublishSharedTextureFrame(frame, decoder, generation);
        decoder.GpuExportEnabled = true;
        Services.LogService.Instance.Write(Services.LogLevel.Info, "ScreenMirror",
            "GPU zero-copy export enabled: D3D11 shared texture.");
    }

    [SupportedOSPlatform("windows")]
    private void PublishSharedTextureFrame(D3D11SharedVideoFrame frame, MediaFoundationGpuVideoDecoder decoder,
        int generation)
    {
        try
        {
            if (!IsCurrentSession(generation))
            {
                frame.MarkPresented();
                return;
            }

            Interlocked.Increment(ref _decodedFrames);
            if (_disposed)
            {
                frame.MarkPresented();
                return;
            }
            if (_exportFrameLogged++ % 60 == 0)
                Views.Editors.MirrorTrace.Write($"export frame {frame.Width}x{frame.Height} exportEnabled={decoder.GpuExportEnabled}");
            if (!GpuPresenter.PublishSharedTexture(frame))
            {
                decoder.GpuExportEnabled = false;
                return;
            }
            ScheduleRender();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            frame.MarkPresented();
            ReportFrameFailure("shared-texture presentation", error);
        }
    }

    /// <summary>Routes macOS IOSurface frames to the composition presenter; export is disabled when it cannot be presented.
    /// <para>把 macOS IOSurface 帧交给合成呈现器；无法呈现时禁用导出。</para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    private void EnableIosurfaceExport(VideoToolboxVideoDecoder decoder, int generation)
    {
        GpuPresenter.CompositionFailed += (_, _) => decoder.GpuExportEnabled = false;
        decoder.IosurfaceFrameCallback = frame => PublishIosurfaceFrame(frame, decoder, generation);
        decoder.GpuExportEnabled = true;
    }

    [SupportedOSPlatform("macos")]
    private void PublishIosurfaceFrame(IosurfaceVideoFrame frame, VideoToolboxVideoDecoder decoder, int generation)
    {
        try
        {
            if (!IsCurrentSession(generation))
            {
                frame.MarkPresented();
                return;
            }

            Interlocked.Increment(ref _decodedFrames);
            if (_disposed)
            {
                frame.MarkPresented();
                return;
            }
            if (!GpuPresenter.PublishIosurface(frame))
            {
                decoder.GpuExportEnabled = false;
                return;
            }
            ScheduleRender();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            frame.MarkPresented();
            ReportFrameFailure("IOSurface presentation", error);
        }
    }

    private void ScheduleRender()
    {
        lock (_frameLock)
        {
            if (!_renderScheduled && !_disposed)
            {
                _renderScheduled = true;
                Dispatcher.UIThread.Post(RenderPendingFrame, DispatcherPriority.Render);
            }
        }
    }

    private void ScheduleRenderLocked(Views.Editors.ScreenMirrorPixelBuffer next)
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            _pendingFrame = next;
            if (!_renderScheduled)
            {
                _renderScheduled = true;
                Dispatcher.UIThread.Post(RenderPendingFrame, DispatcherPriority.Render);
            }
        }
    }

    private void RenderPendingFrame()
    {
        var compositionCovering = GpuPresenter.CompositionCoversVideo;
        Views.Editors.ScreenMirrorPixelBuffer? pending;
        lock (_frameLock)
        {
            _renderScheduled = false;
            pending = _pendingFrame;
            _pendingFrame = null;
            if (compositionCovering && GpuPresenter.PixelSize.Width > 0)
            {
                pending = null;
                _spareFrame = null;
            }
        }
        if (_renderLoggedThrottle++ % 30 == 0)
            Views.Editors.MirrorTrace.Write($"vm render covering={compositionCovering} pending={(pending is not null)} frame={Frame is not null} available={GpuPresenter.IsAvailable} pixel={GpuPresenter.PixelSize.Width}x{GpuPresenter.PixelSize.Height}");
        if (pending is null)
        {
            var gpuDraws = compositionCovering || GpuPresenter.CanPresentFallback;
            if (gpuDraws && GpuPresenter.PixelSize.Width > 0 && Frame is { } oldBitmap)
            {
                Frame = null;
                oldBitmap.Dispose();
            }
            if (!_disposed) FramePresented?.Invoke(this, EventArgs.Empty);
            return;
        }
        try
        {
            if (!_disposed)
            {
                RenderBgra(pending.Pixels, pending.Width, pending.Height, pending.Width * 4);
                Interlocked.Increment(ref _uploadedFrames);
                FramePresented?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            lock (_frameLock)
            {
                if (!_disposed) _spareFrame = pending;
            }
        }
    }

    private unsafe void RenderBgra(byte[] bgra, int width, int height, int sourceStride)
    {
        var bitmap = Frame;
        if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormats.Bgra8888);
            Frame = bitmap;
        }

        using var framebuffer = bitmap.Lock();
        var stride = framebuffer.RowBytes;
        var rowBytes = Math.Min(width * 4, stride);
        var dst = framebuffer.Address;
        var total = rowBytes * (long)height;
        if (sourceStride == stride && rowBytes == stride && total <= bgra.Length)
        {
            fixed (byte* source = bgra)
            {
                Buffer.MemoryCopy(source, (void*)dst, total, total);
            }
            return;
        }

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(bgra, row * sourceStride, IntPtr.Add(dst, row * stride), rowBytes);
        }
    }

    private static string GetLocalizedString(string key)
        => Services.LanguageService.Instance.GetString(key);

    /// <summary>
    /// Builds server options from the persisted app settings and the resolved jar version. Audio is
    /// dropped for devices older than Android 11 because the scrcpy server refuses to start its audio
    /// capture there and the whole mirror would fail with a missing abstract socket.
    /// <para>按持久化应用设置与已解析的 jar 版本构建服务端选项。Android 11 之前的设备会关闭音频，
    /// 因为 scrcpy 服务端在这些设备上拒绝启动音频采集，否则整个投屏会因抽象套接字缺失而失败。</para>
    /// </summary>
    /// <param name="serverVersion">Version string pushed with the jar. <para>随 jar 推送的版本串。</para></param>
    /// <param name="allowAudio">Whether the device supports scrcpy audio capture.
    /// <para>设备是否支持 scrcpy 音频采集。</para></param>
    private ScrcpyServerOptions CreateOptionsFromSettings(string serverVersion, bool allowAudio)
    {
        _activeSettings = PendingSettings.Copy();
        var settings = _activeSettings;
        return new ScrcpyServerOptions
        {
            ServerVersion = serverVersion,
            VideoBitRate = settings.ScreenMirrorBitRate,
            MaxFps = settings.ScreenMirrorMaxFps,
            MaxSize = settings.ScreenMirrorMaxSize,
            VideoCodec = settings.ScreenMirrorVideoCodec,
            Audio = allowAudio && settings.ScreenMirrorAudioEnabled,
            AudioCodec = settings.ScreenMirrorAudioCodec,
            Control = settings.ScreenMirrorControl,
        };
    }

    /// <summary>
    /// Builds the HDC capture options from a settings snapshot: 0 fps means unlimited and becomes 60 fps, the
    /// frame rate is clamped to 1..120, the bitrate to 1..50 Mbps, and a key frame is forced every 2000 ms.
    /// <para>按设置快照构建 HDC 采集选项：0 fps 表示不限并取 60 fps，帧率收敛到 1..120，码率收敛到
    /// 1..50 Mbps，并每 2000 毫秒强制一个关键帧。</para>
    /// </summary>
    /// <param name="settings">Settings snapshot the values are read from.
    /// <para>读取这些值的设置快照。</para></param>
    /// <returns>The launch options passed to the HDC facade.
    /// <para>传给 HDC 门面的启动选项。</para></returns>
    private static OhscrcpyLaunchOptions CreateHdcLaunchOptions(Services.AppSettings settings)
        => new(
            Math.Clamp(settings.ScreenMirrorMaxFps == 0 ? 60 : settings.ScreenMirrorMaxFps, 1, 120),
            Math.Clamp(settings.ScreenMirrorBitRate, 1_000_000, 50_000_000),
            2_000);

    private static bool SupportsScrcpyAudio(DetectedDevice device)
    {
        try
        {
            var sdk = App.DeviceMonitor.RunAdb(device, session =>
                Encoding.UTF8.GetString(session.ExecuteCommand("getprop ro.build.version.sdk").Stdout).Trim());
            return int.TryParse(sdk, out var value) && value >= 30;
        }
        catch
        {
            return false;
        }
    }
}