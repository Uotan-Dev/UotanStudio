using System.Diagnostics;
using System.Globalization;
using System.Text;

using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>ADB handlers of the wrapped command runner.<para>包装命令执行器的 ADB 处理器。</para></summary>
internal sealed partial class WrappedCommandRunner
{
    private int AdbHelp(Context context)
    {
        context.Line($"Android Debug Bridge version {AdbProtocolVersion}");
        context.Line($"Version {Banner} {context.Version}");
        context.Line("Installed as UotanStudio wrapped command line tool");
        context.Line();
        context.Line("global options:");
        context.Line(" -a                       listen on all network interfaces, not just localhost");
        context.Line(" -d                       use USB device (error if multiple devices connected)");
        context.Line(" -e                       use TCP/IP device (error if multiple TCP/IP devices available)");
        context.Line(" -s SERIAL                use device with given serial (overrides $ANDROID_SERIAL)");
        context.Line(" -t ID                    use device with given transport id");
        context.Line(" -H                       name of adb server host [default=localhost]");
        context.Line(" -P                       port of adb server [default=5037]");
        context.Line(" -L SOCKET                listen on given socket for adb server [default=tcp:localhost:5037]");
        context.Line();
        context.Line("general commands:");
        context.Line(" devices [-l]             list connected devices (-l for long output)");
        context.Line(" help                     show this help message");
        context.Line(" version                  show version num");
        context.Line();
        context.Line("networking:");
        context.Line(" connect HOST[:PORT]      connect to a device via TCP/IP [default port=5555]");
        context.Line(" disconnect [HOST[:PORT]] disconnect from given TCP/IP device [default port=5555], or all");
        context.Line(" pair HOST[:PORT] [PAIRING CODE]");
        context.Line("     pair with a device for secure TCP/IP communication");
        context.Line(" forward --list           list all forward socket connections");
        context.Line(" reverse --list           list all reverse socket connections from device");
        context.Line(" mdns check               check if mdns discovery is available");
        context.Line(" mdns services            list all discovered services");
        context.Line();
        context.Line("file transfer:");
        context.Line(" push [--sync] LOCAL... REMOTE");
        context.Line("     copy local files/directories to device");
        context.Line(" pull [-a] REMOTE... LOCAL");
        context.Line("     copy files/dirs from device");
        context.Line();
        context.Line("shell:");
        context.Line(" shell [-e ESCAPE] [-n] [-Tt] [-x] [COMMAND...]");
        context.Line("     run remote shell command (interactive shell if no command given)");
        context.Line(" emu COMMAND              run emulator console command");
        context.Line();
        context.Line("app installation (see also `adb shell cmd package help`):");
        context.Line(" install [-lrtsdg] [--instant] PACKAGE");
        context.Line("     push a single package to the device and install it");
        context.Line(" uninstall [-k] PACKAGE");
        context.Line("     remove this app package from the device");
        context.Line();
        context.Line("debugging:");
        context.Line(" bugreport [PATH]");
        context.Line("     write bugreport to given PATH [default=bugreport.zip]");
        context.Line(" jdwp                     list pids of processes hosting a JDWP transport");
        context.Line(" logcat                   show device log (logcat --help for more)");
        context.Line();
        context.Line("security:");
        context.Line(" disable-verity           disable dm-verity checking on userdebug builds");
        context.Line(" enable-verity            re-enable dm-verity checking on userdebug builds");
        context.Line();
        context.Line("scripting:");
        context.Line(" wait-for[-TRANSPORT]-STATE...");
        context.Line("     wait for device to be in a given state");
        context.Line("     STATE: device, recovery, rescue, sideload, bootloader, or disconnect");
        context.Line(" get-state                print offline | bootloader | device");
        context.Line(" get-serialno             print <serial-number>");
        context.Line(" get-devpath              print <device-path>");
        context.Line(" remount [-R]");
        context.Line("      remount partitions read-write");
        context.Line(" reboot [bootloader|recovery|sideload|sideload-auto-reboot]");
        context.Line("     reboot the device; defaults to booting system image but");
        context.Line("     supports bootloader and recovery too");
        context.Line(" sideload OTAPACKAGE      sideload the given full OTA package");
        context.Line(" root                     restart adbd with root permissions");
        context.Line(" unroot                   restart adbd without root permissions");
        context.Line(" usb                      restart adbd listening on USB");
        context.Line(" tcpip PORT               restart adbd listening on TCP on PORT");
        context.Line();
        context.Line("internal debugging:");
        context.Line(" start-server             ensure that there is a server running");
        context.Line(" kill-server              kill the server if it is running");
        context.Line(" reconnect                kick connection from host side to force reconnect");
        context.Line();
        context.Line("environment variables:");
        context.Line(" ANDROID_SERIAL           serial to use for device. See -s option");
        return 0;
    }

