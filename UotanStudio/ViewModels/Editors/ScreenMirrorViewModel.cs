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
using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.Media.Decoding;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>scrcpy 投屏文档：部署服务端、解码 H.264/H.265 视频流并渲染到 <see cref="Frame"/>。</para>
/// scrcpy screen-mirror document: deploys the server, decodes the H.264/H.265
/// video stream and renders it into <see cref="Frame"/>.
/// </summary>
/// <remarks>
/// <para>
/// 解码循环运行在专用线程：scrcpy 帧解包 → <see cref="IVideoDecoder"/> 平台解码 →
/// NV12→BGRA 转换 → 经 Dispatcher 在 UI 线程写入 <see cref="WriteableBitmap"/>。
/// 回调契约要求在解码回调内完成转换（帧随后被解码器释放）。
/// </para>
/// <para>
/// The decode loop runs on a dedicated thread: scrcpy frame demux →
/// <see cref="IVideoDecoder"/> platform decode → NV12→BGRA conversion →
/// WriteableBitmap update on the UI thread via the Dispatcher. The decoder
/// callback contract requires the conversion to happen inside the callback
/// (the frame is disposed right after it returns).
/// </para>
/// </remarks>
public partial class ScreenMirrorViewModel : Document
{
    private const int ConnectRetryCount = 20;
    private const int ConnectRetryDelayMs = 250;
    private const int ReadTimeoutMs = 3000;

    private AdbSession? _session;
    private ScrcpyClient? _client;
    private AdbStream? _shellStream;
    private readonly object _frameLock = new();
    private PendingFrame? _pendingFrame;
    private bool _renderScheduled;
    private sealed record PendingFrame(byte[] Pixels, int Length, int Width, int Height);
    private CancellationTokenSource? _cts;
    private Thread? _decodeThread;
    private volatile bool _disposed;

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
        if (_session is not null)
        {
            return; // Already running. 已在运行。
        }

