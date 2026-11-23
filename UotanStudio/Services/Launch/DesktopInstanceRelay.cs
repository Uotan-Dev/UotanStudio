using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace UotanStudio.Services.Launch;

/// <summary>One desktop launch forwarded from a secondary process.<para>由次级进程转发的桌面启动请求。</para></summary>
/// <param name="Files">The files to open.<para>需要打开的文件。</para></param>
/// <param name="ScreenMirrorSerial">The device serial of a screen-mirror request, or null.<para>投屏请求的设备序列号；无则为 null。</para></param>
public sealed record DesktopLaunchRequest(IReadOnlyList<string> Files, string? ScreenMirrorSerial);

/// <summary>
/// Single-instance coordination for the desktop host: the first GUI process owns the per-user lock and
/// serves forwarded launches over a named pipe, so a later launch opens its files in the running window.
/// <para>桌面宿主的单实例协调：首个 GUI 进程持有按用户锁并通过命名管道接收转发的启动请求，使后续启动把文件
/// 打开到正在运行的窗口。</para>
/// </summary>
/// <remarks>
/// A launch is forwarded only while the lock is held, and a failed forward still starts a window instead of
/// dropping the user's file. Every failure is logged and never blocks startup.
/// <para>仅在锁被持有时转发；转发失败时仍启动窗口而不是丢弃用户的文件。所有失败只记日志，绝不阻断启动。</para>
/// </remarks>
public sealed class DesktopInstanceRelay : IDisposable
{
    private const string LockFileName = "desktop-instance.lock";
    private const string PipeFileName = "desktop-instance.pipe";
    private const string PipeNamePrefix = "UotanStudio.Desktop.";
    private const int ProtocolVersion = 1;
    private const int MaxFiles = 64;
    private const int MaxStringBytes = 128 * 1024;
    private const int MaxPendingRequests = 32;
    private const int ConnectTimeoutMilliseconds = 800;
    private const int ForwardTimeoutMilliseconds = 3000;
    private const int ForwardRetryDelayMilliseconds = 120;
    private const int ReadTimeoutSeconds = 10;
    private const int ListenerRetryDelayMilliseconds = 250;

    private readonly Lock _gate = new();
    private readonly FileStream? _lockFile;
    private readonly string? _pipeName;
    private readonly string? _pipeNamePath;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<DesktopLaunchRequest> _pending = new();
    private Action<DesktopLaunchRequest>? _handler;
    private bool _disposed;

    private DesktopInstanceRelay(FileStream? lockFile, string? pipeName, string? pipeNamePath)
    {
        _lockFile = lockFile;
        _pipeName = pipeName;
        _pipeNamePath = pipeNamePath;
    }

    /// <summary>
    /// Starts single-instance coordination for this process. Returns a relay when this process became the
    /// primary instance (and therefore owns the listener); returns <see langword="null"/> when another
    /// instance accepted the forwarded launch and this process should exit.
    /// <para>为当前进程启动单实例协调。本进程成为主实例（并持有监听器）时返回中继；另一实例已接收转发的
    /// 启动请求、本进程应当退出时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="files">The launch files to open in the primary instance.<para>要在主实例中打开的启动文件。</para></param>
    /// <param name="screenMirrorSerial">The screen-mirror serial carried by the launch, or null.<para>启动携带的投屏序列号；无则为 null。</para></param>
    /// <returns>The primary relay, or null when the launch was forwarded.<para>主实例中继；转发成功时为 null。</para></returns>
    public static DesktopInstanceRelay? Start(IReadOnlyList<string> files, string? screenMirrorSerial)
        => StartInDirectory(AppPaths.Root, files, screenMirrorSerial);

    /// <summary>Starts coordination in an explicit state directory; used by tests.<para>在显式状态目录中启动协调；供测试使用。</para></summary>
    /// <param name="directory">The directory holding the lock and pipe-name files.<para>承载锁与管道名文件的目录。</para></param>
    /// <param name="files">The launch files to open in the primary instance.<para>要在主实例中打开的启动文件。</para></param>
    /// <param name="screenMirrorSerial">The screen-mirror serial carried by the launch, or null.<para>启动携带的投屏序列号；无则为 null。</para></param>
    /// <returns>The primary relay, or null when the launch was forwarded.<para>主实例中继；转发成功时为 null。</para></returns>
    internal static DesktopInstanceRelay? StartInDirectory(string directory, IReadOnlyList<string> files, string? screenMirrorSerial)
        => StartCore(directory, new DesktopLaunchRequest([.. files], screenMirrorSerial));

    /// <summary>
    /// Registers the handler that receives forwarded launches. Requests that arrived before registration are
    /// delivered immediately so a launch during startup is never lost.
    /// <para>注册接收转发启动请求的处理器。注册前到达的请求会立即补发，因此启动期间的启动请求不会丢失。</para>
    /// </summary>
    /// <param name="handler">The handler invoked for every forwarded launch.<para>每个转发启动请求调用的处理器。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.<para>处理器为 null。</para></exception>
    public void SetLaunchHandler(Action<DesktopLaunchRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        DesktopLaunchRequest[] pending;
        lock (_gate)
        {
            _handler = handler;
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var request in pending)
        {
            handler(request);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        if (_pipeNamePath is not null)
        {
            TryDelete(_pipeNamePath);
        }

        _lockFile?.Dispose();
    }

    private static DesktopInstanceRelay? StartCore(string directory, DesktopLaunchRequest request)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogService.Instance.Write(LogLevel.Warn, "Launch",
                $"Single-instance state directory '{directory}' is unavailable: {ex.Message}");
            return new DesktopInstanceRelay(null, null, null);
        }

