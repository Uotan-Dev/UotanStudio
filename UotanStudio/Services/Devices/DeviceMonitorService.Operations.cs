using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mirroring;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>Runs a command on the selected HDC target.</summary>
    public Task<string> HdcShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken = default)
        => HdcOperations.ShellAsync(device, command, cancellationToken);

    /// <summary>Reads a parameter from the selected HDC target.</summary>
    public Task<string> HdcPropertyAsync(DetectedDevice device, string name, CancellationToken cancellationToken = default)
        => HdcOperations.GetPropertyAsync(device, name, cancellationToken);

    /// <summary>Sends a file to the selected HDC target.</summary>
    public Task<string> HdcSendFileAsync(DetectedDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default)
        => HdcOperations.SendFileAsync(device, localPath, remotePath, cancellationToken);

    /// <summary>Receives a file from the selected HDC target.</summary>
    public Task<string> HdcReceiveFileAsync(DetectedDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default)
        => HdcOperations.ReceiveFileAsync(device, remotePath, localPath, cancellationToken);

    /// <summary>Installs a package on the selected HDC target.</summary>
    public Task<string> HdcInstallAsync(DetectedDevice device, string localPath, CancellationToken cancellationToken = default)
        => HdcOperations.InstallAsync(device, localPath, cancellationToken);

    /// <summary>Uninstalls a package from the selected HDC target.</summary>
    public Task<string> HdcUninstallAsync(DetectedDevice device, string bundle, CancellationToken cancellationToken = default)
        => HdcOperations.UninstallAsync(device, bundle, cancellationToken);

    /// <summary>Disconnects the selected HDC network target.</summary>
    public Task<string> HdcDisconnectAsync(DetectedDevice device, CancellationToken cancellationToken = default)
        => HdcOperations.DisconnectAsync(device, cancellationToken);

    /// <summary>Enumerates serial endpoints without assigning an unverified device protocol.</summary>
    public IReadOnlyList<string> EnumerateSerialPorts()
        => [.. SerialTransport.GetAvailablePorts().OrderBy(port => port, StringComparer.Ordinal)];

    internal static void RequireSerialTarget(SerialConnectionOptions selected, IReadOnlyList<string> ports)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentException.ThrowIfNullOrWhiteSpace(selected.PortName);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!ports.Any(port => string.Equals(port, selected.PortName, comparison)))
            throw new IOException("The selected serial target is no longer present. Refresh the device list.");
    }

    /// <summary>Runs a preflighted Firehose bundle on the selected EDL serial target.</summary>
    public Task<IReadOnlyList<string>> FlashEdlSerialAsync(SerialConnectionOptions serial, string folder, string? programmerPath,
        FirehoseConfiguration? configuration = null, bool includeUfsProvision = false,
        CancellationToken cancellationToken = default, bool continueOnFailure = false,
        bool skipAbsentSources = false, IReadOnlyList<string>? labels = null,
        IProgress<FlashBundleProgress>? progress = null, bool verify = false, EdlBundleTuning? tuning = null)
    {
        RequireSerialTarget(serial, EnumerateSerialPorts());
        return FlashEdlBundleService.FlashSerialCoreAsync(
            token => EdlSerialBootstrap.ConnectAsync(serial, programmerPath, configuration, token),
            folder, includeUfsProvision, cancellationToken, continueOnFailure, skipAbsentSources, labels,
            progress, verify, tuning);
    }

    /// <summary>Runs a preflighted bundle against the selected USB EDL device.</summary>
    public Task<IReadOnlyList<string>> FlashEdlBundleAsync(DetectedDevice device, string folder, string programmerPath,
        CancellationToken cancellationToken = default, bool continueOnFailure = false,
        bool skipAbsentSources = false, IReadOnlyList<string>? labels = null,
        IProgress<FlashBundleProgress>? progress = null, bool verify = false, bool skipUfsProvision = false,
        EdlBundleTuning? tuning = null, EdlInitializationOptions? initialization = null)
        => FlashEdlBundleService.FlashAsync(device, folder, programmerPath, cancellationToken,
            continueOnFailure, skipAbsentSources, labels, progress, verify, skipUfsProvision, tuning, initialization);

    /// <summary>Routes deployment and connection through the selected ADB server or native USB target.</summary>
    public ManagedMirrorSession StartMirror(DetectedDevice device,
        string jarPath, ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        var (lease, session) = LeaseAdb(device, CancellationToken.None);
        if (session.UsesServer)
        {
            try
            {
                using (session)
                {
                    var mirror = StartAdbServerMirror(device, jarPath, options);
                    return new ManagedMirrorSession(mirror.Client, mirror.Shell, null);
                }
            }
            finally
            {
                CloseLease(lease);
            }
        }

        try
        {
            session.Connect();
            var mirror = StartAdbUsbMirror(session, jarPath, options);
            return new ManagedMirrorSession(mirror.Client, mirror.Shell, session, lease);
        }
        catch
        {
            session.Dispose();
            CloseLease(lease);
            throw;
        }
    }

    private (ScrcpyClient Client, Stream Shell) StartAdbServerMirror(DetectedDevice device, string jarPath,
        ScrcpyServerOptions options)
    {
        RequireAdbServerDevice(device);
        ArgumentNullException.ThrowIfNull(options);
        var server = new AdbServerServices(device.Serial, DeviceCommunicationConfiguration.ServerHost,
            DeviceCommunicationConfiguration.AdbServerPort);
        server.Push(jarPath, ScrcpyConstants.ServerDevicePath);
        var shell = server.Open("shell:" + options.BuildShellCommand());
        try { return (new ScrcpyClient(server, options), shell); }
        catch { shell.Dispose(); throw; }
    }

    /// <summary>Deploys and opens a native USB mirror stream on a manager-created session.</summary>
    /// <remarks>Deployment is awaited to completion here because <see cref="StartMirror"/> is a synchronous
    /// entry point: the blocking wait stays on this caller thread rather than leaking into the mirroring layer.
    /// <para>本方法在此把部署等待到完成，因为 <see cref="StartMirror"/> 是同步入口：阻塞等待留在本调用线程，
    /// 不会泄漏到投屏层。</para></remarks>
    private static (ScrcpyClient Client, Stream Shell) StartAdbUsbMirror(IAdbDeviceSession session, string jarPath,
        ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        if (session.UsesServer)
            throw new ArgumentException("A native USB ADB session is required.", nameof(session));
        ScrcpyServerManager.PushServerAsync(session, jarPath).ConfigureAwait(false).GetAwaiter().GetResult();
        var shell = ScrcpyServerManager.StartServer(session, options).AsServiceStream();
        try { return (new ScrcpyClient(session, options), shell); }
        catch { shell.Dispose(); throw; }
    }

    /// <summary>Opens an external shell after verifying the selected server target.</summary>
    public void OpenInteractiveShell(DetectedDevice device)
        => DeviceShellService.OpenInteractiveShell(this, device);

    /// <summary>Relays a selected ADB or HDC shell to the embedded terminal process.</summary>
    public async Task RunInteractiveShellAsync(DetectedDevice device, Stream input, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb)
        {
            await RunAdbAsync(device, async session =>
            {
                using var shell = session.OpenServiceStream("shell:");
                shell.ReadTimeout = 1000;
                await RelayShellAsync(shell, input, output, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            if (!UseHdcServerFor(device)) throw new IOException("HDC server is required for the interactive shell.");
            using var server = new HdcServerClient(DeviceCommunicationConfiguration.ServerHost,
                HdcServerClient.DefaultServerPort);
            var target = server.ListTargetDetails().SingleOrDefault(item =>
                string.Equals(item.ConnectKey, device.Serial, StringComparison.Ordinal));
            if (target is null || !string.Equals(target.State, "Connected", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The HDC server target is not ready.");
            await server.RunInteractiveShellAsync(device.Serial, input, output, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new ArgumentException("An ADB or HDC target is required.", nameof(device));
    }

    private static async Task RelayShellAsync(Stream shell, Stream input, Stream output, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var writer = Task.Run(async () =>
        {
            try { await input.CopyToAsync(shell, lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
        }, lifetime.Token);
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count;
                try { count = await shell.ReadAsync(buffer, token).ConfigureAwait(false); }
                catch (TimeoutException) when (!token.IsCancellationRequested) { continue; }
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            shell.Dispose();
            await writer.ConfigureAwait(false);
        }
    }

    /// <summary>Stops the selected host server and refreshes the managed device list.</summary>
    public Task StopAdbServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeServerControl.StopAdbAsync(this, cancellationToken);

    /// <summary>Stops the selected host server and refreshes the managed device list.</summary>
    public Task StopHdcServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeServerControl.StopHdcAsync(this, cancellationToken);

    /// <summary>Disconnects only the selected network ADB target.</summary>
    public async Task DisconnectAdbAsync(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Protocol != DeviceProtocolKind.Adb || device.Transport != DeviceTransportKind.Tcp)
            throw new ArgumentException("A network ADB target is required.", nameof(device));
        if (!device.Key.StartsWith("adb-tcp:", StringComparison.Ordinal))
        {
            await AdbServerServices.DisconnectAsync(device.Serial,
                DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        TryDetachManualNetworkTarget(device.Serial);
        if (DebugBridgeRoute.AdbServerAvailable)
        {
            await AdbServerServices.DisconnectAsync(device.Serial,
                DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort, cancellationToken)
                .ConfigureAwait(false);
        }
    }
    /// <summary>Reboots through the routed debug bridge session or the Fastboot transport.</summary>
    public async Task<bool> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken = default)
    {
        var adbTarget = mode switch
        {
            "bootloader" => "bootloader",
            "recovery" => "recovery",
            "edl" => "edl",
            "sideload" => "sideload",
            "fastboot" => "fastboot",
            "fastbootd" => "fastboot",
            _ => "now",
        };
        var fastbootTarget = mode switch
        {
            "bootloader" => "bootloader",
            "recovery" => "recovery",
            "edl" => "edl",
            "fastboot" => "fastboot",
            "fastbootd" => "fastboot",
            _ => null,
        };

        if (device.Protocol == DeviceProtocolKind.Fastboot)
        {
            RunFastboot(device, session => session.Reboot(fastbootTarget));
            return true;
        }

        if (device.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc)
        {
            await StopLogcatStreamingAsync(device).ConfigureAwait(false);
        }

        if (device.Protocol == DeviceProtocolKind.Adb)
        {
            RunAdb(device, session =>
            {
                session.CreateDeviceServices().Reboot(adbTarget);
                return true;
            }, cancellationToken);
            return true;
        }

        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            var opened = OpenHdcLease(device);
            if (opened is null)
            {
                return false;
            }

            using var hdcLease = opened.Value.Lease;
            var hdc = opened.Value.Session;
            try
            {
                hdc.Connect();
                hdc.CreateDeviceServices().Reboot(mode);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// <para>查询 ADB 设备状态（优先 adb server；不可达时通过直连握手确认状态）。</para>
    /// Queries the ADB device state (adb server preferred; direct handshake when the server connection is refused).
    /// </summary>
    public async Task<string> GetAdbStateAsync(string serial, CancellationToken cancellationToken = default)
    {
        if (DebugBridgeRoute.UseAdbServer())
        {
            var match = ListAdbServerTargets()
                .FirstOrDefault(target => string.Equals(target.Serial, serial, StringComparison.Ordinal));
            if (match is not null) return match.State.ToLowerInvariant();
        }
        var targets = Scan().Where(d => d.Protocol == DeviceProtocolKind.Adb &&
            string.Equals(d.Serial, serial, StringComparison.Ordinal)).Take(2).ToArray();
        if (targets.Length != 1 || targets[0].Key.StartsWith("adb:", StringComparison.Ordinal))
            return "unknown";
        var target = targets[0];
        try
        {
            return await RunAdbAsync(target,
                session => Task.FromResult(session.IsConnected ? "device" : "unknown"),
                cancellationToken).ConfigureAwait(false);
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// Streams logcat through adb server when available, otherwise through direct USB.
    /// <para>优先通过 adb server 读取 logcat，否则使用 USB 直连；流结束或取消时完成。</para>
    /// </summary>
}