using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mirroring;


namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    // 活动 logcat 捕获登记：设备级操作（如重启）需要端点租约时，先停止捕获并等待其释放。
    // Active logcat captures: device-level operations that need the endpoint lease stop them first.
    private sealed class LogStreamRegistration(CancellationTokenSource linked)
    {
        public CancellationTokenSource Linked { get; } = linked;
        public Task? Task { get; set; }
    }

    private readonly Lock _logStreamLock = new();
    private readonly Dictionary<string, LogStreamRegistration> _activeLogStreams = new(StringComparer.Ordinal);

    public Task StartLogcatStreamingAsync(DetectedDevice device, Action<AdbLogcatEntry> onEntry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var registration = new LogStreamRegistration(linked);
        lock (_logStreamLock)
        {
            _activeLogStreams[device.Key] = registration;
        }
        var run = RunLogcatStreamingCoreAsync(device, onEntry, registration, cancellationToken);
        registration.Task = run;
        return run;
    }

    /// <summary>
    /// <para>停止该设备的活动 logcat 捕获并等待其端点租约释放。外部停止对捕获发起方
    /// （日志页）表现为正常结束，不产生取消错误；调用方自身的取消仍按取消传播。</para>
    /// Stops the active logcat capture for the device and awaits its endpoint-lease release.
    /// An external stop completes the capture normally for its owner (the log page) instead of
    /// surfacing a cancellation error; cancellation by the owner's own token still propagates.
    /// </summary>
    internal async Task StopLogcatStreamingAsync(DetectedDevice device)
    {
        LogStreamRegistration? registration;
        lock (_logStreamLock)
        {
            _activeLogStreams.TryGetValue(device.Key, out registration);
        }
        if (registration is null)
        {
            return;
        }

        registration.Linked.Cancel();
        if (registration.Task is { } run)
        {
            try { await run.ConfigureAwait(false); }
            catch { /* The owner already renders stream errors; stopping only guarantees release. */ }
        }
    }

    private async Task RunLogcatStreamingCoreAsync(DetectedDevice device, Action<AdbLogcatEntry> onEntry,
        LogStreamRegistration registration, CancellationToken callerToken)
    {
        var cancellationToken = registration.Linked.Token;
        try
        {
            if (device.Protocol == DeviceProtocolKind.Hdc)
            {
                var decoder = new AdbLogcatLineDecoder(line => onEntry(AdbLogcatEntry.Parse(line)));
                try
                {
                    if (UseHdcServerFor(device))
                    {
                        using var hdc = new HdcServerClient();
                        await hdc.ExecuteStreamingAsync(device.Serial, "shell hilog -v year",
                            bytes => decoder.Feed(bytes), cancellationToken).ConfigureAwait(false);
                    }
                    else
                        await Task.Run(() =>
                        {
                            var opened = HdcDirectConnection.Open(device);
                            using var lease = opened.Lease;
                            opened.Session.CreateShellClient("hilog -v year").ExecuteStreaming(bytes => decoder.Feed(bytes), null, cancellationToken);
                        }, cancellationToken).ConfigureAwait(false);
                }
                finally { if (!cancellationToken.IsCancellationRequested) decoder.Feed([], flush: true); }
                return;
            }
            if (device.Protocol != DeviceProtocolKind.Adb)
                throw new ArgumentException("Logcat requires an ADB device.", nameof(device));
            cancellationToken.ThrowIfCancellationRequested();
            if (AdbServerManages(device))
            {
                var services = new AdbServerServices(device.Serial,
                    DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort);
                var reader = new AdbLogcatReader((command, onLine, token) =>
                    services.StreamShellAsync(command, line =>
                    {
                        if (token.IsCancellationRequested) return false;
                        onLine(line);
                        return true;
                    }, token));
                await reader.RunAsync(onEntry, cancellationToken).ConfigureAwait(false);
                return;
            }

            var opened = OpenAdbLease(device)
                ?? throw new InvalidOperationException("Unable to open an ADB session for the device.");
            using var adbLease = opened.Lease;
            var session = opened.Session;
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var directReader = new AdbLogcatReader(async (command, onLine, token) =>
            {
                var stdout = new AdbLogcatLineDecoder(onLine);
                var stderr = new AdbLogcatLineDecoder(onLine);
                try
                {
                    var shell = session.CreateShellClient(command);
                    var exitCode = await Task.Run(() => shell.ExecuteStreaming(
                        buffer => stdout.Feed(buffer), buffer => stderr.Feed(buffer), token), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (exitCode != 0) throw new InvalidOperationException($"ADB shell exited with code {exitCode}.");
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                    {
                        stdout.Feed([], flush: true);
                        stderr.Feed([], flush: true);
                    }
                }
            });
            await directReader.RunAsync(onEntry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // 设备级操作请求的停止：捕获方视为正常结束，租约已在 using 中释放。
            // A device-level operation requested the stop: the owner sees a normal completion
            // and the lease is already released by the using scope.
        }
        finally
        {
            lock (_logStreamLock)
            {
                if (_activeLogStreams.TryGetValue(device.Key, out var current) &&
                    ReferenceEquals(current, registration))
                {
                    _activeLogStreams.Remove(device.Key);
                }
            }
            registration.Linked.Dispose();
        }
    }

    /// <summary>
    /// Clears all device logcat buffers, using adb server or direct USB.
    /// <para>通过 adb server 或 USB 直连清空设备端全部 logcat 缓冲区；失败时抛出异常。</para>
    /// </summary>
    public async Task ClearLogcatBuffersAsync(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            await HdcDeviceService.FromSettings().ShellAsync(device, "hilog -r", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("Logcat requires an ADB device.", nameof(device));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        const string successMarker = "__UOTAN_LOGCAT_CLEARED__";
        // The legacy adb-server shell API does not expose the remote exit code.
        const string command = "logcat -b all -c && echo " + successMarker;
        var confirmed = false;
        var diagnostic = new System.Text.StringBuilder();
        void OnLine(string line)
        {
            if (line.Trim() == successMarker) confirmed = true;
            else if (diagnostic.Length < 4096)
                diagnostic.AppendLine(line[..Math.Min(line.Length, 4096 - diagnostic.Length)]);
        }

        try
        {
            token.ThrowIfCancellationRequested();
            var exitCode = 0;
            if (AdbServerManages(device))
            {
                var services = new AdbServerServices(device.Serial,
                    DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort);
                await services.StreamShellAsync(command, line =>
                {
                    if (token.IsCancellationRequested) return false;
                    OnLine(line);
                    return true;
                }, token).ConfigureAwait(false);
            }
            else
            {
                var opened = OpenAdbLease(device)
                    ?? throw new InvalidOperationException("Unable to open an ADB session for the device.");
                using var adbLease = opened.Lease;
                var session = opened.Session;
                await session.ConnectAsync(token).ConfigureAwait(false);
                var stdout = new AdbLogcatLineDecoder(OnLine);
                var stderr = new AdbLogcatLineDecoder(OnLine);
                var shell = session.CreateShellClient(command);
                exitCode = await Task.Run(() => shell.ExecuteStreaming(
                    buffer => stdout.Feed(buffer), buffer => stderr.Feed(buffer), token), token).ConfigureAwait(false);
                stdout.Feed([], flush: true);
                stderr.Feed([], flush: true);
            }
            token.ThrowIfCancellationRequested();
            if (!confirmed || exitCode != 0)
                throw new InvalidOperationException(diagnostic.Length > 0 ? diagnostic.ToString().Trim()
                    : $"Logcat buffer clear was not confirmed (exit code {exitCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Clearing logcat buffers timed out.");
        }
    }

}