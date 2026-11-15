using System.Globalization;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>
/// Executes a parsed wrapped command inside the toolbox and renders official-style output text.
/// The runner never spawns tool binaries: every supported command maps onto the host's own device
/// services through <see cref="IWrappedCommandGateway"/>.
/// <para>在工具箱内部执行解析后的包装命令并渲染官方风格的输出文本。执行器绝不启动工具二进制：每个受支持的
/// 命令都经 <see cref="IWrappedCommandGateway"/> 映射到宿主自己的设备服务。</para>
/// </summary>
/// <param name="gateway">Device operations seam.<para>设备操作缝隙。</para></param>
/// <param name="version">Toolbox version rendered into <c>version</c> output.<para>渲染进 version 输出的工具箱版本。</para></param>
internal sealed partial class WrappedCommandRunner(IWrappedCommandGateway gateway, string version)
{
    /// <summary>Gets the banner identifying the wrapped command line tool.
    /// <para>获取标识包装命令行工具的横幅。</para></summary>
    internal const string Banner = "UotanStudio Wrapped CommandLine Tool";

    /// <summary>Gets the ADB protocol version the wrapped tool reports.<para>获取包装工具上报的 ADB 协议版本。</para></summary>
    internal const string AdbProtocolVersion = "1.0.41";

    /// <summary>Sub-commands the ADB dispatcher recognizes; unknown names are left to the real shell.
    /// <para>ADB 分发器识别的子命令；未知名称交还真实 shell。</para></summary>
    internal static readonly IReadOnlyList<string> AdbCommands =
    [
        "devices", "help", "version", "shell", "push", "pull", "install", "uninstall", "reboot",
        "reboot-bootloader", "reboot-recovery", "reboot-fastboot", "reboot-sideload", "connect",
        "disconnect", "pair", "root", "unroot", "tcpip", "usb", "get-state", "get-serialno",
        "get-devpath", "wait-for-device", "wait-for-recovery", "wait-for-sideload", "wait-for-bootloader",
        "start-server", "kill-server", "logcat", "bugreport", "remount", "disable-verity",
        "enable-verity", "forward", "reverse", "mdns", "emu", "jdwp", "features", "reconnect",
        "install-multiple", "install-multi-package", "backup", "restore", "sync", "keygen",
        "host-features", "transport-id", "abb", "abb_exec", "track-devices", "track-jdwp",
        "track-apps", "trace", "device",
    ];

    /// <summary>Sub-commands the fastboot dispatcher recognizes.<para>fastboot 分发器识别的子命令。</para></summary>
    internal static readonly IReadOnlyList<string> FastbootCommands =
    [
        "devices", "version", "help", "getvar", "flash", "erase", "format", "boot", "download",
        "reboot", "reboot-bootloader", "reboot-recovery", "reboot-fastboot", "reboot-edl", "oem",
        "flashing", "set_active", "continue", "reset", "stage", "get-staged", "fetch", "update",
        "snapshot-update", "flashall",
    ];

    /// <summary>Sub-commands the hdc dispatcher recognizes.<para>hdc 分发器识别的子命令。</para></summary>
    internal static readonly IReadOnlyList<string> HdcCommands =
    [
        "help", "version", "checkserver", "list", "tconn", "tdis", "tmode", "target", "wait",
        "shell", "file", "fport", "rport", "install", "uninstall", "hilog", "bugreport", "jpid",
        "track-jpid", "keygen", "kill", "start", "reconnect", "smode", "discover", "any",
        "checkdevice",
    ];

