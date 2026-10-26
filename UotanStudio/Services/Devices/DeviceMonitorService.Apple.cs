using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Apple;
using UotanStudio.Comm.Protocols.Apple.Afc;
using UotanStudio.Comm.Protocols.Apple.Syslog;
using UotanStudio.Comm.Protocols.Apple.Tunnel;
using UotanStudio.Comm.Protocols.Apple.Usbmuxd;
using UotanStudio.Mirroring;
using UotanStudio.Mirroring.AirPlay;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private const string AppleLogStreamPrefix = "apple-log:";
    private const string AppleSshForwardKey = "apple-ssh:";

    private readonly Lock _appleForwardLock = new();
    private readonly Dictionary<string, AppleSshForwardRegistration> _appleSshForwards = new(StringComparer.Ordinal);

    private sealed class AppleSshForwardRegistration : IDisposable
    {
        public required DeviceLease Lease { get; init; }
        public required UsbmuxdPortForward Forward { get; init; }

        public void Dispose()
        {
            Forward.Dispose();
            Lease.Dispose();
        }
    }

    /// <summary>
    /// Runs one operation against the selected Apple device's session.
    /// <para>对所选 Apple 设备的会话执行一次操作。</para>
    /// </summary>
    /// <typeparam name="TResult">The operation result type. <para>操作结果类型。</para></typeparam>
    /// <param name="device">The detected Apple device. <para>检测到的 Apple 设备。</para></param>
    /// <param name="operation">The session operation. <para>会话操作。</para></param>
    /// <param name="cancellationToken">Cancels the open and the operation. <para>取消打开与操作。</para></param>
    /// <returns>The operation result. <para>操作结果。</para></returns>
    internal TResult RunApple<TResult>(DetectedDevice device, Func<IAppleDeviceSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (device.Protocol != DeviceProtocolKind.Ios)
        {
            throw new InvalidOperationException("Cannot open the selected iOS device.");
        }

        var (lease, session) = LeaseApple(device, cancellationToken);
        try
        {
            using (session)
            {
                session.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
                return operation(session);
            }
        }
        finally
        {
            CloseLease(lease);
        }
    }

    private (DeviceLease Lease, IAppleDeviceSession Session) LeaseApple(DetectedDevice device,
        CancellationToken cancellationToken)
    {
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Apple,
            cancellationToken: cancellationToken);
        if (!lease.TryGetSession<IAppleDeviceSession>(out var session))
        {
            CloseLease(lease);
            throw new IOException("The selected iOS endpoint has no supported session.");
        }

        return (lease, session);
    }

    /// <summary>
    /// Streams the device syslog as log entries until stopped; iOS 17+ routes through the trusted
    /// tunnel, earlier versions open the relay service directly.
    /// <para>以日志条目形式流转设备 syslog 直到停止；iOS 17+ 走受信隧道，更早版本直开中继服务。</para>
    /// </summary>
    /// <param name="device">The detected iOS device. <para>检测到的 iOS 设备。</para></param>
    /// <param name="onEntry">The entry callback; may run on the pumping thread. <para>条目回调；可能在中继线程运行。</para></param>
    /// <param name="cancellationToken">Cancels the capture. <para>取消捕获。</para></param>
    public Task StartIosLogStreamingAsync(DetectedDevice device, Action<AdbLogcatEntry> onEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var key = AppleLogStreamPrefix + device.Key;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var registration = new LogStreamRegistration(linked);
        LogStreamRegistration? replaced;
        lock (_logStreamLock)
        {
            _activeLogStreams.TryGetValue(key, out replaced);
            _activeLogStreams[key] = registration;
        }

        if (replaced is not null)
        {
            try { replaced.Linked.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        var run = RunIosLogStreamingCoreAsync(device, onEntry, registration, cancellationToken);
        registration.Task = run;
        return run;
    }

    /// <summary>
    /// Stops the active iOS syslog capture.
    /// <para>停止活动的 iOS syslog 捕获。</para>
    /// </summary>
    /// <param name="device">The device whose capture is stopped. <para>要停止捕获的设备。</para></param>
    internal async Task StopIosLogStreamingAsync(DetectedDevice device)
    {
        var key = AppleLogStreamPrefix + device.Key;
        LogStreamRegistration? registration;
        lock (_logStreamLock)
        {
            _activeLogStreams.TryGetValue(key, out registration);
            _activeLogStreams.Remove(key);
        }

        if (registration is null)
        {
            return;
        }

        try { registration.Linked.Cancel(); }
        catch (ObjectDisposedException) { return; }
        if (registration.Task is { } run)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogService.Instance.Write(LogLevel.Debug, "AdbLog",
                    $"Stopping the iOS log capture for {device.Key} reported {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async Task RunIosLogStreamingCoreAsync(DetectedDevice device, Action<AdbLogcatEntry> onEntry,
        LogStreamRegistration registration, CancellationToken callerToken)
    {
        var cancellationToken = registration.Linked.Token;
        try
        {
            await Task.Run(() =>
            {
                var (lease, session) = LeaseApple(device, cancellationToken);
                try
                {
                    session.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
                    var info = session.ReadDeviceInfoAsync(cancellationToken).GetAwaiter().GetResult();
                    using var stream = OpenIosLogStream(session, info, cancellationToken);
                    new SyslogRelayClient().Pump(stream,
                        line => onEntry(ToLogEntry(line)), cancellationToken);
                }
                finally
                {
                    CloseLease(lease);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
        }
    }

    private static Stream OpenIosLogStream(IAppleDeviceSession session, AppleDeviceInfo info,
        CancellationToken cancellationToken)
    {
        if (GetMajorVersion(info.ProductVersion) >= 17)
        {
            try
            {
                using var tunnel = AppleTunnelSession.Start(session, cancellationToken);
                return tunnel.OpenService(AppleConstants.ServiceSyslogRelayShimRemote, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogService.Instance.Write(LogLevel.Info, "AdbLog",
                    $"The iOS trusted tunnel failed ({exception.Message}); falling back to the direct relay service.");
            }
        }

        return session.OpenService(AppleConstants.ServiceSyslogRelay, cancellationToken);
    }

    private static int GetMajorVersion(string? productVersion) =>
        int.TryParse(productVersion?.Split('.')[0], out var major) ? major : 0;

    private static AdbLogcatEntry ToLogEntry(SyslogLine line)
    {
        var timestamp = line.Timestamp?.ToString("MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        var priority = (line.Level ?? string.Empty).ToUpperInvariant() switch
        {
            "ERROR" => 'E',
            "FAULT" => 'F',
            "WARNING" => 'W',
            "DEBUG" => 'D',
            _ => 'I',
        };
        return new AdbLogcatEntry(
            line.RawText,
            timestamp,
            line.ProcessId ?? 0,
            0,
            line.Process ?? "ios",
            line.Process ?? "ios",
            priority,
            line.Message);
    }

    /// <summary>
    /// Opens an SSH channel to the device over the multiplexer (jailbroken devices only); the channel
    /// stays open until <see cref="StopSshTunnel"/> or process shutdown.
    /// <para>经复用器打开到设备 22 端口的 SSH 通道（仅限越狱设备）；通道保持到
    /// <see cref="StopSshTunnel"/> 或进程退出。</para>
    /// </summary>
    /// <param name="device">The detected iOS device. <para>检测到的 iOS 设备。</para></param>
    /// <param name="cancellationToken">Cancels the start. <para>取消启动。</para></param>
    /// <returns>The bound local port. <para>实际绑定的本地端口。</para></returns>
    private readonly Lock _appleAirPlayLock = new();
    private MirrorSessionFacade? _airPlaySession;

    /// <summary>
    /// Starts the AirPlay receiving endpoint, replacing any endpoint started earlier in this process.
    /// <para>启动 AirPlay 接收端点，并替换本进程中先前启动的端点。</para>
    /// </summary>
    /// <param name="logSink">Optional diagnostic sink; may be called from a background thread.
    /// <para>可选的诊断接收端；可能由后台线程调用。</para></param>
    /// <returns>The connected receiving session owning the RTSP listener.
    /// <para>持有 RTSP 监听器的已连接接收会话。</para></returns>
    /// <exception cref="System.Net.Sockets.SocketException">The RTSP or mDNS port cannot be bound.
    /// <para>RTSP 或 mDNS 端口无法绑定。</para></exception>
    internal MirrorSessionFacade StartAirPlayReceiver(Action<string>? logSink = null)
    {
        var session = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions(), logSink));
        try
        {
            session.Connect();
        }
        catch
        {
            session.Dispose();
            throw;
        }

        lock (_appleAirPlayLock)
        {
            _airPlaySession?.Dispose();
            _airPlaySession = session;
        }

        return session;
    }

    /// <summary>
    /// Stops the AirPlay receiving endpoint started by this process.
    /// <para>停止本进程启动的 AirPlay 接收端点。</para>
    /// </summary>
    internal void StopAirPlayReceiver()
    {
        MirrorSessionFacade? session;
        lock (_appleAirPlayLock)
        {
            session = _airPlaySession;
            _airPlaySession = null;
        }

        session?.Dispose();
    }

    internal int StartSshTunnel(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        var key = AppleSshForwardKey + device.Key;
        lock (_appleForwardLock)
        {
            if (_appleSshForwards.ContainsKey(key))
            {
                throw new InvalidOperationException("The SSH channel for this device is already open.");
            }
        }

        var (lease, session) = LeaseApple(device, cancellationToken);
        UsbmuxdPortForward forward;
        try
        {
            session.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
            forward = UsbmuxdPortForward.Start(session, 0, 22, cancellationToken);
        }
        catch
        {
            CloseLease(lease);
            throw;
        }

        var registration = new AppleSshForwardRegistration { Lease = lease, Forward = forward };
        bool added;
        lock (_appleForwardLock)
        {
            added = _appleSshForwards.TryAdd(key, registration);
        }

        if (!added)
        {
            registration.Dispose();
            throw new InvalidOperationException("The SSH channel for this device is already open.");
        }

        return forward.LocalPort;
    }

    /// <summary>
    /// Closes the SSH channel for a device, if open.
    /// <para>关闭某设备的 SSH 通道（如已打开）。</para>
    /// </summary>
    /// <param name="device">The device whose channel closes. <para>要关闭通道的设备。</para></param>
    internal void StopSshTunnel(DetectedDevice device)
    {
        var key = AppleSshForwardKey + device.Key;
        AppleSshForwardRegistration? registration;
        lock (_appleForwardLock)
        {
            if (!_appleSshForwards.Remove(key, out registration))
            {
                return;
            }
        }

        registration.Dispose();
    }

    internal bool IsSshTunnelOpen(DetectedDevice device)
    {
        lock (_appleForwardLock)
        {
            return _appleSshForwards.ContainsKey(AppleSshForwardKey + device.Key);
        }
    }

    /// <summary>
    /// Exports every crash report currently stored on the device into
    /// <c>%TEMP%\UotanStudio\crashlogs\&lt;serial&gt;</c> and returns the directory.
    /// <para>把设备上现存的崩溃报告导出到
    /// <c>%TEMP%\UotanStudio\crashlogs\&lt;序列号&gt;</c> 并返回目录。</para>
    /// </summary>
    /// <param name="device">The detected iOS device. <para>检测到的 iOS 设备。</para></param>
    /// <param name="cancellationToken">Cancels the export. <para>取消导出。</para></param>
    /// <returns>The export directory path. <para>导出目录路径。</para></returns>
    internal string ExportCrashLogs(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(Path.GetTempPath(), "UotanStudio", "crashlogs", device.Serial);
        Directory.CreateDirectory(target);
        return RunApple(device, session =>
        {
            using var afc = new AfcClient(session.OpenService(AppleConstants.ServiceCrashReportCopyMobile,
                cancellationToken));
            var entries = afc.ReadDirectory("/", cancellationToken);
            var exported = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.EndsWith(".ips", StringComparison.OrdinalIgnoreCase) &&
                    !entry.EndsWith(".crash", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var bytes = afc.ReadFile("/" + entry, AfcClient.DefaultMaxFileBytes, cancellationToken);
                    File.WriteAllBytes(Path.Combine(target, entry), bytes);
                    exported++;
                }
                catch (AfcException exception) when (exception.StatusCode == 8)
                {
                }
            }

            LogService.Instance.Write(LogLevel.Info, "Devices",
                $"Exported {exported} crash reports for {device.Serial} to {target}.");
            return target;
        }, cancellationToken);
    }
}