        var lockPath = Path.Combine(directory, LockFileName);
        if (TryAcquire(lockPath, out var lockFile))
        {
            return CreatePrimary(directory, lockFile!);
        }

        if (TryForward(directory, request))
        {
            LogService.Instance.Write(LogLevel.Info, "Launch",
                $"Forwarded the launch of {request.Files.Count} file(s) to the running instance.");
            return null;
        }

        if (TryAcquire(lockPath, TimeSpan.FromSeconds(2), out lockFile))
        {
            LogService.Instance.Write(LogLevel.Info, "Launch",
                "The previous instance released the desktop lock; starting as the primary instance.");
            return CreatePrimary(directory, lockFile!);
        }

        LogService.Instance.Write(LogLevel.Warn, "Launch",
            "A running instance holds the desktop lock but did not accept the launch; starting a second window.");
        return new DesktopInstanceRelay(null, null, null);
    }

    private static DesktopInstanceRelay CreatePrimary(string directory, FileStream lockFile)
    {
        var pipeName = PipeNamePrefix + Guid.NewGuid().ToString("N");
        var pipeNamePath = Path.Combine(directory, PipeFileName);
        TryDelete(pipeNamePath);
        try
        {
            File.WriteAllText(pipeNamePath, pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Instance.Write(LogLevel.Warn, "Launch",
                $"The single-instance pipe name could not be published: {ex.Message}");
        }

        var relay = new DesktopInstanceRelay(lockFile, pipeName, pipeNamePath);
        _ = Task.Run(() => relay.ServeAsync(pipeName, relay._shutdown.Token));
        return relay;
    }

    private async Task ServeAsync(string pipeName, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer(pipeName);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(ReadTimeoutSeconds));
                var request = await ReadRequestAsync(server, timeout.Token).ConfigureAwait(false);
                if (request is not null)
                {
                    Dispatch(request);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                LogService.Instance.Write(LogLevel.Debug, "Launch",
                    "Dropped a stalled single-instance connection.");
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException ex)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                LogService.Instance.Write(LogLevel.Debug, "Launch",
                    $"Single-instance connection failed: {ex.Message}");
                try
                {
                    await Task.Delay(ListenerRetryDelayMilliseconds, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                LogService.Instance.Write(LogLevel.Warn, "Launch",
                    $"Single-instance listener failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Dispatch(DesktopLaunchRequest request)
    {
        Action<DesktopLaunchRequest>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
            {
                if (_pending.Count < MaxPendingRequests)
                {
                    _pending.Enqueue(request);
                }

                return;
            }
        }

        handler(request);
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        try
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (PlatformNotSupportedException)
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }
    }

    private static bool TryForward(string directory, DesktopLaunchRequest request)
    {
        var pipeNamePath = Path.Combine(directory, PipeFileName);
        var deadline = Environment.TickCount64 + ForwardTimeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (ReadPipeName(pipeNamePath) is { } pipeName && TrySend(pipeName, request))
            {
                return true;
            }

            Thread.Sleep(ForwardRetryDelayMilliseconds);
        }

        return false;
    }

    private static string? ReadPipeName(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var name = File.ReadAllText(path).Trim();
            return name.Length == 0 ? null : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TrySend(string pipeName, DesktopLaunchRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMilliseconds);
            WriteRequest(client, request);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryAcquire(string lockPath, out FileStream? lockFile)
    {
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lockFile = null;
            return false;
        }
    }

    private static bool TryAcquire(string lockPath, TimeSpan timeout, out FileStream? lockFile)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (TryAcquire(lockPath, out lockFile))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }
    }

    private static async Task<DesktopLaunchRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        if (await ReadInt32Async(stream, token).ConfigureAwait(false) != ProtocolVersion)
        {
            return null;
        }

        var count = await ReadInt32Async(stream, token).ConfigureAwait(false);
        if (count is < 0 or > MaxFiles)
        {
            return null;
        }

        var files = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            if (await ReadStringAsync(stream, token).ConfigureAwait(false) is not { } file)
            {
                return null;
            }

            files.Add(file);
        }

        var hasSerial = await ReadInt32Async(stream, token).ConfigureAwait(false);
        if (hasSerial is not (0 or 1))
        {
            return null;
        }

        string? serial = null;
        if (hasSerial == 1)
        {
            serial = await ReadStringAsync(stream, token).ConfigureAwait(false);
            if (serial is null)
            {
                return null;
            }
        }

        return new DesktopLaunchRequest(files, serial);
    }

    private static void WriteRequest(Stream stream, DesktopLaunchRequest request)
    {
        WriteInt32(stream, ProtocolVersion);
        WriteInt32(stream, request.Files.Count);
        foreach (var file in request.Files)
        {
            WriteString(stream, file);
        }

        WriteInt32(stream, request.ScreenMirrorSerial is null ? 0 : 1);
        if (request.ScreenMirrorSerial is { } serial)
        {
            WriteString(stream, serial);
        }

        stream.Flush();
    }

    private static void WriteInt32(Stream stream, int value)
    {
        var buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer, 0, buffer.Length);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken token)
    {
        var buffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<string?> ReadStringAsync(Stream stream, CancellationToken token)
    {
        var length = await ReadInt32Async(stream, token).ConfigureAwait(false);
        if (length is < 0 or > MaxStringBytes)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}