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
    private const int ControlDaemonReadyBudgetMs = 10_000;
    private const int ControlDaemonPollDelayMs = 150;
    private const int ExtensionReadyBudgetMs = 6_000;
    private const int ExtensionPollDelayMs = 200;
    private const int ExtensionSettleMilliseconds = 400;
    private const int ProbeAttempts = 3;
    private const int ProbeRetryDelayMs = 250;
    private const int RemoteFileVisibleBudgetMs = 2_000;
    private const int RemoteFileVisiblePollMs = 100;
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
    /// <param name="cancellationToken">Cancels the remaining probes between attempts.
    /// <para>在两次尝试之间取消剩余探测。</para></param>
    /// <returns>The probed capabilities. <para>探测到的能力。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">The device did not answer the probe shell commands after retrying, for example
    /// because the target was still enumerating or the UI Test service was not up yet; the start may be retried.
    /// <para>设备在重试之后仍未响应探测命令（例如目标仍在枚举，或 UI Test 服务尚未就绪）；可以重试启动。</para></exception>
    /// <exception cref="NotSupportedException">UI Test reports no four-component version, or is older than
    /// <see cref="OhscrcpyConstants.MinimumUiTestVersion"/>. <para>UI Test 没有报告四段式版本，或低于
    /// <see cref="OhscrcpyConstants.MinimumUiTestVersion"/>。</para></exception>
    internal static UiTestDeviceInfo Probe(IHdcDeviceSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var versionOutput = RunProbeCommand(
            session, OhscrcpyConstants.UiTestExecutable + " --version", cancellationToken);

        var version = ParseUiTestVersion(versionOutput);
        if (version is null)
        {
            throw new NotSupportedException(
                OhscrcpyFailure.CapabilityPrefix
                + $"the device did not report a usable UI Test version from "
                + $"{OhscrcpyConstants.UiTestExecutable} --version; the output was: {Snippet(versionOutput)}. "
                + OhscrcpyFailure.CapabilityPrefix
                + $"设备未从 {OhscrcpyConstants.UiTestExecutable} --version 报告可用的 UI Test 版本；输出为："
                + $"{Snippet(versionOutput)}。");
        }

        if (version < OhscrcpyConstants.MinimumUiTestVersion)
        {
            throw new NotSupportedException(
                OhscrcpyFailure.CapabilityPrefix
                + $"UI Test {version} is too old for the pushed capture library; "
                + $"{OhscrcpyConstants.MinimumUiTestVersion} or newer is required. "
                + OhscrcpyFailure.CapabilityPrefix
                + $"UI Test {version} 版本过低，推送的采集库至少需要 {OhscrcpyConstants.MinimumUiTestVersion}。");
        }

        var architecture = RunProbeCommand(session, "uname -m", cancellationToken).Trim();
        var model = RunProbeCommand(session, "param get const.product.model", cancellationToken).Trim();
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
    /// <param name="cancellationToken">Cancels the daemon readiness wait between polls.
    /// <para>在轮询之间取消守护进程就绪等待。</para></param>
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
        IHdcDeviceSession session, UiTestDeviceInfo device, Action<string>? logSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        var existing = FindControlDaemonProcessIds(session);
        if (existing.Count > 0)
        {
            logSink?.Invoke(
                $"Reusing the UI Test control daemon already running on the device "
                + $"(pid {JoinProcessIds(existing)}); its agent is left untouched. "
                + $"复用设备上已有的 UI Test 控制守护进程（进程号 {JoinProcessIds(existing)}），不替换其代理。");
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
                OhscrcpyFailure.NotReadyPrefix
                + $"the UI Test control agent {agentFileName} did not reach {RemoteControlAgentPath}: "
                + $"{error.Message}. "
                + OhscrcpyFailure.NotReadyPrefix
                + $"UI Test 控制代理 {agentFileName} 没有传到 {RemoteControlAgentPath}：{error.Message}。",
                error);
        }

        if (!WaitForRemoteFile(session, RemoteControlAgentPath, cancellationToken))
        {
            RestoreControlAgent(session, backupPath, logSink);
            throw new IOException(
                OhscrcpyFailure.NotReadyPrefix
                + $"the UI Test control agent reached {RemoteControlAgentPath} but the device never listed it "
                + $"within {RemoteFileVisibleBudgetMs} ms; the upload was not committed. Retry mirroring. "
                + OhscrcpyFailure.NotReadyPrefix
                + $"UI Test 控制代理已传到 {RemoteControlAgentPath}，但设备在 {RemoteFileVisibleBudgetMs} ms 内始终看不到"
                + "该文件，上传未落盘。请重新开始投屏。");
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
            owned = WaitForControlDaemon(session, cancellationToken);
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
                OhscrcpyFailure.NotReadyPrefix
                + "the device did not keep the UI Test control daemon running within "
                + $"{ControlDaemonReadyBudgetMs} ms "
                + $"({OhscrcpyConstants.UiTestExecutable} {OhscrcpyConstants.UiTestDaemonCommand}). "
                + "Unlock the device, close other UI Test sessions (DevEco Studio screen mirroring counts as one), "
                + "then start mirroring again. "
                + OhscrcpyFailure.NotReadyPrefix
                + $"设备未在 {ControlDaemonReadyBudgetMs} ms 内保持 UI Test 控制守护进程运行"
                + $"（{OhscrcpyConstants.UiTestExecutable} {OhscrcpyConstants.UiTestDaemonCommand}）。"
                + "请解锁设备、关闭其它 UI Test 会话（DevEco Studio 的投屏也算一个）后重新开始投屏。");
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
    /// <param name="cancellationToken">Cancels the extension readiness wait and the settle check.
    /// <para>取消扩展就绪等待与稳定检查。</para></param>
    /// <returns>The owned extension state. <para>本会话持有的扩展状态。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.
    /// <para>某个参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> holds a value outside the
    /// accepted capture ranges. <para><paramref name="options"/> 的取值超出接受的采集范围。</para></exception>
    /// <exception cref="FileNotFoundException">No matching capture library exists in
    /// <see cref="RuntimeDirectory"/>. <para><see cref="RuntimeDirectory"/> 中没有匹配的采集库。</para></exception>
    /// <exception cref="IOException">No candidate stayed running, for example because the device was still
    /// starting up; the message carries the last device log and the start may be retried.
    /// <para>没有任何候选保持运行（例如设备仍在启动）；消息中包含最后的设备日志，可以重试启动。</para></exception>
    /// <exception cref="NotSupportedException">The session exposes no sync (file transfer) channel, or the
    /// device could not load any pushed capture library because its dynamic dependencies do not match this system
    /// build, which retrying cannot fix.
    /// <para>会话没有可用的 sync（文件传输）通道，或设备因动态依赖与该系统版本不匹配而无法加载任何推送的采集库；
    /// 后者重试无济于事。</para></exception>
    internal static VideoDaemonHandle StartVideoDaemon(
        IHdcDeviceSession session, UiTestDeviceInfo device, OhscrcpyLaunchOptions options,
        Action<string>? logSink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
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
        NotSupportedException? capabilityFailure = null;
        var lastLog = "";
        using var sync = session.CreateSyncClient();
        foreach (var library in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryName = Path.GetFileName(library);
            try
            {
                logSink?.Invoke($"Deploying {libraryName}. 正在部署 {libraryName}。");
                sync.Send(library, remotePath);
                if (!WaitForRemoteFile(session, remotePath, cancellationToken))
                {
                    throw new IOException(
                        OhscrcpyFailure.NotReadyPrefix
                        + $"{libraryName} reached {remotePath} but the device never listed it within "
                        + $"{RemoteFileVisibleBudgetMs} ms; the upload was not committed. Retry mirroring. "
                        + OhscrcpyFailure.NotReadyPrefix
                        + $"{libraryName} 已传到 {remotePath}，但设备在 {RemoteFileVisibleBudgetMs} ms 内始终看不到该文件，"
                        + "上传未落盘。请重新开始投屏。");
                }

                OhscrcpyShell.Run(session, BuildVideoStartCommand(extensionName, options, remoteLogPath));
                var owned = WaitForExtension(session, extensionMarker, cancellationToken);
                if (owned.Count > 0 && ExtensionSurvivesSettle(session, extensionMarker, owned, cancellationToken))
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

                if (OhscrcpyFailure.LooksLikeAbiIncompatibility(deviceLog))
                {
                    capabilityFailure = BuildCapabilityFailure(libraryName, device, deviceLog, lastError);
                    logSink?.Invoke(capabilityFailure.Message);
                    break;
                }

                logSink?.Invoke(
                    $"Capture extension {libraryName} exited or never became visible within "
                    + $"{ExtensionReadyBudgetMs} ms: {Snippet(deviceLog)}. "
                    + $"采集扩展 {libraryName} 已退出，或在 {ExtensionReadyBudgetMs} ms 内没有出现：{Snippet(deviceLog)}。");
            }
            catch (Exception error) when (IsTransportLost(error))
            {
                throw new IOException(
                    OhscrcpyFailure.NotReadyPrefix
                    + $"the HDC session was lost while deploying {libraryName}; reconnect the target and start "
                    + "mirroring again. "
                    + OhscrcpyFailure.NotReadyPrefix
                    + $"部署 {libraryName} 时 HDC 会话丢失；请重新连接目标后再开始投屏。",
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

        if (capabilityFailure is not null)
        {
            RemoveRemoteFiles(session, remotePath, remoteLogPath, logSink);
            throw capabilityFailure;
        }

        throw new IOException(
            OhscrcpyFailure.NotReadyPrefix
            + "no pushed capture library stayed running on the device "
            + $"(UI Test {device.UiTestVersion}, {candidates.Count} candidate(s) tried). Unlock the device and start "
            + "mirroring again; a library that keeps exiting with a loader error is a capability gap, which the log "
            + $"above shows. Last device log: {Snippet(lastLog)}. "
            + OhscrcpyFailure.NotReadyPrefix
            + $"没有任何推送的采集库在设备上保持运行（UI Test {device.UiTestVersion}，已尝试 {candidates.Count} 个候选）。"
            + "请解锁设备后重新开始投屏；如果某个库持续以加载错误退出，则属于能力缺口，上面的日志会给出证据。"
            + $"最后的设备日志：{Snippet(lastLog)}。", lastError);
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

    private static IReadOnlyList<int> WaitForControlDaemon(
        IHdcDeviceSession session, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + ControlDaemonReadyBudgetMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(ControlDaemonPollDelayMs);
            var owned = FindControlDaemonProcessIds(session);
            if (owned.Count > 0)
            {
                return owned;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return [];
            }
        }
    }

    private static IReadOnlyList<int> WaitForExtension(
        IHdcDeviceSession session, string extensionMarker, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + ExtensionReadyBudgetMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(ExtensionPollDelayMs);
            var owned = FindExtensionProcessIds(session, extensionMarker);
            if (owned.Count > 0)
            {
                return owned;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Waits until a just-pushed file is visible to the device shell, so a start command never runs before the upload
    /// is committed on a slow target. <para>等待刚推送的文件对设备 shell 可见，使启动命令不会在慢速目标尚未提交上传时执行。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <param name="remotePath">Absolute device path of the pushed file.
    /// <para>已推送文件的设备绝对路径。</para></param>
    /// <param name="cancellationToken">Cancels the poll between attempts.
    /// <para>在两次轮询之间取消。</para></param>
    /// <returns><see langword="true"/> when the file became visible within the budget.
    /// <para>文件在预算内变得可见时为 <see langword="true"/>。</para></returns>
    private static bool WaitForRemoteFile(
        IHdcDeviceSession session, string remotePath, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + RemoteFileVisibleBudgetMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = OhscrcpyShell.Run(session, $"if [ -f {remotePath} ]; then echo visible; fi");
            if (output.Contains("visible", StringComparison.Ordinal))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(RemoteFileVisiblePollMs);
        }
    }

    /// <summary>
    /// Confirms that at least one process of a just-started extension is still alive after a short settle window, so
    /// a library that the device loads and then aborts on is reported as a failed candidate instead of a running
    /// extension. <para>在短暂的稳定窗口后确认刚启动的扩展至少还有一个进程存活，使设备加载后立即退出的库被报告为
    /// 失败候选，而不是"正在运行"的扩展。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <param name="extensionMarker">Unique extension marker of this session.
    /// <para>本会话唯一的扩展标记。</para></param>
    /// <param name="owned">Process ids observed right after the start command.
    /// <para>启动命令之后立即观测到的进程号。</para></param>
    /// <param name="cancellationToken">Cancels the settle wait. <para>取消稳定等待。</para></param>
    /// <returns><see langword="true"/> when one owned process is still alive.
    /// <para>仍有一个所属进程存活时为 <see langword="true"/>。</para></returns>
    private static bool ExtensionSurvivesSettle(IHdcDeviceSession session, string extensionMarker,
        IReadOnlyList<int> owned, CancellationToken cancellationToken)
    {
        Thread.Sleep(ExtensionSettleMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();
        var live = new HashSet<int>(FindExtensionProcessIds(session, extensionMarker));
        return owned.Any(live.Contains);
    }

    /// <summary>
    /// Builds the capability failure for a capture library the device cannot load, quoting its log so the next step
    /// is actionable. <para>为设备无法加载的采集库构造能力失败，并引用其日志，使下一步可操作。</para>
    /// </summary>
    /// <param name="libraryName">Pushed library file name. <para>推送的库文件名。</para></param>
    /// <param name="device">Probed device capabilities. <para>已探测的设备能力。</para></param>
    /// <param name="deviceLog">Capture-extension log excerpt. <para>采集扩展日志片段。</para></param>
    /// <param name="inner">Last transport error, when one exists. <para>最后的传输错误（如果有）。</para></param>
    /// <returns>The classified capability failure. <para>分类后的能力失败。</para></returns>
    private static NotSupportedException BuildCapabilityFailure(
        string libraryName, UiTestDeviceInfo device, string deviceLog, Exception? inner)
        => new(
            OhscrcpyFailure.CapabilityPrefix
            + $"{libraryName} could not run under UI Test {device.UiTestVersion} on {device.Architecture}; its "
            + "dynamic dependencies do not match this system build. Replace the ohos/device capture library with a "
            + $"build for this system version, or mirror from a supported device. Capture log: {Snippet(deviceLog)} "
            + OhscrcpyFailure.CapabilityPrefix
            + $"{libraryName} 无法在 UI Test {device.UiTestVersion}（{device.Architecture}）下运行，其动态依赖与该系统"
            + "版本不匹配。请把 ohos/device 下的采集库替换为该系统版本的构建，或改用受支持的设备投屏。采集日志："
            + Snippet(deviceLog),
            inner);

    /// <summary>
    /// Runs one probe shell command, retrying only transport failures so a target that is still enumerating is not
    /// reported as missing UI Test. <para>执行一条探测 shell 命令，只对传输失败重试，使仍在枚举的目标不会被报告为
    /// 缺少 UI Test。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <param name="command">Command text. <para>命令文本。</para></param>
    /// <param name="cancellationToken">Cancels the retries between attempts.
    /// <para>在两次尝试之间取消重试。</para></param>
    /// <returns>The trimmed command output. <para>去除首尾空白的命令输出。</para></returns>
    /// <exception cref="IOException">The device kept failing the command; the start may be retried.
    /// <para>设备持续拒绝该命令；可以重试启动。</para></exception>
    private static string RunProbeCommand(
        IHdcDeviceSession session, string command, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < ProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return OhscrcpyShell.Run(session, command);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                last = error;
                if (!OhscrcpyFailure.IsConnectionFailure(error) || attempt + 1 >= ProbeAttempts)
                {
                    break;
                }

                Thread.Sleep(ProbeRetryDelayMs * (attempt + 1));
            }
        }

        throw new IOException(
            OhscrcpyFailure.NotReadyPrefix
            + $"the device did not answer `{command}`: {last?.Message}. Unlock the device, confirm the UI Test "
            + "service is running, then start mirroring again. "
            + OhscrcpyFailure.NotReadyPrefix
            + $"设备没有响应 `{command}`：{last?.Message}。请解锁设备、确认 UI Test 服务正在运行后重新开始投屏。",
            last);
    }

    /// <summary>
    /// Reads a bounded tail of one pushed capture extension's device log for failure classification.
    /// <para>读取一条推送采集扩展的设备日志尾部（有界），用于失败分类。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <param name="remoteLogPath">Device path of the extension log.
    /// <para>扩展日志的设备路径。</para></param>
    /// <returns>The log tail, or an empty string when it cannot be read.
    /// <para>日志尾部；无法读取时为空串。</para></returns>
    internal static string ReadExtensionLog(IHdcDeviceSession session, string remoteLogPath)
    {
        if (session is null || string.IsNullOrWhiteSpace(remoteLogPath))
        {
            return "";
        }

        return ReadDaemonLog(session, remoteLogPath);
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