using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Media.Audio;
using UotanStudio.Media.Decoding;
using UotanStudio.Services.Mirroring;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>scrcpy 投屏文档：部署服务端、解码视频流，并交接到 GPU 呈现器或位图回退。</para>
/// scrcpy screen-mirror document: deploys the server, decodes the H.264/H.265
/// video stream and hands frames to GPU presentation or the bitmap fallback.
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
public partial class ScreenMirrorViewModel : Document
{
    private Services.Mirroring.ManagedMirrorSession? _client;
    private int _starting;
    private readonly object _frameLock = new();
    private Views.Editors.ScreenMirrorPixelBuffer? _pendingFrame;
    private Views.Editors.ScreenMirrorPixelBuffer? _spareFrame;
    private bool _renderScheduled;
    internal Views.Editors.ScreenMirrorGpuPresenter GpuPresenter { get; private set; } = new();
    internal PixelSize VideoPixelSize => GpuPresenter.IsAvailable && GpuPresenter.PixelSize is { Width: > 0 } size ? size : Frame?.PixelSize ?? default;
    // At most two session-owned buffers: reuse the pending frame before writing
    // its replacement, while the UI may own the other buffer. Only exchange
    // ownership under the lock; bitmap.Lock() must never hold up the decoder.
    private CancellationTokenSource? _cts;
    private Thread? _decodeThread;
    private Thread? _audioThread;
    private volatile bool _disposed;

    /// <summary>Raised on the audio thread after the output sink opened successfully.</summary>
    public event EventHandler? AudioSinkOpened;

    /// <summary>Gets the total decoded audio frames; 0 while audio is off or unsupported.</summary>
    /// <summary>获取累计解码音频帧数；音频关闭或编码不受支持时为 0。</summary>
    public long AudioDecodedFrames => Interlocked.Read(ref _audioDecodedFrames);

    /// <summary>Gets the decoded audio sample rate; 0 until the first decoded frame.</summary>
    /// <summary>获取解码音频采样率；首帧解码前为 0。</summary>
    public int AudioSampleRate => Volatile.Read(ref _audioSampleRate);

    /// <summary>Gets the decoded audio channel count; 0 until the first decoded frame.</summary>
    /// <summary>获取解码音频声道数；首帧解码前为 0。</summary>
    public int AudioChannels => Volatile.Read(ref _audioChannels);

    /// <summary>Gets whether the session requested an audio stream.</summary>
    /// <summary>获取本会话是否请求了音频流。</summary>
    public bool AudioEnabled => StreamOptions?.Audio ?? false;

    /// <summary>Raised on the UI thread when a GPU frame is available or the fallback bitmap is updated; not a display-present timestamp.</summary>
    public event EventHandler? FramePresented;