    private int AdbVersion(Context context)
    {
        context.Line($"Android Debug Bridge version {AdbProtocolVersion}");
        context.Line($"Version {Banner} {context.Version}");
        context.Line("Installed as UotanStudio wrapped command line tool");
        return 0;
    }

    private int AdbDevices(WrappedCommand command, Context context)
    {
        var longOutput = command.Arguments.FirstOrDefault() is "-l" or "--long";
        context.Line("List of devices attached");
        var targets = context.Gateway.Scan()
            .Where(device => device.Protocol == DeviceProtocolKind.Adb)
            .OrderBy(device => device.Serial, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            var device = targets[index];
            if (!longOutput)
            {
                context.Line($"{device.Serial}\t{device.State}");
                continue;
            }
            context.Line($"{device.Serial}\t{device.State} product: model: device: transport_id:{index + 1}");
        }
        return 0;
    }

    private async Task<int> AdbShellAsync(WrappedCommand command, Context context)
    {
        var arguments = ConsumeAdbShellOptions(command.Arguments);
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        if (arguments.Length == 0)
        {
            // Interactive shell: hand the session to the in-app device terminal panel.
            // 交互式 shell：把会话交给应用内设备终端面板。
            await context.Gateway.OpenDeviceShellAsync(device).ConfigureAwait(false);
            return 0;
        }
        var (exitCode, output) = await context.Gateway
            .ExecuteAdbShellAsync(device, QuoteDeviceShell(arguments), context.CancellationToken)
            .ConfigureAwait(false);
        if (output.Length > 0) context.Raw(output);
        return exitCode == 0 ? 0 : exitCode;
    }

    private static string[] ConsumeAdbShellOptions(IReadOnlyList<string> arguments)
    {
        var index = 0;
        while (index < arguments.Count)
        {
            var token = arguments[index];
            if (token == "-e" && index + 1 < arguments.Count) index += 2;
            else if (token is "-n" or "-T" or "-t" or "-tt" or "-x") index++;
            else break;
        }
        return index == 0 ? [.. arguments] : [.. arguments.Skip(index)];
    }