    /// <summary>Runs the command, writing official-style output and returning the process exit code.
    /// <para>执行命令，写出官方风格输出并返回进程退出码。</para></summary>
    /// <param name="command">The parsed command.<para>解析后的命令。</para></param>
    /// <param name="write">Output callback receiving terminal-ready text.<para>接收终端就绪文本的输出回调。</para></param>
    /// <param name="cancellationToken">Cancels the command.<para>取消命令。</para></param>
    /// <returns>The exit code the real tool would have returned.<para>真实工具会返回的退出码。</para></returns>
    internal async Task<int> RunAsync(WrappedCommand command, Action<string> write,
        CancellationToken cancellationToken = default)
    {
        var context = new Context(gateway, version, write, cancellationToken);
        try
        {
            // Fastboot and adb-service calls are synchronous protocol rounds; run the whole dispatch
            // off the terminal input path. fastboot 与 adb 服务的协议调用是同步的；整个分发都放到终端输入路径之外。
            return await Task.Run(async () => command.Tool switch
            {
                WrappedTool.Adb => await RunAdbAsync(command, context).ConfigureAwait(false),
                WrappedTool.Fastboot => RunFastboot(command, context),
                WrappedTool.Hdc => await RunHdcAsync(command, context).ConfigureAwait(false),
                _ => 1,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            context.Error(command.Tool, message);
            return 1;
        }
    }

    /// <summary>Reports whether the dispatcher has a handler for the command, so unknown sub-commands
    /// fall through to the real shell. <para>报告分发器是否有该命令的处理器；未知子命令交还真实 shell。</para></summary>
    /// <param name="command">The parsed command.<para>解析后的命令。</para></param>
    /// <returns><c>true</c> when the wrapped tool handles the command.<para>包装工具可处理该命令时为 <c>true</c>。</para></returns>
    internal bool CanHandle(WrappedCommand command) => command.Tool switch
    {
        WrappedTool.Adb => AdbCommands.Contains(command.Command),
        WrappedTool.Fastboot => FastbootCommands.Contains(command.Command),
        WrappedTool.Hdc => HdcCommands.Contains(command.Command),
        _ => false,
    };

    /// <summary>Shared execution context handed to every handler.<para>交给每个处理器的共享执行上下文。</para></summary>
    internal sealed class Context(IWrappedCommandGateway gateway, string version, Action<string> write,
        CancellationToken cancellationToken)
    {
        /// <summary>Gets the device operations seam.<para>获取设备操作缝隙。</para></summary>
        internal IWrappedCommandGateway Gateway { get; } = gateway;

        /// <summary>Gets the toolbox version text.<para>获取工具箱版本文本。</para></summary>
        internal string Version { get; } = version;

        /// <summary>Gets the cancellation token of the running command.<para>获取运行中命令的取消令牌。</para></summary>
        internal CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>Writes one normalized line.<para>写出一行规范化文本。</para></summary>
        /// <param name="text">Line text without the line ending; empty writes a blank line.
        /// <para>不含行尾的行文本；空串写一个空行。</para></param>
        internal void Line(string text = "") => write(text.Length == 0 ? "\r\n" : Normalize(text) + "\r\n");

        /// <summary>Writes text without a line ending, normalizing bare newlines for the terminal.
        /// <para>写出不带行尾的文本，裸换行会规范化为终端格式。</para></summary>
        /// <param name="text">Text to write.<para>待写文本。</para></param>
        internal void Raw(string text) => write(Normalize(text));

        /// <summary>Writes a tool-prefixed error line.<para>写出带工具前缀的错误行。</para></summary>
        /// <param name="tool">The tool naming the prefix.<para>决定前缀的工具。</para></param>
        /// <param name="message">Error message.<para>错误消息。</para></param>
        internal void Error(WrappedTool tool, string message)
        {
            var prefix = tool switch
            {
                WrappedTool.Adb => "adb: error: ",
                WrappedTool.Fastboot => "fastboot: error: ",
                WrappedTool.Hdc => "Error: ",
                _ => "error: ",
            };
            Line(prefix + message);
        }

        /// <summary>Writes a fastboot status message padded to the official 50 column layout without a
        /// line ending, so the following <see cref="Okay"/> completes the line.
        /// <para>按官方 50 列布局写出 fastboot 状态消息且不带行尾，由后续的 <see cref="Okay"/> 收尾。</para></summary>
        /// <param name="message">Status message, or empty to reset the line anchor.
        /// <para>状态消息；空串用于重置行锚点。</para></param>
        internal void FastbootStatus(string message) =>
            write(FormattableString.Invariant($"{message,-50} "));

        /// <summary>Completes a fastboot status line with the official OKAY suffix.
        /// <para>以官方 OKAY 后缀收尾一条 fastboot 状态行。</para></summary>
        /// <param name="seconds">Elapsed seconds of the phase.<para>该相位的耗时秒数。</para></param>
        internal void Okay(double seconds) =>
            Line(FormattableString.Invariant($"OKAY [{seconds,7:F3}s]"));

        /// <summary>Renders a failed fastboot response the way the official tool does, including the
        /// trailing <c>fastboot: error: Command failed</c> line.
        /// <para>按官方方式渲染失败的 fastboot 响应，包括结尾的 <c>fastboot: error: Command failed</c> 行。</para></summary>
        /// <param name="response">The failed response.<para>失败的响应。</para></param>
        internal void FailFastboot(Comm.Protocols.Fastboot.FastbootResponse response)
        {
            Line($"FAILED ({response.Text})");
            Error(WrappedTool.Fastboot, "Command failed");
        }

        internal static string Normalize(string text) =>
            text.Replace("\r\n", "\n").Replace("\n", "\r\n");

        internal static string FormatSeconds(double seconds) =>
            seconds.ToString("F3", CultureInfo.InvariantCulture);
    }

    // ---- shared device resolution ----

    private (DetectedDevice? Device, string? Error) ResolveAdbTarget(WrappedCommand command,
        bool requireReady, IReadOnlyList<DetectedDevice>? candidates = null)
    {
        candidates ??= gateway.Scan();
        IEnumerable<DetectedDevice> pool = candidates.Where(device => device.Protocol == DeviceProtocolKind.Adb);
        if (command.UseUsb) pool = pool.Where(device => device.Transport == DeviceTransportKind.Usb);
        if (command.UseTcp) pool = pool.Where(device => device.Transport == DeviceTransportKind.Tcp);
        if (command.Target is { } target)
        {
            var match = pool.FirstOrDefault(device => device.Serial == target
                || device.NetworkAlias == target
                || device.Key == $"adb:{target}" || device.Key == $"adb-tcp:{target}");
            if (match is null) return (null, $"device '{target}' not found");
            if (requireReady && !IsAdbReady(match)) return (null, $"device '{target}' offline or unauthorized");
            return (match, null);
        }
        var ready = pool.Where(IsAdbReady).ToArray();
        if (ready.Length == 1) return (ready[0], null);
        if (ready.Length == 0)
        {
            var offline = pool.ToArray();
            return (null, offline.Length == 0 ? "no devices/emulators found" : "device offline or unauthorized");
        }
        return (null, "more than one device/emulator");
    }

    private static bool IsAdbReady(DetectedDevice device) =>
        device.State is "device" or "online" or "recovery" or "sideload" or "bootloader";

    private (DetectedDevice? Device, string? Error) ResolveHdcTarget(WrappedCommand command)
    {
        var pool = gateway.Scan().Where(device => device.Protocol == DeviceProtocolKind.Hdc).ToArray();
        if (command.Target is { } target)
        {
            var match = pool.FirstOrDefault(device => device.Serial == target || device.Key == $"hdc:{target}");
            if (match is null) return (null, $"No such device: {target}");
            if (match.State != "device") return (null, $"Device is not connected: {target}");
            return (match, null);
        }
        var ready = pool.Where(device => device.State == "device").ToArray();
        return ready.Length switch
        {
            1 => (ready[0], null),
            0 => (null, pool.Length == 0 ? "No devices found" : "Device is not connected"),
            _ => (null, "More than one device, use -t to specify the connect key"),
        };
    }

    private (DetectedDevice? Device, string? Error) ResolveFastbootTarget(WrappedCommand command)
    {
        var pool = gateway.Scan().Where(device => device.Protocol == DeviceProtocolKind.Fastboot).ToArray();
        if (command.Target is { } target)
        {
            var match = pool.FirstOrDefault(device => device.Serial == target
                || device.Key == $"fastboot:{target}" || device.Key == $"fastboot-tcp:{target}"
                || device.Key == $"fastboot-udp:{target}");
            if (match is null) return (null, "no such device");
            return (match, null);
        }
        return pool.Length switch
        {
            1 => (pool[0], null),
            0 => (null, "no devices"),
            _ => (null, "more than one device"),
        };
    }

    // ---- adb dispatch ----

    private async Task<int> RunAdbAsync(WrappedCommand command, Context context)
    {
        switch (command.Command)
        {
            case "help": return AdbHelp(context);
            case "version": return AdbVersion(context);
            case "devices": return AdbDevices(command, context);
            case "shell": return await AdbShellAsync(command, context).ConfigureAwait(false);
            case "push": return await AdbPushAsync(command, context).ConfigureAwait(false);
            case "pull": return await AdbPullAsync(command, context).ConfigureAwait(false);
            case "install": return await AdbInstallAsync(command, context).ConfigureAwait(false);
            case "uninstall": return await AdbUninstallAsync(command, context).ConfigureAwait(false);
            case "reboot":
            case "reboot-bootloader":
            case "reboot-recovery":
            case "reboot-fastboot":
            case "reboot-sideload":
                return await AdbRebootAsync(command, context).ConfigureAwait(false);
            case "connect": return await AdbConnectAsync(command, context).ConfigureAwait(false);
            case "disconnect": return await AdbDisconnectAsync(command, context).ConfigureAwait(false);
            case "pair": return await AdbPairAsync(command, context).ConfigureAwait(false);
            case "root":
            case "unroot": return AdbServiceCommand(command, context, command.Command == "root" ? "root:" : "unroot:");
            case "tcpip": return AdbTcpIp(command, context);
            case "usb": return AdbServiceCommand(command, context, "usb:");
            case "get-state": return AdbGetState(command, context);
            case "get-serialno": return AdbGetSerialNo(command, context);
            case "get-devpath":
                context.Line("error: get-devpath is not supported by the wrapped tool");
                return 1;
            case "wait-for-device":
            case "wait-for-recovery":
            case "wait-for-sideload":
            case "wait-for-bootloader":
                return await AdbWaitForAsync(command, context).ConfigureAwait(false);
            case "start-server": return 0;
            case "kill-server":
                await context.Gateway.StopAdbServerAsync(context.CancellationToken).ConfigureAwait(false);
                return 0;
            case "logcat": return await AdbLogcatAsync(command, context).ConfigureAwait(false);
            case "bugreport": return await AdbBugReportAsync(command, context).ConfigureAwait(false);
            case "remount": return AdbServiceCommand(command, context, "remount:");
            case "disable-verity": return AdbServiceCommand(command, context, "disable-verity:");
            case "enable-verity": return AdbServiceCommand(command, context, "enable-verity:");
            case "features": return AdbServiceCommand(command, context, "features:");
            case "forward":
            case "reverse":
            case "mdns":
            case "emu":
            case "jdwp":
            case "reconnect":
            case "install-multiple":
            case "install-multi-package":
            case "backup":
            case "restore":
            case "sync":
            case "keygen":
            case "host-features":
            case "transport-id":
            case "abb":
            case "abb_exec":
            case "track-devices":
            case "track-jdwp":
            case "track-apps":
            case "trace":
            case "device":
                context.Error(WrappedTool.Adb, $"{command.Command} is not supported by the wrapped command line tool");
                return 1;
            default:
                // Unknown sub-commands are not intercepted at all; this path only guards direct calls.
                // 未知子命令根本不会被拦截；此路径仅防御直接调用。
                return 1;
        }
    }

    // ---- fastboot dispatch ----

    private int RunFastboot(WrappedCommand command, Context context)
    {
        switch (command.Command)
        {
            case "help": return FastbootUsage(context);
            case "version": return FastbootVersion(context);
            case "devices": return FastbootDevices(command, context);
            case "getvar": return FastbootGetVar(command, context);
            case "flash": return FastbootFlash(command, context);
            case "erase": return FastbootErase(command, context);
            case "format": return FastbootFormat(command, context);
            case "boot": return FastbootBoot(command, context);
            case "download": return FastbootDownload(command, context);
            case "reboot":
            case "reboot-bootloader":
            case "reboot-recovery":
            case "reboot-fastboot":
            case "reboot-edl":
                return FastbootReboot(command, context);
            case "oem": return FastbootPassthrough(command, context);
            case "flashing": return FastbootPassthrough(command, context);
            case "set_active": return FastbootPassthrough(command, context);
            case "continue":
            case "reset":
                return FastbootPassthrough(command, context);
            case "stage":
            case "get-staged":
            case "fetch":
            case "update":
            case "snapshot-update":
            case "flashall":
                context.Error(WrappedTool.Fastboot, $"{command.Command} is not supported by the wrapped command line tool");
                return 1;
            default:
                return 1;
        }
    }

    // ---- hdc dispatch ----

    private async Task<int> RunHdcAsync(WrappedCommand command, Context context)
    {
        switch (command.Command)
        {
            case "help": return HdcHelp(context);
            case "version": return HdcVersion(context);
            case "checkserver": return HdcCheckServer(context);
            case "list": return HdcListTargets(command, context);
            case "shell": return await HdcShellAsync(command, context).ConfigureAwait(false);
            case "file": return await HdcFileAsync(command, context).ConfigureAwait(false);
            case "install": return await HdcInstallAsync(command, context).ConfigureAwait(false);
            case "uninstall": return await HdcUninstallAsync(command, context).ConfigureAwait(false);
            case "target": return await HdcTargetAsync(command, context).ConfigureAwait(false);
            case "tconn": return await HdcTconnAsync(command, context).ConfigureAwait(false);
            case "tdis": return await HdcTdisAsync(command, context).ConfigureAwait(false);
            case "wait": return await HdcWaitAsync(context).ConfigureAwait(false);
            case "kill":
                await context.Gateway.StopHdcServerAsync(context.CancellationToken).ConfigureAwait(false);
                return 0;
            case "start": return 0;
            case "tmode":
            case "fport":
            case "rport":
            case "hilog":
            case "bugreport":
            case "jpid":
            case "track-jpid":
            case "keygen":
            case "reconnect":
            case "smode":
            case "discover":
            case "any":
            case "checkdevice":
                context.Error(WrappedTool.Hdc, $"{command.Command} is not supported by the wrapped command line tool");
                return 1;
            default:
                return 1;
        }
    }
}