    private long _receivedFrames, _decodedFrames, _uploadedFrames, _receivedBytes;
    private long _audioDecodedFrames;
    private int _audioSampleRate, _audioChannels;
    private string _decoderBackend = string.Empty;
    private ScrcpyServerOptions? _streamOptions;
    /// <summary>Session parameters and counters; frame rates are calculated from counter deltas.</summary>
    internal (long Received, long Decoded, long Uploaded, long Bytes) ReadCounters()
        => (Interlocked.Read(ref _receivedFrames), Interlocked.Read(ref _decodedFrames),
            Interlocked.Read(ref _uploadedFrames) + GpuPresenter.Draws, Interlocked.Read(ref _receivedBytes));
    internal string DecoderBackend => _decoderBackend;
    internal ScrcpyServerOptions? StreamOptions => _streamOptions;
    internal ScrcpyVideoMetadata? StreamMetadata => _client?.VideoMetadata;
    internal Services.Mirroring.ScrcpyAudioMetadata? StreamMetadataAudio => _client?.AudioMetadata;
    internal bool IsStopped => _disposed;
    internal bool AudioDisabledByServer => _client?.AudioDisabledByServer == true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private WriteableBitmap? _frame;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    /// <summary>
    /// <para>按序列号解析设备并启动投屏（部署/连接在后台线程执行）。</para>
    /// Resolves the device by serial and starts mirroring (deploy/connect runs on a background thread).
    /// </summary>
    /// <param name="serial"><para>目标设备序列号。</para>The target device serial.</param>
    public void StartForDevice(string serial)
    {
        if (_disposed || _client is not null || Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return;

        _serial = serial;
        ApplyMirrorSettingsCommand.NotifyCanExecuteChanged();
        StatusText = GetLocalizedString("ScreenMirror_Preparing");
        _startTask = Task.Run(() =>
        {
            try
            {
                var device = App.DeviceMonitor.Scan()
                    .SingleOrDefault(candidate => candidate.Serial == serial &&
                        candidate.Protocol == Services.Devices.DeviceProtocolKind.Adb)
                    ?? throw new InvalidOperationException($"Device {serial} is not connected.");
                var options = CreateOptionsFromSettings();
                _streamOptions = options;
                StatusText = GetLocalizedString("ScreenMirror_Installing");
                var mirror = App.DeviceMonitor.StartMirror(
                    device, ResolveServerJarPath(), options);
                if (_disposed)
                {
                    mirror.Dispose();
                    return;
                }
                _client = mirror;
                if (_disposed)
                {
                    // 页面在部署期间关闭时，Cleanup 可能已在本赋值前完成清理；这里兜底释放
                    // 刚建立的会话，避免设备端 scrcpy 服务与 shell 流随页面关闭而泄漏。
                    _client = null;
                    mirror.Dispose();
                    return;
                }
                StatusText = GetLocalizedString("ScreenMirror_Connecting");
                BeginStreaming();
            }
            catch (Exception ex)
            {
                StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + global::UotanStudio.Services.UserError.Describe(ex);
                UotanStudio.Services.LogService.Instance.Write(
                    UotanStudio.Services.LogLevel.Error, "ScreenMirror", $"Start failed: {ex.Message}");
                _client?.Dispose(); _client = null;
            }
            finally { Interlocked.Exchange(ref _starting, 0); }
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

        _cts = new CancellationTokenSource();
        var decodeClient = client;
        var decodeToken = _cts.Token;
        _decodeThread = new Thread(() => DecodeLoop(decodeClient, decodeToken))
        {
            IsBackground = true,
            Name = "UotanStudio.ScreenMirror",
        };
        _decodeThread.Start();
        if (decodeClient.AudioMetadata is not null)
        {
            // Consume audio continuously so the server's audio socket never
            // fills up and stalls the whole mirroring process; AAC is decoded
            // and played, unsupported codecs keep draining.
            // 持续消费音频，避免服务端音频 socket 缓冲填满阻塞整个投屏进程；
            // AAC 解码并播放，不支持的编码保持排空。
            _audioThread = new Thread(() => AudioLoop(decodeClient, decodeToken))
            {
                IsBackground = true,
                Name = "UotanStudio.ScreenMirror.Audio",
            };
            _audioThread.Start();
        }
    }

    /// <summary>
    /// <para>工作区文档关闭时的清理入口：停止解码循环并断开所有 socket。</para>
    /// Cleanup entry invoked by the workspace when the document closes: stops
    /// the decode loop and disconnects every socket.
    /// </summary>
    public void Cleanup()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GpuPresenter.Dispose();
        _cts?.Cancel();
        _client?.Dispose(); // Unblock socket reads before waiting; decoder disposal stays on its owner thread.
        lock (_frameLock)
        {
            _pendingFrame = null;
            _spareFrame = null;
        }
        try
        {
            _decodeThread?.Join(2000);
        }
        catch
        {
            // Join may fail if the thread already exited. 线程已退出时 Join 可能失败。
        }
        try
        {
            _audioThread?.Join(2000);
        }
        catch
        {
            // Join may fail if the thread already exited. 线程已退出时 Join 可能失败。
        }
        _audioThread = null;

        _client?.Dispose();

        _client = null;
        _cts?.Dispose();
        _cts = null;

        // Clear on the owning UI thread; capture the old bitmap so a queued cleanup
        // cannot dispose a replacement created by a subsequent reconnect.
        var oldFrame = Frame;
        void ReleaseBitmap()
        {
            if (ReferenceEquals(Frame, oldFrame)) Frame = null;
            oldFrame?.Dispose();
            FramePresented?.Invoke(this, EventArgs.Empty);
        }
        if (Dispatcher.UIThread.CheckAccess()) ReleaseBitmap();
        else Dispatcher.UIThread.Post(ReleaseBitmap);

        StatusText = GetLocalizedString("ScreenMirror_Stopped");
    }

    /// <summary>
    /// <para>注入触摸事件（视频像素坐标）。</para>
    /// Injects a touch event (video pixel coordinates).
    /// </summary>
    /// <param name="action"><para>动作：按下/抬起/移动。</para>Motion action: down/up/move.</param>
    /// <param name="pointerId"><para>指针 ID（鼠标为 <see cref="ScrcpyConstants.PointerIdMouse"/>）。</para>Pointer id (<see cref="ScrcpyConstants.PointerIdMouse"/> for mouse).</param>
    /// <param name="x"><para>视频像素坐标 X。</para>Video pixel X.</param>
    /// <param name="y"><para>视频像素坐标 Y。</para>Video pixel Y.</param>
    /// <param name="width"><para>解码帧宽度。</para>Decoded frame width.</param>
    /// <param name="height"><para>解码帧高度。</para>Decoded frame height.</param>
    /// <param name="buttons"><para>当前按键状态掩码。</para>Current button state mask.</param>
    /// <param name="actionButton">Button that changed on press/release; zero for movement or cancellation.</param>
    public void InjectTouch(byte action, long pointerId, int x, int y, int width, int height, uint buttons, uint actionButton = 0)
        => SendControl(ScrcpyControlMessageWriter.InjectTouchEvent(action, pointerId, x, y, width, height,
            pressure: action is ScrcpyConstants.ActionUp or 3 ? (ushort)0 : ushort.MaxValue,
            actionButton: actionButton, buttons: buttons));

    /// <summary>
    /// <para>注入滚轮事件（视频像素坐标）。</para>
    /// Injects a scroll event (video pixel coordinates).
    /// </summary>
    /// <param name="x"><para>视频像素坐标 X。</para>Video pixel X.</param>
    /// <param name="y"><para>视频像素坐标 Y。</para>Video pixel Y.</param>
    /// <param name="width"><para>解码帧宽度。</para>Decoded frame width.</param>
    /// <param name="height"><para>解码帧高度。</para>Decoded frame height.</param>
    /// <param name="delta"><para>滚动刻度：正数向下滚动。</para>Scroll ticks: positive scrolls down.</param>
    public void InjectScroll(int x, int y, int width, int height, float delta)
        => SendControl(ScrcpyControlMessageWriter.InjectScrollEvent(
            x, y, width, height,
            0,
            // 客户端向下滚动在线路上为负（服务端正数表示向上）。
            // Scroll-down is negative on the wire (server positive = scroll up).
            -delta));

    /// <summary>
    /// <para>注入 Android 键码事件。</para>
    /// Injects an Android keycode event.
    /// </summary>
    /// <param name="action"><para>动作：按下/抬起。</para>Action: down/up.</param>
    /// <param name="keyCode"><para>Android 键码。</para>Android keycode.</param>
    /// <param name="metaState"><para>修饰键掩码。</para>Modifier mask.</param>
    public void InjectKeyCode(byte action, int keyCode, int metaState)
        => SendControl(ScrcpyControlMessageWriter.InjectKeyCode(action, keyCode, 0, metaState));

    /// <summary>
    /// <para>注入 UTF-8 文本。</para>
    /// Injects UTF-8 text.
    /// </summary>
    /// <param name="text"><para>要注入的文本（≤300 字节）。</para>Text to inject (≤300 bytes).</param>
    public void InjectText(string text)
        => SendControl(ScrcpyControlMessageWriter.InjectText(text));

    private void SendControl(byte[] message)
    {
        var client = _client;
        if (client is null || !client.IsControlConnected)
        {
            return; // Not connected / control disabled: silently drop. 未连接或控制禁用时静默丢弃。
        }

        try
        {
            client.SendControl(message);
        }
        catch
        {
            // A failed inject must not crash the mirror session.
            // 注入失败不应导致投屏会话崩溃。
        }
    }

    /// <summary>Locates the bundled scrcpy-server jar next to the executable.</summary>
    /// <summary>在可执行文件旁定位随附的 scrcpy-server jar。</summary>
    private static string ResolveServerJarPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "scrcpy-server.jar"),
            Path.Combine(AppContext.BaseDirectory, "scrcpy-server.jar"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"scrcpy server jar not found. Place scrcpy-server {ScrcpyConstants.ServerVersion} at Assets/scrcpy-server.jar. "
            + $"未找到 scrcpy 服务端 jar。请将 scrcpy-server {ScrcpyConstants.ServerVersion} 放置到 Assets/scrcpy-server.jar。");
    }