    private static string QuoteDeviceShell(IReadOnlyList<string> arguments)
    {
        // One shell join: arguments with spaces or quotes are wrapped in double quotes and the
        // device-side shell re-parses them, matching what a real adb invocation sends.
        // 一次 shell 拼接：含空格或引号的参数用双引号包裹，由设备侧 shell 重新解析，与真实 adb 调用一致。
        return string.Join(' ', arguments.Select(argument =>
        {
            if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
            return $"\"{argument.Replace("\"", "\\\"")}\"";
        }));
    }

    private async Task<int> AdbPushAsync(WrappedCommand command, Context context)
    {
        string[] paths = [.. command.Arguments.Where(argument => !argument.StartsWith('-'))];
        if (paths.Length < 2)
        {
            context.Error(WrappedTool.Adb, "usage: adb push LOCAL... REMOTE");
            return 1;
        }
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var remote = paths[^1];
        var locals = paths[..^1];
        var treatRemoteAsDirectory = remote.EndsWith('/') || remote.EndsWith('\\') || locals.Length > 1;
        var exitCode = 0;
        foreach (var local in locals)
        {
            if (!File.Exists(local) && !Directory.Exists(local))
            {
                context.Error(WrappedTool.Adb, $"cannot stat '{local}': No such file or directory");
                exitCode = 1;
                continue;
            }
            exitCode = Math.Max(exitCode,
                await PushPathAsync(device, local, remote, treatRemoteAsDirectory, context).ConfigureAwait(false));
        }
        return exitCode;
    }

    private async Task<int> PushPathAsync(DetectedDevice device, string local, string remote,
        bool treatRemoteAsDirectory, Context context)
    {
        if (Directory.Exists(local))
        {
            var exitCode = 0;
            foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(local, file).Replace('\\', '/');
                var destination = $"{remote.TrimEnd('/')}/{relative}";
                exitCode = Math.Max(exitCode, await PushFileAsync(device, file, destination, context).ConfigureAwait(false));
            }
            return exitCode;
        }
        var target = treatRemoteAsDirectory ? $"{remote.TrimEnd('/')}/{Path.GetFileName(local)}" : remote;
        return await PushFileAsync(device, local, target, context).ConfigureAwait(false);
    }

    private async Task<int> PushFileAsync(DetectedDevice device, string local, string remote, Context context)
    {
        var size = new FileInfo(local).Length;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await context.Gateway.PushAdbFileAsync(device, local, remote, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Adb, $"failed to push '{local}': {ex.Message}");
            return 1;
        }
        stopwatch.Stop();
        context.Line($"{local}: 1 file pushed, 0 skipped. {FormatRate(size, stopwatch.Elapsed.TotalSeconds)} " +
            $"({size} bytes in {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s)");
        return 0;
    }

    private async Task<int> AdbPullAsync(WrappedCommand command, Context context)
    {
        string[] paths = [.. command.Arguments.Where(argument => !argument.StartsWith('-'))];
        if (paths.Length < 2)
        {
            context.Error(WrappedTool.Adb, "usage: adb pull REMOTE... LOCAL");
            return 1;
        }
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var local = paths[^1];
        var remotes = paths[..^1];
        var exitCode = 0;
        foreach (var remote in remotes)
        {
            exitCode = Math.Max(exitCode, await PullPathAsync(device, remote, local, context).ConfigureAwait(false));
        }
        return exitCode;
    }

    private async Task<int> PullPathAsync(DetectedDevice device, string remote, string local, Context context)
    {
        var stopwatch = Stopwatch.StartNew();
        var destination = Directory.Exists(local) ? Path.Combine(local, Path.GetFileName(remote.TrimEnd('/'))) : local;
        try
        {
            await context.Gateway.PullAdbFileAsync(device, remote, destination, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Adb, $"failed to pull '{remote}': {ex.Message}");
            return 1;
        }
        stopwatch.Stop();
        var size = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        context.Line($"{remote}: 1 file pulled, 0 skipped. {FormatRate(size, stopwatch.Elapsed.TotalSeconds)} " +
            $"({size} bytes in {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s)");
        return 0;
    }

    private async Task<int> AdbInstallAsync(WrappedCommand command, Context context)
    {
        var options = new List<string>();
        foreach (var argument in command.Arguments)
        {
            switch (argument)
            {
                case "-r": options.Add("-r"); break;
                case "-d": options.Add("-d"); break;
                case "-g": options.Add("-g"); break;
                case "-t": options.Add("-t"); break;
            }
        }
        string[] apks = [.. command.Arguments.Where(argument => !argument.StartsWith('-'))];
        if (apks.Length == 0)
        {
            context.Error(WrappedTool.Adb, "usage: adb install [-lrtsdg] PACKAGE");
            return 1;
        }
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var exitCode = 0;
        foreach (var apk in apks)
        {
            if (!File.Exists(apk))
            {
                context.Error(WrappedTool.Adb, $"failed to install '{apk}': No such file or directory");
                exitCode = 1;
                continue;
            }
            var (success, output) = await context.Gateway.InstallAdbAsync(device, apk, string.Join(' ', options),
                context.CancellationToken).ConfigureAwait(false);
            if (success)
            {
                context.Line("Success");
            }
            else
            {
                context.Error(WrappedTool.Adb, $"failed to install '{apk}': {output.TrimEnd()}");
                exitCode = 1;
            }
        }
        return exitCode;
    }

    private async Task<int> AdbUninstallAsync(WrappedCommand command, Context context)
    {
        var keepData = command.Arguments.Contains("-k");
        string[] packages = [.. command.Arguments.Where(argument => !argument.StartsWith('-'))];
        if (packages.Length != 1)
        {
            context.Error(WrappedTool.Adb, "usage: adb uninstall [-k] PACKAGE");
            return 1;
        }
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var (success, output) = await context.Gateway
            .UninstallAdbAsync(device, packages[0], keepData, context.CancellationToken).ConfigureAwait(false);
        if (success)
        {
            context.Line("Success");
            return 0;
        }
        context.Error(WrappedTool.Adb, $"failed to uninstall '{packages[0]}': {output.TrimEnd()}");
        return 1;
    }

    private async Task<int> AdbRebootAsync(WrappedCommand command, Context context)
    {
        var mode = command.Command == "reboot"
            ? command.Arguments.FirstOrDefault() ?? string.Empty
            : command.Command["reboot-".Length..];
        if (mode is not ("" or "bootloader" or "recovery" or "sideload" or "sideload-auto-reboot"
            or "fastboot" or "fastbootd" or "quiescent" or "userspace" or "edl"))
        {
            context.Error(WrappedTool.Adb, $"invalid Reboot argument: {mode}");
            return 1;
        }
        var (device, error) = ResolveAdbTarget(command, requireReady: false);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var success = await context.Gateway.RebootAsync(device, mode.Length == 0 ? null : mode, context.CancellationToken)
            .ConfigureAwait(false);
        return success ? 0 : 1;
    }

    private async Task<int> AdbConnectAsync(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length != 1)
        {
            context.Error(WrappedTool.Adb, "usage: adb connect HOST[:PORT]");
            return 1;
        }
        var address = NormalizeAdbAddress(command.Arguments[0]);
        var existing = context.Gateway.Scan().Any(device => device.Protocol == DeviceProtocolKind.Adb
            && (device.Serial == address || device.NetworkAlias == address));
        if (existing)
        {
            context.Line($"already connected to {address}");
            return 0;
        }
        try
        {
            await context.Gateway.ConnectNetworkDeviceAsync(DeviceProtocolKind.Adb, address, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Adb, $"cannot connect to {address}: {ex.Message}");
            return 1;
        }
        context.Line($"connected to {address}");
        return 0;
    }

    private async Task<int> AdbDisconnectAsync(WrappedCommand command, Context context)
    {
        var address = command.Arguments.Length > 0 ? NormalizeAdbAddress(command.Arguments[0]) : null;
        await context.Gateway.DisconnectAdbAsync(address, context.CancellationToken).ConfigureAwait(false);
        context.Line(address is null ? "disconnected everything" : $"disconnected {address}");
        return 0;
    }

    private async Task<int> AdbPairAsync(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length is < 1 or > 2)
        {
            context.Error(WrappedTool.Adb, "usage: adb pair HOST[:PORT] [PAIRING CODE]");
            return 1;
        }
        var address = command.Arguments[0];
        var code = command.Arguments.Length == 2 ? command.Arguments[1] : string.Empty;
        try
        {
            var response = await context.Gateway.PairAdbAsync(address, code, context.CancellationToken)
                .ConfigureAwait(false);
            context.Line(response.Length == 0 ? $"Successfully paired to {address}" : response.TrimEnd());
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Adb, $"failed to pair with {address}: {ex.Message}");
            return 1;
        }
    }

    private int AdbServiceCommand(WrappedCommand command, Context context, string service)
    {
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var response = context.Gateway.RunAdbService(device.Serial, service);
        if (response.Length > 0) context.Line(response);
        return 0;
    }

    private int AdbTcpIp(WrappedCommand command, Context context)
    {
        var port = command.Arguments.FirstOrDefault() ?? "5555";
        return AdbServiceCommand(command, context, $"tcpip:{port}");
    }

    private int AdbGetState(WrappedCommand command, Context context)
    {
        var (device, error) = ResolveAdbTarget(command, requireReady: false);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        context.Line(device.State.Length == 0 ? "device" : device.State);
        return 0;
    }

    private int AdbGetSerialNo(WrappedCommand command, Context context)
    {
        var (device, error) = ResolveAdbTarget(command, requireReady: false);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        context.Line(device.Serial);
        return 0;
    }

    private async Task<int> AdbWaitForAsync(WrappedCommand command, Context context)
    {
        var state = command.Command["wait-for".Length..].TrimStart('-');
        if (state.Length == 0) state = "device";
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var (device, _) = ResolveAdbTarget(command, requireReady: false);
            if (device is not null && (device.State == state
                || (state == "device" && IsAdbReady(device))))
            {
                return 0;
            }
            await Task.Delay(250, context.CancellationToken).ConfigureAwait(false);
        }
        return 130;
    }

    private async Task<int> AdbLogcatAsync(WrappedCommand command, Context context)
    {
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var commandLine = command.Arguments.Length == 0 ? "logcat" : $"logcat {QuoteDeviceShell(command.Arguments)}";
        return await context.Gateway.StreamAdbShellAsync(device, commandLine,
            chunk => context.Raw(Encoding.UTF8.GetString(chunk)), context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<int> AdbBugReportAsync(WrappedCommand command, Context context)
    {
        var (device, error) = ResolveAdbTarget(command, requireReady: true);
        if (device is null)
        {
            context.Error(WrappedTool.Adb, error!);
            return 1;
        }
        var path = command.Arguments.FirstOrDefault();
        if (path is null)
        {
            return await context.Gateway.StreamAdbShellAsync(device, "bugreport",
                chunk => context.Raw(Encoding.UTF8.GetString(chunk)), context.CancellationToken)
                .ConfigureAwait(false);
        }
        var target = Directory.Exists(path) ? Path.Combine(path, "bugreport.zip") : path;
        await using var stream = File.Create(target);
        var exitCode = await context.Gateway.StreamAdbShellAsync(device, "bugreport",
            bytes => stream.Write(bytes, 0, bytes.Length), context.CancellationToken).ConfigureAwait(false);
        context.Line($"Created file {target}");
        return exitCode;
    }

    private static string NormalizeAdbAddress(string address) =>
        address.Contains(':') ? address : $"{address}:5555";

    private static string FormatRate(long bytes, double seconds)
    {
        if (seconds <= 0) return "0.0 MB/s";
        var mbps = bytes / seconds / (1024 * 1024);
        return mbps.ToString("F1", CultureInfo.InvariantCulture) + " MB/s";
    }
}