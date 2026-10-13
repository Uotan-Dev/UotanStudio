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
    /// <summary>Runs a command on the selected HDC target.<para>在所选 HDC 目标上执行一条命令。</para></summary>
    /// <param name="device">The HDC target to run the command on.<para>执行命令的 HDC 目标。</para></param>
    /// <param name="command">The shell command text to send.<para>要发送的 shell 命令文本。</para></param>
    /// <param name="cancellationToken">Cancels the command.<para>取消该命令。</para></param>
    /// <returns>The output reported by the target.<para>目标返回的输出文本。</para></returns>
    public Task<string> HdcShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken = default)
        => HdcOperations.ShellAsync(device, command, cancellationToken);

    /// <summary>Reads a parameter from the selected HDC target.<para>读取所选 HDC 目标的一项参数。</para></summary>
    /// <param name="device">The HDC target to query.<para>要查询的 HDC 目标。</para></param>
    /// <param name="name">The parameter name to read.<para>要读取的参数名。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消读取。</para></param>
    /// <returns>The parameter value reported by the target.<para>目标返回的参数值。</para></returns>
    public Task<string> HdcPropertyAsync(DetectedDevice device, string name, CancellationToken cancellationToken = default)
        => HdcOperations.GetPropertyAsync(device, name, cancellationToken);

    /// <summary>Sends a file to the selected HDC target.<para>向所选 HDC 目标发送一个文件。</para></summary>
    /// <param name="device">The HDC target that receives the file.<para>接收文件的 HDC 目标。</para></param>
    /// <param name="localPath">Path of the file to send.<para>要发送的本地文件路径。</para></param>
    /// <param name="remotePath">Path the file is written to on the target.<para>目标上写入的文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    /// <returns>The output reported by the target.<para>目标返回的输出文本。</para></returns>
    public Task<string> HdcSendFileAsync(DetectedDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default)
        => HdcOperations.SendFileAsync(device, localPath, remotePath, cancellationToken);

    /// <summary>Receives a file from the selected HDC target.<para>从所选 HDC 目标接收一个文件。</para></summary>
    /// <param name="device">The HDC target that supplies the file.<para>提供文件的 HDC 目标。</para></param>
    /// <param name="remotePath">Path of the file on the target.<para>目标上的文件路径。</para></param>
    /// <param name="localPath">Path the file is written to locally.<para>本地写入的文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    /// <returns>The output reported by the target.<para>目标返回的输出文本。</para></returns>
    public Task<string> HdcReceiveFileAsync(DetectedDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default)
        => HdcOperations.ReceiveFileAsync(device, remotePath, localPath, cancellationToken);

    /// <summary>Installs a package on the selected HDC target.<para>在所选 HDC 目标上安装一个包。</para></summary>
    /// <param name="device">The HDC target that installs the package.<para>执行安装的 HDC 目标。</para></param>
    /// <param name="localPath">Path of the package to install.<para>要安装的包文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the install.<para>取消安装。</para></param>
    /// <returns>The output reported by the target.<para>目标返回的输出文本。</para></returns>
    public Task<string> HdcInstallAsync(DetectedDevice device, string localPath, CancellationToken cancellationToken = default)
        => HdcOperations.InstallAsync(device, localPath, cancellationToken);

    /// <summary>Uninstalls a package from the selected HDC target.<para>从所选 HDC 目标卸载一个包。</para></summary>
    /// <param name="device">The HDC target that uninstalls the package.<para>执行卸载的 HDC 目标。</para></param>
    /// <param name="bundle">The bundle name of the package to remove.<para>要移除的包名。</para></param>
    /// <param name="cancellationToken">Cancels the uninstall.<para>取消卸载。</para></param>
    /// <returns>The output reported by the target.<para>目标返回的输出文本。</para></returns>
    public Task<string> HdcUninstallAsync(DetectedDevice device, string bundle, CancellationToken cancellationToken = default)
        => HdcOperations.UninstallAsync(device, bundle, cancellationToken);

    /// <summary>Disconnects the selected HDC network target.<para>断开所选的 HDC 网络目标。</para></summary>
    /// <param name="device">The HDC network target to disconnect.<para>要断开的 HDC 网络目标。</para></param>
    /// <param name="cancellationToken">Cancels the disconnect.<para>取消断开操作。</para></param>
    /// <returns>The output reported by the target.<para>目标返回的输出文本。</para></returns>
    public Task<string> HdcDisconnectAsync(DetectedDevice device, CancellationToken cancellationToken = default)
        => HdcOperations.DisconnectAsync(device, cancellationToken);

    /// <summary>Enumerates serial endpoints without assigning an unverified device protocol.<para>枚举串口端点，不为其指派未经核实的设备协议。</para></summary>
    /// <returns>The available port names in ordinal order.<para>按序号排序的可用端口名。</para></returns>
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

    /// <summary>Runs a preflighted Firehose bundle on the selected EDL serial target.<para>在所选 EDL 串口目标上执行已预检的 Firehose 包。</para></summary>
    /// <param name="serial">The serial target the bundle runs on.<para>执行该包的串口目标。</para></param>
    /// <param name="folder">The directory holding the bundle images and manifests.<para>存放包镜像与清单的目录。</para></param>
    /// <param name="programmerPath">Optional explicit Firehose programmer image path.<para>可选的显式 Firehose programmer 镜像路径。</para></param>
    /// <param name="configuration">Optional Firehose session configuration.<para>可选的 Firehose 会话配置。</para></param>
    /// <param name="includeUfsProvision">When true, UFS provisioning is part of the run.<para>为 true 时，执行中包含 UFS 预置。</para></param>
    /// <param name="cancellationToken">Cancels the flash run.<para>取消刷写执行。</para></param>
    /// <param name="continueOnFailure">When true, a failed entry does not stop the run.<para>为 true 时，条目失败不会中止执行。</para></param>
    /// <param name="skipAbsentSources">When true, missing source images are tolerated.<para>为 true 时，容忍缺失的源镜像。</para></param>
    /// <param name="labels">Optional bundle entry labels limiting what is flashed.<para>可选的包条目标签，限定刷写范围。</para></param>
    /// <param name="progress">Optional reporter for bundle progress.<para>可选的包进度回调。</para></param>
    /// <param name="verify">When true, programmed writes are read back for checking.<para>为 true 时回读已写入数据以校验。</para></param>
    /// <param name="tuning">Optional sector-size, chunk and NAND overrides.<para>可选的扇区大小、写入块与 NAND 覆盖项。</para></param>
    /// <returns>The non-fatal deviations recorded during the run.<para>执行期间记录的非致命偏差。</para></returns>
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

    /// <summary>Runs a preflighted bundle against the selected USB EDL device.<para>对所选 USB EDL 设备执行已预检的包。</para></summary>
    /// <param name="device">The USB EDL device the bundle runs against.<para>执行该包的 USB EDL 设备。</para></param>
    /// <param name="folder">The directory holding the bundle images and manifests.<para>存放包镜像与清单的目录。</para></param>
    /// <param name="programmerPath">The Firehose programmer image to load.<para>要加载的 Firehose programmer 镜像。</para></param>
    /// <param name="cancellationToken">Cancels the flash run.<para>取消刷写执行。</para></param>
    /// <param name="continueOnFailure">When true, a failed entry does not stop the run.<para>为 true 时，条目失败不会中止执行。</para></param>
    /// <param name="skipAbsentSources">When true, missing source images are tolerated.<para>为 true 时，容忍缺失的源镜像。</para></param>
    /// <param name="labels">Optional bundle entry labels limiting what is flashed.<para>可选的包条目标签，限定刷写范围。</para></param>
    /// <param name="progress">Optional reporter for bundle progress.<para>可选的包进度回调。</para></param>
    /// <param name="verify">When true, programmed writes are read back for checking.<para>为 true 时回读已写入数据以校验。</para></param>
    /// <param name="skipUfsProvision">When true, UFS provisioning is left out of the run.<para>为 true 时，执行中不含 UFS 预置。</para></param>
    /// <param name="tuning">Optional sector-size, chunk and NAND overrides.<para>可选的扇区大小、写入块与 NAND 覆盖项。</para></param>
    /// <param name="initialization">Optional EDL initialization options for the session.<para>可选的会话 EDL 初始化选项。</para></param>
    /// <returns>The non-fatal deviations recorded during the run.<para>执行期间记录的非致命偏差。</para></returns>
    public Task<IReadOnlyList<string>> FlashEdlBundleAsync(DetectedDevice device, string folder, string programmerPath,
        CancellationToken cancellationToken = default, bool continueOnFailure = false,
        bool skipAbsentSources = false, IReadOnlyList<string>? labels = null,
        IProgress<FlashBundleProgress>? progress = null, bool verify = false, bool skipUfsProvision = false,
        EdlBundleTuning? tuning = null, EdlInitializationOptions? initialization = null)
        => FlashEdlBundleService.FlashAsync(device, folder, programmerPath, cancellationToken,
            continueOnFailure, skipAbsentSources, labels, progress, verify, skipUfsProvision, tuning, initialization);

    /// <summary>Routes deployment and connection through the selected ADB server or native USB target.<para>经所选 ADB server 或原生 USB 目标完成部署与连接。</para></summary>
    /// <param name="device">The target to mirror.<para>要投屏的目标。</para></param>
    /// <param name="jarPath">Path of the server jar to deploy.<para>要部署的服务 jar 路径。</para></param>
    /// <param name="options">The server launch options.<para>服务启动选项。</para></param>
    /// <returns>The managed mirror session.<para>受管理的投屏会话。</para></returns>
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

    /// <summary>Deploys and opens a native USB mirror stream on a manager-created session.<para>在管理器创建的会话上部署并打开原生 USB 投屏流。</para></summary>
    /// <param name="session">The native USB ADB session carrying the stream.<para>承载该流的原生 USB ADB 会话。</para></param>
    /// <param name="jarPath">Path of the server jar to deploy.<para>要部署的服务 jar 路径。</para></param>
    /// <param name="options">The server launch options.<para>服务启动选项。</para></param>
    /// <returns>The mirror client and its shell stream.<para>投屏客户端及其 shell 流。</para></returns>
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

    /// <summary>Opens an external shell after verifying the selected server target.<para>校验所选 server 目标后打开一个外部 shell。</para></summary>
    /// <param name="device">The target to open the shell on.<para>要打开 shell 的目标。</para></param>
    public void OpenInteractiveShell(DetectedDevice device)
        => DeviceShellService.OpenInteractiveShell(this, device);

    /// <summary>Relays an interactive ADB or HDC shell between the supplied streams.<para>在提供的输入流与输出流之间转发交互式 ADB 或 HDC shell。</para></summary>
    /// <param name="device">The ADB or HDC target to open the shell on.<para>要打开 shell 的 ADB 或 HDC 目标。</para></param>
    /// <param name="input">The stream supplying input to the shell.<para>向 shell 提供输入的流。</para></param>
    /// <param name="output">The stream receiving shell output.<para>接收 shell 输出的流。</para></param>
    /// <param name="cancellationToken">Cancels the relay.<para>取消转发。</para></param>
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

    /// <summary>Stops the selected host server and refreshes the managed device list.<para>停止所选的本机 server 并刷新受管理的设备列表。</para></summary>
    /// <param name="cancellationToken">Cancels the stop request.<para>取消停止请求。</para></param>
    public Task StopAdbServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeServerControl.StopAdbAsync(this, cancellationToken);

    /// <summary>Stops the selected host server and refreshes the managed device list.<para>停止所选的本机 server 并刷新受管理的设备列表。</para></summary>
    /// <param name="cancellationToken">Cancels the stop request.<para>取消停止请求。</para></param>
    public Task StopHdcServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeServerControl.StopHdcAsync(this, cancellationToken);

    /// <summary>Disconnects only the selected network ADB target.<para>仅断开所选的网络 ADB 目标。</para></summary>
    /// <param name="device">The network ADB target to disconnect.<para>要断开的网络 ADB 目标。</para></param>
    /// <param name="cancellationToken">Cancels the disconnect.<para>取消断开操作。</para></param>
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
    /// <summary>Reboots through the routed debug bridge session or the Fastboot transport.<para>经路由的调试桥会话或 Fastboot 传输执行重启。</para></summary>
    /// <param name="device">The device to reboot.<para>要重启的设备。</para></param>
    /// <param name="mode">The requested boot destination, such as recovery or bootloader.<para>请求的启动目的地，例如 recovery 或 bootloader。</para></param>
    /// <param name="cancellationToken">Cancels the reboot request.<para>取消重启请求。</para></param>
    /// <returns><see langword="true"/> when a routed session handled the request.<para>有路由会话处理了该请求时为 <see langword="true"/>。</para></returns>
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
    /// Queries the ADB device state (adb server preferred; direct handshake when the server connection is refused).
    /// <para>查询 ADB 设备状态（优先 adb server；不可达时通过直连握手确认状态）。</para>
    /// </summary>
    /// <param name="serial">The serial of the ADB target to query.<para>要查询的 ADB 目标序列号。</para></param>
    /// <param name="cancellationToken">Cancels the state query.<para>取消状态查询。</para></param>
    /// <returns>The lower-case device state, or <c>unknown</c>.<para>小写的设备状态，未知时为 <c>unknown</c>。</para></returns>
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