    private void DecodeLoop(Services.Mirroring.ManagedMirrorSession client, CancellationToken token)
    {
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
            using var decoder = VideoDecoderFactory.Create(codec, ActiveDecoderOptions);
            decoder.Initialize(codec, metadata.Width, metadata.Height, OnDecodedFrame);
            _decoderBackend = VideoDecoderFactory.GetBackendName(decoder);
            if (VideoDecoderFactory.GetFallbackReason(decoder) is { } reason)
                UotanStudio.Services.LogService.Instance.Write(UotanStudio.Services.LogLevel.Warn,
                    "ScreenMirror", $"GPU initialization failed; using CPU: {reason}");
            byte[]? configuration = null;
            while (!token.IsCancellationRequested)
            {
                ScrcpyVideoFrame? packet = client.ReadFrame(1000);
                if (packet is null) { decoder.Poll(); continue; }
                Interlocked.Add(ref _receivedBytes, packet.Data.Length);
                if (packet.IsCodecConfig)
                {
                    // Some MFTs require parameter sets and the first picture in a single sample.
                    // Keep the complete scrcpy config packet and prepend it to the next picture.
                    if (packet.Data.Length > 1024 * 1024) throw new InvalidDataException("Excessive scrcpy codec configuration.");
                    configuration = packet.Data;
                    continue;
                }
                long timestampTicks = checked(packet.PresentationTimestampMicroseconds * 10);
                Interlocked.Increment(ref _receivedFrames);
                if (configuration is null) decoder.Decode(packet.Data, timestampTicks);
                else
                {
                    int length = checked(configuration.Length + packet.Data.Length);
                    if (length > 32 * 1024 * 1024) throw new InvalidDataException("Excessive scrcpy access unit.");
                    byte[] combined = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        configuration.CopyTo(combined, 0);
                        packet.Data.CopyTo(combined, configuration.Length);
                        decoder.Decode(combined.AsSpan(0, length), timestampTicks);
                        configuration = null;
                    }
                    finally { ArrayPool<byte>.Shared.Return(combined); }
                }
            }
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            UotanStudio.Services.LogService.Instance.Write(
                UotanStudio.Services.LogLevel.Error, "ScreenMirror", $"Decode failed: {exception.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed) StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + global::UotanStudio.Services.UserError.Describe(exception);
            });
        }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private void OnDecodedFrame(DecodedVideoFrame frame)
    {
        Interlocked.Increment(ref _decodedFrames);
        if (_disposed) return;
        if (GpuPresenter.IsAvailable && GpuPresenter.Publish(frame, _activeSettings.ScreenMirrorColorMatrix, _activeSettings.ScreenMirrorColorRange))
        {
            lock (_frameLock)
            {
                if (!_renderScheduled && !_disposed)
                {
                    _renderScheduled = true;
                    Dispatcher.UIThread.Post(RenderPendingFrame, DispatcherPriority.Render);
                }
            }
            return;
        }
        Views.Editors.ScreenMirrorPixelBuffer next;
        lock (_frameLock)
        {
            if (_disposed) return;
            // A pending frame has not reached the UI and can be replaced safely.
            // Take it before conversion so a third full-size buffer is unnecessary.
            next = _pendingFrame ?? _spareFrame ?? new Views.Editors.ScreenMirrorPixelBuffer();
            if (_pendingFrame is not null) _pendingFrame = null;
            else _spareFrame = null;
        }
        next.Update(frame, _activeSettings.ScreenMirrorColorMatrix, _activeSettings.ScreenMirrorColorRange);
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
        Views.Editors.ScreenMirrorPixelBuffer? pending;
        lock (_frameLock)
        {
            _renderScheduled = false;
            pending = _pendingFrame;
            _pendingFrame = null;
            if (GpuPresenter.IsAvailable && GpuPresenter.PixelSize.Width > 0)
            {
                pending = null;
                _spareFrame = null;
            }
        }
        if (pending is null)
        {
            if (GpuPresenter.IsAvailable && GpuPresenter.PixelSize.Width > 0 && Frame is { } oldBitmap)
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

    private void RenderBgra(byte[] bgra, int width, int height, int sourceStride)
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
        // 逐行复制到锁定帧缓冲，保证源行距（GPU 回读可能带填充）与位图行距不一致时的安全。
        // Copy row by row into the locked framebuffer so a source-row-pitch mismatch
        // (GPU readback may include padding) against the bitmap stride stays safe.
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(bgra, row * sourceStride, IntPtr.Add(dst, row * stride), rowBytes);
        }
    }

    private static string GetLocalizedString(string key)
        => UotanStudio.Services.LanguageService.Instance.GetString(key);

    /// <summary>Builds server options from the persisted app settings.</summary>
    /// <summary>按持久化应用设置构建服务端选项。</summary>
    private ScrcpyServerOptions CreateOptionsFromSettings()
    {
        _activeSettings = PendingSettings.Copy();
        var settings = _activeSettings;
        return new ScrcpyServerOptions
        {
            VideoBitRate = settings.ScreenMirrorBitRate,
            MaxFps = settings.ScreenMirrorMaxFps,
            MaxSize = settings.ScreenMirrorMaxSize,
            VideoCodec = settings.ScreenMirrorVideoCodec,
            Audio = settings.ScreenMirrorAudioEnabled,
            AudioCodec = settings.ScreenMirrorAudioCodec,
            Control = settings.ScreenMirrorControl,
        };
    }
}