        StatusText = GetLocalizedString("ScreenMirror_Preparing");
        Task.Run(() =>
        {
            try
            {
                var device = UotanStudio.Services.Devices.DeviceMonitorService.Instance.FindDeviceBySerial(serial)
                    ?? throw new InvalidOperationException($"Device {serial} is not connected. 设备 {serial} 未连接。");
                var session = UotanStudio.Services.Devices.DeviceMonitorService.Instance.OpenAdbSession(device);
                if (session is null)
                {
                    throw new InvalidOperationException($"ADB session for {serial} unavailable. 无法建立 {serial} 的 ADB 会话。");
                }

                Start(session, CreateOptionsFromSettings());
            }
            catch (Exception ex)
            {
                StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + ex.Message;
                UotanStudio.Services.LogService.Instance.Write(
                    UotanStudio.Services.LogLevel.Error, "ScreenMirror", $"Start failed: {ex.Message}");
                _session = null;
            }
        });
    }

    /// <summary>
    /// <para>启动投屏：推送服务端、启动进程、连接 socket 并进入解码循环。</para>
    /// Starts mirroring: pushes the server, launches it, connects the sockets
    /// and enters the decode loop.
    /// </summary>
    /// <param name="session"><para>已连接的 ADB 会话。</para>The established ADB session.</param>
    /// <param name="options"><para>服务端启动选项。</para>Server launch options.</param>
    public void Start(AdbSession session, ScrcpyServerOptions options)
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("Mirroring is already running. 投屏已在运行中。");
        }

        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(options);

        var jarPath = ResolveServerJarPath();
        StatusText = GetLocalizedString("ScreenMirror_Installing");

        ScrcpyServerManager.PushServer(session, jarPath);
        _shellStream = ScrcpyServerManager.StartServer(session, options);

        StatusText = GetLocalizedString("ScreenMirror_Connecting");
        _client = new ScrcpyClient(session, options);
        ConnectWithRetry(_client);

        DeviceName = _client.DeviceName;
        StatusText = GetLocalizedString("ScreenMirror_Mirroring");

        _cts = new CancellationTokenSource();
        var decodeClient = _client;
        var decodeToken = _cts.Token;
        _decodeThread = new Thread(() => DecodeLoop(decodeClient, decodeToken))
        {
            IsBackground = true,
            Name = "UotanStudio.ScreenMirror",
        };
        _decodeThread.Start();
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
        _cts?.Cancel();
        _client?.Dispose(); // Unblock socket reads before waiting; decoder disposal stays on its owner thread.
        lock (_frameLock)
        {
            if (_pendingFrame is { } pending) ArrayPool<byte>.Shared.Return(pending.Pixels);
            _pendingFrame = null;
        }
        try
        {
            _decodeThread?.Join(2000);
        }
        catch
        {
            // Join may fail if the thread already exited. 线程已退出时 Join 可能失败。
        }

        _client?.Dispose();
        _shellStream?.Dispose();

        _client = null;
        _shellStream = null;
        _cts?.Dispose();
        _cts = null;
        _session = null;

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
    public void InjectTouch(byte action, long pointerId, int x, int y, int width, int height, uint buttons)
        => SendControl(ScrcpyControlMessageWriter.InjectTouchEvent(action, pointerId, x, y, width, height, buttons: buttons));

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

    private void ConnectWithRetry(ScrcpyClient client)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                client.Connect(ReadTimeoutMs);
                return;
            }
            catch (Exception) when (attempt < ConnectRetryCount - 1)
            {
                // The server process needs a moment to open its sockets.
                // 服务端进程需要短暂时间打开 socket。
                Thread.Sleep(ConnectRetryDelayMs);
            }
        }
    }

    private void DecodeLoop(ScrcpyClient client, CancellationToken token)
    {
        try
        {
            var metadata = client.VideoMetadata ?? throw new InvalidDataException("Missing scrcpy video metadata.");
            var codec = metadata.CodecName.ToUpperInvariant() switch
            {
                "H264" => VideoCodecId.H264,
                "H265" or "HEVC" => VideoCodecId.H265,
                _ => throw new NotSupportedException($"Unsupported video codec '{metadata.CodecName}'."),
            };
            using var decoder = VideoDecoderFactory.Create();
            decoder.Initialize(codec, metadata.Width, metadata.Height, OnDecodedFrame);
            byte[]? configuration = null;
            while (!token.IsCancellationRequested)
            {
                ScrcpyVideoFrame? packet = client.ReadFrame(1000);
                if (packet is null) continue;
                if (packet.IsCodecConfig)
                {
                    // Some MFTs require parameter sets and the first picture in a single sample.
                    // Keep the complete scrcpy config packet and prepend it to the next picture.
                    if (packet.Data.Length > 1024 * 1024) throw new InvalidDataException("Excessive scrcpy codec configuration.");
                    configuration = packet.Data;
                    continue;
                }
                long timestampTicks = checked(packet.PresentationTimestampMicroseconds * 10);
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
                if (!_disposed) StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + exception.Message;
            });
        }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private void OnDecodedFrame(DecodedVideoFrame frame)
    {
        if (_disposed) return;
        int bgraSize = Nv12ToBgraConverter.GetBgraBufferSize(frame.Width, frame.Height);
        byte[]? bgra = ArrayPool<byte>.Shared.Rent(bgraSize);
        try
        {
            Nv12ToBgraConverter.Convert(in frame, bgra.AsSpan(0, bgraSize));
            lock (_frameLock)
            {
                if (_disposed) return;
                if (_pendingFrame is { } previous) ArrayPool<byte>.Shared.Return(previous.Pixels);
                _pendingFrame = new(bgra, bgraSize, frame.Width, frame.Height);
                bgra = null; // Ownership passes to the bounded, latest-frame slot.
                if (!_renderScheduled)
                {
                    _renderScheduled = true;
                    Dispatcher.UIThread.Post(RenderPendingFrame);
                }
            }
        }
        finally { if (bgra is not null) ArrayPool<byte>.Shared.Return(bgra); }
    }

    private void RenderPendingFrame()
    {
        PendingFrame? pending;
        lock (_frameLock)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }
        if (pending is null) return;
        try
        {
            if (!_disposed) RenderBgra(pending.Pixels, pending.Length, pending.Width, pending.Height);
        }
        finally { ArrayPool<byte>.Shared.Return(pending.Pixels); }
    }

    private void RenderBgra(byte[] bgra, int bgraSize, int width, int height)
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
        // 逐行复制到锁定帧缓冲，保证 stride 与托管源行距不一致时的安全。
        // Copy row by row into the locked framebuffer so a stride mismatch
        // between the managed source and the bitmap stays safe.
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(bgra, row * width * 4, IntPtr.Add(dst, row * stride), rowBytes);
        }
    }

    private static string GetLocalizedString(string key)
        => UotanStudio.Services.LanguageService.Instance.GetString(key);

    /// <summary>Builds server options from the persisted app settings.</summary>
    /// <summary>按持久化应用设置构建服务端选项。</summary>
    private static ScrcpyServerOptions CreateOptionsFromSettings()
    {
        var settings = UotanStudio.Services.SettingsPersistenceService.Instance.LoadSettings();
        return new ScrcpyServerOptions
        {
            VideoBitRate = settings.ScreenMirrorBitRate,
            MaxFps = settings.ScreenMirrorMaxFps,
            MaxSize = settings.ScreenMirrorMaxSize,
        };
    }
}
