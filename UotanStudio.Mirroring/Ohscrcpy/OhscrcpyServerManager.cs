using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Mirroring;

internal static partial class OhscrcpyServerManager
{
    private const string ControlDaemonProcessMarker = "uitest start-daemon singleness";
    private const string ExtensionProcessMarker = "--extension-name ";
    private const string ExtensionExclusionMarker = "--extension-name";
    private const string ShellWrapperMarker = "sh -c";
    private const string RemoteControlAgentPath =
        OhscrcpyConstants.DeviceRuntimeDirectory + "/" + OhscrcpyConstants.ControlAgentFileName;
    private const int ControlDaemonPollAttempts = 15;
    private const int ControlDaemonPollDelayMs = 150;
    private const int VideoDaemonPollAttempts = 10;
    private const int VideoDaemonPollDelayMs = 200;
    private const int DaemonLogTailBytes = 3000;
    private const int MaximumDiagnosticLength = 3000;
    private const int MaximumProbeOutputLength = 8192;
    private static readonly Version LegacyControlAgentMaximum = new(5, 1, 1, 2);
    private static readonly Version UnalignedControlAgentVersion = new(5, 1, 1, 3);

    [GeneratedRegex(@"(?<!\d)\d+\.\d+\.\d+\.\d+(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex UiTestVersionPattern();

    [GeneratedRegex(@"(?:unix_|server_)(\d+(?:\.\d+)+)", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureLibraryVersionPattern();

    [GeneratedRegex(@"server(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureLibraryBuildPattern();

    /// <summary>
    /// Probes the UI Test version, the CPU architecture and the product model of one device.
    /// <para>探测单台设备的 UI Test 版本、CPU 架构与产品型号。</para>
    /// </summary>
    /// <param name="session">Connected HDC session; every command runs through it.
    /// <para>已连接的 HDC 会话；所有命令都经它执行。</para></param>
    /// <returns>The probed capabilities. <para>探测到的能力。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException">UI Test is missing, reports no four-component version, or is
    /// older than <see cref="OhscrcpyConstants.MinimumUiTestVersion"/>.
    /// <para>UI Test 缺失、没有报告四段式版本，或低于
    /// <see cref="OhscrcpyConstants.MinimumUiTestVersion"/>。</para></exception>
    internal static UiTestDeviceInfo Probe(IHdcDeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string versionOutput;
        try
        {
            versionOutput = OhscrcpyShell.Run(session, OhscrcpyConstants.UiTestExecutable + " --version");
        }
        catch (IOException error)
        {
            throw new NotSupportedException(
                $"UI Test is not usable on this device: {error.Message}. "
                + $"该设备的 UI Test 不可用：{error.Message}。", error);
        }

        var version = ParseUiTestVersion(versionOutput);
        if (version is null)
        {
            throw new NotSupportedException(
                $"The device did not report a usable UI Test version from {OhscrcpyConstants.UiTestExecutable} --version; "
                + $"the output was: {Snippet(versionOutput)}. "
                + $"设备未从 {OhscrcpyConstants.UiTestExecutable} --version 报告可用的 UI Test 版本；输出为："
                + $"{Snippet(versionOutput)}。");
        }

        if (version < OhscrcpyConstants.MinimumUiTestVersion)
        {
            throw new NotSupportedException(
                $"UI Test {version} is too old for the pushed capture library; "
                + $"{OhscrcpyConstants.MinimumUiTestVersion} or newer is required. "
                + $"UI Test {version} 版本过低，推送的采集库至少需要 {OhscrcpyConstants.MinimumUiTestVersion}。");
        }

        var architecture = OhscrcpyShell.Run(session, "uname -m").Trim();
        var model = OhscrcpyShell.Run(session, "param get const.product.model").Trim();
        return new UiTestDeviceInfo(version, architecture, model);
    }

    /// <summary>
    /// Reuses a running UI Test control daemon or installs the matching agent and starts one.
    /// <para>复用正在运行的 UI Test 控制守护进程，或安装匹配的代理并启动一个新的控制守护进程。</para>
    /// </summary>
    /// <param name="session">Connected HDC session; every command and file transfer runs through it.
    /// <para>已连接的 HDC 会话；所有命令与文件传输都经它执行。</para></param>
    /// <param name="device">Probed device capabilities selecting the agent version.
    /// <para>选择代理版本的已探测设备能力。</para></param>
    /// <param name="logSink">Optional progress sink; may be called from the calling thread.
    /// <para>可选进度接收端；可能由调用线程调用。</para></param>
    /// <returns>The owned daemon state; <see cref="ControlDaemonHandle.OwnedProcessIds"/> is empty when an
    /// existing daemon was reused. <para>本会话持有的守护进程状态；复用已有守护进程时
    /// <see cref="ControlDaemonHandle.OwnedProcessIds"/> 为空。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="device"/> is
    /// <see langword="null"/>. <para><paramref name="session"/> 或 <paramref name="device"/> 为
    /// <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The selected control agent is missing from
    /// <see cref="RuntimeDirectory"/>. <para><see cref="RuntimeDirectory"/> 中缺少所选控制代理。</para></exception>
    /// <exception cref="IOException">The agent transfer failed or the daemon did not stay alive.
    /// <para>代理传输失败或守护进程未能保持运行。</para></exception>
    /// <exception cref="NotSupportedException">The session exposes no sync (file transfer) channel.
    /// <para>会话没有可用的 sync（文件传输）通道。</para></exception>
    internal static ControlDaemonHandle EnsureControlDaemon(
        IHdcDeviceSession session, UiTestDeviceInfo device, Action<string>? logSink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(device);
        var existing = FindControlDaemonProcessIds(session);
        if (existing.Count > 0)
        {
            logSink?.Invoke(
                $"Reusing the UI Test control daemon already running on the device (pid {JoinProcessIds(existing)}). "
                + $"复用设备上已有的 UI Test 控制守护进程（进程号 {JoinProcessIds(existing)}）。");
            return new ControlDaemonHandle([], false, RemoteControlAgentPath, "");
        }

        var agentFileName = SelectControlAgent(device.UiTestVersion, device.Architecture);
        var localAgentPath = Path.Combine(RuntimeDirectory, agentFileName);
        if (!File.Exists(localAgentPath))
        {
            throw new FileNotFoundException(
                $"The UI Test control agent {agentFileName} is missing from {RuntimeDirectory}; restore the "
                + "ohos/device runtime assets beside the application. "
                + $"运行时目录 {RuntimeDirectory} 中缺少 UI Test 控制代理 {agentFileName}，请恢复应用旁的 "
                + "ohos/device 运行时资源。", localAgentPath);
        }

        var remoteExists = OhscrcpyShell
            .Run(session, $"if [ -f {RemoteControlAgentPath} ]; then echo yes; fi")
            .Contains("yes", StringComparison.Ordinal);
        var backupPath = "";
        var installed = true;
        if (remoteExists)
        {
            backupPath = $"{OhscrcpyConstants.DeviceRuntimeDirectory}/agent.hdc_mirror_{Guid.NewGuid():N}.bak";
            OhscrcpyShell.Run(session, $"mv {RemoteControlAgentPath} {backupPath}");
        }

        try
        {
            using var sync = session.CreateSyncClient();
            sync.Send(localAgentPath, RemoteControlAgentPath);
        }
        catch (NotSupportedException)
        {
            RestoreControlAgent(session, backupPath, logSink);
            throw;
        }
        catch (Exception error)
        {
            RestoreControlAgent(session, backupPath, logSink);
            throw new IOException(
                $"Pushing the UI Test control agent {agentFileName} to {RemoteControlAgentPath} failed: "
                + $"{error.Message}. "
                + $"推送 UI Test 控制代理 {agentFileName} 到 {RemoteControlAgentPath} 失败：{error.Message}。",
                error);
        }

        logSink?.Invoke(
            $"Installed the UI Test control agent {agentFileName} at {RemoteControlAgentPath}. "
            + $"已安装 UI Test 控制代理 {agentFileName} 到 {RemoteControlAgentPath}。");

        IReadOnlyList<int> owned;
        try
        {
            OhscrcpyShell.Run(session,
                $"{OhscrcpyConstants.UiTestExecutable} {OhscrcpyConstants.UiTestDaemonCommand} "
                + ">/dev/null 2>&1 </dev/null & echo started");
            owned = WaitForControlDaemon(session);
        }
        catch (Exception)
        {
            if (installed)
            {
                RestoreControlAgent(session, backupPath, logSink);
            }

            throw;
        }

        if (owned.Count == 0)
        {
            if (installed)
            {
                RestoreControlAgent(session, backupPath, logSink);
            }

            throw new IOException(
                $"The device did not keep the UI Test control daemon running "
                + $"({OhscrcpyConstants.UiTestExecutable} {OhscrcpyConstants.UiTestDaemonCommand}). "
                + "Unlock the device, stop other UI Test sessions and retry the mirror. "
                + "设备未能保持 UI Test 控制守护进程运行"
                + $"（{OhscrcpyConstants.UiTestExecutable} {OhscrcpyConstants.UiTestDaemonCommand}）。"
                + "请解锁设备、停止其它 UI Test 会话后重试投屏。");
        }

        logSink?.Invoke(
            $"Started the UI Test control daemon (pid {JoinProcessIds(owned)}). "
            + $"已启动 UI Test 控制守护进程（进程号 {JoinProcessIds(owned)}）。");
        return new ControlDaemonHandle(owned, installed, RemoteControlAgentPath, backupPath);
    }

    /// <summary>
    /// Pushes the first capture library that starts and returns its owned daemon state.
    /// <para>推送第一个能够成功启动的采集库，并返回本会话持有的守护进程状态。</para>
    /// </summary>
    /// <param name="session">Connected HDC session; every command and file transfer runs through it.
    /// <para>已连接的 HDC 会话；所有命令与文件传输都经它执行。</para></param>
    /// <param name="device">Probed device capabilities selecting the candidate libraries.
    /// <para>选择候选采集库的已探测设备能力。</para></param>
    /// <param name="options">Capture parameters; every value is validated before any device command.
    /// <para>采集参数；在发送任何设备命令前逐项校验。</para></param>
    /// <param name="logSink">Optional progress sink; may be called from the calling thread.
    /// <para>可选进度接收端；可能由调用线程调用。</para></param>
    /// <returns>The owned extension state. <para>本会话持有的扩展状态。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.
    /// <para>某个参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> holds a value outside the
    /// accepted capture ranges. <para><paramref name="options"/> 的取值超出接受的采集范围。</para></exception>
    /// <exception cref="FileNotFoundException">No matching capture library exists in
    /// <see cref="RuntimeDirectory"/>. <para><see cref="RuntimeDirectory"/> 中没有匹配的采集库。</para></exception>
    /// <exception cref="IOException">Every candidate failed to start; the message carries the last device log.
    /// <para>所有候选都未能启动；消息中包含最后的设备日志。</para></exception>
    /// <exception cref="NotSupportedException">The session exposes no sync (file transfer) channel.
    /// <para>会话没有可用的 sync（文件传输）通道。</para></exception>
    internal static VideoDaemonHandle StartVideoDaemon(
        IHdcDeviceSession session, UiTestDeviceInfo device, OhscrcpyLaunchOptions options,
        Action<string>? logSink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var candidates = SelectVideoLibraries(RuntimeDirectory, device);
        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                $"No capture library for UI Test {device.UiTestVersion} was found in {RuntimeDirectory}; a "
                + $"{(device.UsesUnixSockets ? "Unix-socket" : "legacy TCP")} libscrcpy_server*.z.so file is required. "
                + $"运行时目录 {RuntimeDirectory} 中没有匹配 UI Test {device.UiTestVersion} 的采集库；需要 "
                + $"{(device.UsesUnixSockets ? "Unix 套接字" : "旧版 TCP")} 的 libscrcpy_server*.z.so 文件。");
        }

        var extensionName = OhscrcpyConstants.ExtensionNamePrefix + Guid.NewGuid().ToString("N") + ".z.so";
        var remotePath = OhscrcpyConstants.DeviceRuntimeDirectory + "/" + extensionName;
        var remoteLogPath = remotePath + ".log";
        var extensionMarker = ExtensionProcessMarker + extensionName;
        Exception? lastError = null;
        var lastLog = "";
        using var sync = session.CreateSyncClient();
        foreach (var library in candidates)
        {
            var libraryName = Path.GetFileName(library);
            try
            {
                logSink?.Invoke($"Deploying {libraryName}. 正在部署 {libraryName}。");
                sync.Send(library, remotePath);
                OhscrcpyShell.Run(session, BuildVideoStartCommand(extensionName, options, remoteLogPath));
                var owned = WaitForExtension(session, extensionMarker);
                if (owned.Count > 0)
                {
                    logSink?.Invoke(
                        $"Capture extension {extensionName} is running (pid {JoinProcessIds(owned)}). "
                        + $"采集扩展 {extensionName} 已运行（进程号 {JoinProcessIds(owned)}）。");
                    return new VideoDaemonHandle(extensionName, remotePath, remoteLogPath, owned);
                }

                var deviceLog = ReadDaemonLog(session, remoteLogPath);
                if (deviceLog.Length > 0)
                {
                    lastLog = deviceLog;
                }

                logSink?.Invoke(
                    $"Capture extension {libraryName} exited: {Snippet(deviceLog)}. "
                    + $"采集扩展 {libraryName} 已退出：{Snippet(deviceLog)}。");
            }
            catch (Exception error) when (IsTransportLost(error))
            {
                throw new IOException(
                    $"The HDC server or session was lost while deploying {libraryName}; stop retrying the "
                    + $"remaining capture libraries and reconnect the target. "
                    + $"部署 {libraryName} 时 HDC server 或会话已丢失；停止重试剩余采集库并重新连接目标。",
                    error);
            }
            catch (Exception error)
            {
                lastError = error;
                logSink?.Invoke(
                    $"Capture extension {libraryName} failed: {error.Message}. "
                    + $"采集扩展 {libraryName} 失败：{error.Message}。");
            }

            RemoveRemoteFiles(session, remotePath, remoteLogPath, logSink);
        }

        throw new IOException(
            $"Every pushed capture library failed to start on the device "
            + $"(UI Test {device.UiTestVersion}, {candidates.Count} candidate(s) tried). "
            + $"Last device log: {Snippet(lastLog)}. "
            + $"所有推送的采集库都未能在设备上启动（UI Test {device.UiTestVersion}，已尝试 "
            + $"{candidates.Count} 个候选）。最后的设备日志：{Snippet(lastLog)}。", lastError);
    }

    /// <summary>
    /// Stops the control daemons this session started, then removes or restores the control agent.
    /// <para>停止本会话启动的控制守护进程，随后移除或恢复控制代理。</para>
    /// </summary>
    /// <param name="session">Connected HDC session; a <see langword="null"/> session is ignored.
    /// <para>已连接的 HDC 会话；为 <see langword="null"/> 时直接忽略。</para></param>
    /// <param name="handle">Owned state returned by <see cref="EnsureControlDaemon"/>.
    /// <para><see cref="EnsureControlDaemon"/> 返回的持有状态。</para></param>
    /// <param name="logSink">Optional sink receiving cleanup failures; this method never throws.
    /// <para>可选接收清理失败的接收端；本方法从不抛出异常。</para></param>
    internal static void StopControlDaemon(
        IHdcDeviceSession session, ControlDaemonHandle handle, Action<string>? logSink)
    {
        if (session is null || handle is null)
        {
            return;
        }

        try
        {
            if (handle.OwnedProcessIds.Count > 0)
            {
                KillLiveProcesses(session, handle.OwnedProcessIds, FindControlDaemonProcessIds(session), logSink);
            }
        }
        catch (Exception error)
        {
            logSink?.Invoke(
                $"Stopping the UI Test control daemon was incomplete: {error.Message}. "
                + $"停止 UI Test 控制守护进程未完成：{error.Message}。");
        }

        if (!handle.InstalledAgent)
        {
            return;
        }

        try
        {
            RemoveControlAgent(session, handle.BackupPath, logSink);
        }
        catch (Exception error)
        {
            logSink?.Invoke(
                $"Restoring the original UI Test control agent was incomplete: {error.Message}. "
                + $"恢复原有 UI Test 控制代理未完成：{error.Message}。");
        }
    }

    /// <summary>
    /// Stops the extension processes this session started and deletes its pushed library and log.
    /// <para>停止本会话启动的扩展进程，并删除其推送的库与日志。</para>
    /// </summary>
    /// <param name="session">Connected HDC session; a <see langword="null"/> session is ignored.
    /// <para>已连接的 HDC 会话；为 <see langword="null"/> 时直接忽略。</para></param>
    /// <param name="handle">Owned state returned by <see cref="StartVideoDaemon"/>.
    /// <para><see cref="StartVideoDaemon"/> 返回的持有状态。</para></param>
    /// <param name="logSink">Optional sink receiving cleanup failures; this method never throws.
    /// <para>可选接收清理失败的接收端；本方法从不抛出异常。</para></param>
    internal static void StopVideoDaemon(
        IHdcDeviceSession session, VideoDaemonHandle handle, Action<string>? logSink)
    {
        if (session is null || handle is null)
        {
            return;
        }

        try
        {
            if (handle.ProcessIds.Count > 0)
            {
                var extensionMarker = ExtensionProcessMarker + handle.ExtensionName;
                KillLiveProcesses(session, handle.ProcessIds, FindExtensionProcessIds(session, extensionMarker),
                    logSink);
            }
        }
        catch (Exception error)
        {
            logSink?.Invoke(
                $"Stopping the capture extension was incomplete: {error.Message}. "
                + $"停止采集扩展未完成：{error.Message}。");
        }

        RemoveRemoteFiles(session, handle.RemotePath, handle.RemoteLogPath, logSink);
    }

    /// <summary>
    /// Selects the UI Test control agent file name for a device version and architecture.
    /// <para>按设备版本与架构选择 UI Test 控制代理文件名。</para>
    /// </summary>
    /// <param name="version">Four-component device UI Test version.
    /// <para>四段式设备 UI Test 版本。</para></param>
    /// <param name="architecture">Device <c>uname -m</c> text; an <c>x86_64</c> substring selects the x86 agent.
    /// <para>设备 <c>uname -m</c> 文本；包含 <c>x86_64</c> 时选择 x86 代理。</para></param>
    /// <returns>The agent file name below <see cref="RuntimeDirectory"/>.
    /// <para><see cref="RuntimeDirectory"/> 下的代理文件名。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.
    /// <para><paramref name="version"/> 为 <see langword="null"/>。</para></exception>
    internal static string SelectControlAgent(Version version, string architecture)
    {
        ArgumentNullException.ThrowIfNull(version);
        var agent = version <= LegacyControlAgentMaximum ? "uitest_agent_1.1.3.so"
            : version == UnalignedControlAgentVersion ? "uitest_agent_1.1.5.so"
            : version > OhscrcpyConstants.UnixSocketMinimumVersion ? "uitest_agent_1.2.3.so"
            : "uitest_agent_1.1.12.so";
        return !string.IsNullOrEmpty(architecture) &&
            architecture.Contains("x86_64", StringComparison.Ordinal)
                ? "uitest_agent_x86_1.1.12.so"
                : agent;
    }

    /// <summary>
    /// Lists the local capture libraries that match a device's socket layout, newest first.
    /// <para>按设备套接字布局列出匹配的本地采集库，最新版本在前。</para>
    /// </summary>
    /// <param name="directory">Local runtime directory; a missing directory yields an empty list.
    /// <para>本地运行时目录；目录不存在时返回空列表。</para></param>
    /// <param name="device">Probed device capabilities selecting the socket layout.
    /// <para>选择套接字布局的已探测设备能力。</para></param>
    /// <returns>Absolute library paths, newest version first. <para>库的绝对路径，最新版本在前。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty.
    /// <para><paramref name="directory"/> 为空。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.
    /// <para><paramref name="device"/> 为 <see langword="null"/>。</para></exception>
    internal static IReadOnlyList<string> SelectVideoLibraries(string directory, UiTestDeviceInfo device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(device);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.so")
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(file => !file.Name.Contains("uitest_agent", StringComparison.Ordinal))
            .Where(file => file.Name.Contains("_unix_", StringComparison.Ordinal) == device.UsesUnixSockets)
            .OrderByDescending(file => ParseCaptureLibraryVersion(file.Name))
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => file.Path)
            .ToArray();
    }

    /// <summary>
    /// Builds the device command that starts one pushed capture extension in the background.
    /// <para>构造在后台启动一次推送采集扩展的设备命令。</para>
    /// </summary>
    /// <param name="extensionName">Pushed extension file name, without a directory.
    /// <para>推送扩展的文件名，不含目录。</para></param>
    /// <param name="options">Capture parameters; they are validated here.
    /// <para>采集参数；在此处校验。</para></param>
    /// <param name="remoteLogPath">Device log path receiving the daemon standard streams.
    /// <para>接收守护进程标准流的设备日志路径。</para></param>
    /// <returns>The shell command ending with <c>&amp; echo started</c>.
    /// <para>以 <c>&amp; echo started</c> 结尾的 shell 命令。</para></returns>
    /// <exception cref="ArgumentException">A name or path argument is empty.
    /// <para>某个名称或路径参数为空。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> holds a value outside the
    /// accepted capture ranges. <para><paramref name="options"/> 的取值超出接受的采集范围。</para></exception>
    internal static string BuildVideoStartCommand(
        string extensionName, OhscrcpyLaunchOptions options, string remoteLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteLogPath);
        options.Validate();
        return $"{OhscrcpyConstants.UiTestExecutable} {OhscrcpyConstants.UiTestDaemonCommand} "
            + $"--extension-name {extensionName} -scale 1 -frameRate {options.FrameRate} "
            + $"-bitRate {options.BitRate} -p 5000 -iFrameInterval {options.KeyFrameIntervalMs} "
            + $"-repeatInterval {1000 / options.FrameRate} > {remoteLogPath} 2>&1 < /dev/null & echo started";
    }

    /// <summary>
    /// Extracts the process ids of the <c>ps -ef</c> lines containing one marker.
    /// <para>提取包含指定标记的 <c>ps -ef</c> 行的进程号。</para>
    /// </summary>
    /// <param name="psOutput"><c>ps -ef</c> text; the process id is the second column.
    /// <para><c>ps -ef</c> 文本；进程号位于第二列。</para></param>
    /// <param name="marker">Literal command-line fragment identifying the owned processes.
    /// <para>标识所属进程的命令行字面片段。</para></param>
    /// <returns>Distinct positive process ids in output order; empty when nothing matches.
    /// <para>按输出顺序排列的去重正进程号；没有匹配时为空。</para></returns>
    internal static IReadOnlyList<int> ParseProcessIds(string psOutput, string marker)
    {
        if (string.IsNullOrEmpty(psOutput) || string.IsNullOrEmpty(marker))
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var line in psOutput.Split('\n'))
        {
            if (!line.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > 2 &&
                int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                pid > 0 &&
                !ids.Contains(pid))
            {
                ids.Add(pid);
            }
        }

        return ids;
    }

    /// <summary>
    /// Parses the first four-component version reported by <c>uitest --version</c>.
    /// <para>解析 <c>uitest --version</c> 报告的首个四段式版本。</para>
    /// </summary>
    /// <param name="output">Command output; only a bounded prefix is inspected.
    /// <para>命令输出；只检查有界前缀。</para></param>
    /// <returns>The parsed version, or <see langword="null"/> when the output holds no four-component version.
    /// <para>解析出的版本；输出中没有四段式版本时为 <see langword="null"/>。</para></returns>
    internal static Version? ParseUiTestVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var bounded = output.Length <= MaximumProbeOutputLength ? output : output[..MaximumProbeOutputLength];
        var match = UiTestVersionPattern().Match(bounded);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static IReadOnlyList<int> FindControlDaemonProcessIds(IHdcDeviceSession session) =>
        ParseProcessIds(ExcludeProcessLines(OhscrcpyShell.Run(session, "ps -ef"),
            ExtensionExclusionMarker, ShellWrapperMarker), ControlDaemonProcessMarker);

    private static IReadOnlyList<int> FindExtensionProcessIds(IHdcDeviceSession session, string extensionMarker) =>
        ParseProcessIds(ExcludeProcessLines(OhscrcpyShell.Run(session, "ps -ef"), ShellWrapperMarker),
            extensionMarker);

    private static string ExcludeProcessLines(string psOutput, params string[] fragments)
    {
        if (string.IsNullOrEmpty(psOutput))
        {
            return "";
        }

        var filtered = new StringBuilder(psOutput.Length);
        foreach (var line in psOutput.Split('\n'))
        {
            var excluded = false;
            foreach (var fragment in fragments)
            {
                if (line.Contains(fragment, StringComparison.Ordinal))
                {
                    excluded = true;
                    break;
                }
            }

            if (!excluded)
            {
                filtered.Append(line).Append('\n');
            }
        }

        return filtered.ToString();
    }

    private static IReadOnlyList<int> WaitForControlDaemon(IHdcDeviceSession session)
    {
        for (var attempt = 0; attempt < ControlDaemonPollAttempts; attempt++)
        {
            Thread.Sleep(ControlDaemonPollDelayMs);
            var owned = FindControlDaemonProcessIds(session);
            if (owned.Count > 0)
            {
                return owned;
            }
        }

        return [];
    }

    private static IReadOnlyList<int> WaitForExtension(IHdcDeviceSession session, string extensionMarker)
    {
        for (var attempt = 0; attempt < VideoDaemonPollAttempts; attempt++)
        {
            Thread.Sleep(VideoDaemonPollDelayMs);
            var owned = FindExtensionProcessIds(session, extensionMarker);
            if (owned.Count > 0)
            {
                return owned;
            }
        }

        return [];
    }

    private static void KillLiveProcesses(
        IHdcDeviceSession session, IReadOnlyList<int> ownedProcessIds, IReadOnlyList<int> liveProcessIds,
        Action<string>? logSink)
    {
        var live = new HashSet<int>(liveProcessIds);
        foreach (var pid in ownedProcessIds)
        {
            if (!live.Contains(pid))
            {
                continue;
            }

            try
            {
                OhscrcpyShell.Run(session, $"kill -9 {pid}");
            }
            catch (Exception error)
            {
                logSink?.Invoke(
                    $"Killing device process {pid} failed: {error.Message}. "
                    + $"结束设备进程 {pid} 失败：{error.Message}。");
            }
        }
    }

    private static void RemoveControlAgent(IHdcDeviceSession session, string backupPath, Action<string>? logSink)
    {
        OhscrcpyShell.Run(session, "rm -f " + RemoteControlAgentPath);
        if (string.IsNullOrEmpty(backupPath))
        {
            logSink?.Invoke(
                $"Removed the UI Test control agent at {RemoteControlAgentPath}. "
                + $"已移除 UI Test 控制代理 {RemoteControlAgentPath}。");
            return;
        }

        OhscrcpyShell.Run(session, $"mv {backupPath} {RemoteControlAgentPath}");
        logSink?.Invoke(
            $"Restored the previous UI Test control agent from {backupPath}. "
            + $"已从 {backupPath} 恢复原有的 UI Test 控制代理。");
    }

    private static void RestoreControlAgent(IHdcDeviceSession session, string backupPath, Action<string>? logSink)
    {
        try
        {
            RemoveControlAgent(session, backupPath, logSink);
        }
        catch (Exception error)
        {
            logSink?.Invoke(
                $"Restoring the original UI Test control agent was incomplete: {error.Message}. "
                + $"恢复原有 UI Test 控制代理未完成：{error.Message}。");
        }
    }

    private static void RemoveRemoteFiles(
        IHdcDeviceSession session, string remotePath, string remoteLogPath, Action<string>? logSink)
    {
        try
        {
            OhscrcpyShell.Run(session, $"rm -f {remotePath} {remoteLogPath}");
        }
        catch (Exception error)
        {
            logSink?.Invoke(
                $"Removing the pushed capture files failed: {error.Message}. "
                + $"删除推送的采集文件失败：{error.Message}。");
        }
    }

    private static string ReadDaemonLog(IHdcDeviceSession session, string remoteLogPath)
    {
        try
        {
            return OhscrcpyShell.Run(session, $"tail -c {DaemonLogTailBytes} {remoteLogPath}").Trim();
        }
        catch (Exception error)
        {
            return $"({error.Message})";
        }
    }

    private static Version ParseCaptureLibraryVersion(string fileName)
    {
        var match = CaptureLibraryVersionPattern().Match(fileName);
        if (match.Success && Version.TryParse(match.Groups[1].Value, out var version))
        {
            return version;
        }

        match = CaptureLibraryBuildPattern().Match(fileName);
        return match.Success &&
            int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var build)
                ? new Version(0, build)
                : new Version(0, 0);
    }

    private static bool IsTransportLost(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ObjectDisposedException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    private static string JoinProcessIds(IReadOnlyList<int> processIds) => string.Join(", ", processIds);

    private static string Snippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= MaximumDiagnosticLength
            ? singleLine
            : singleLine[..MaximumDiagnosticLength] + "…";
    }
}