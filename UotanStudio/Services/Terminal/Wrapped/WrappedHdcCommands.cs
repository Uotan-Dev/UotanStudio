using System.Diagnostics;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>HDC handlers of the wrapped command runner.<para>包装命令执行器的 hdc 处理器。</para></summary>
internal sealed partial class WrappedCommandRunner
{
    private int HdcHelp(Context context)
    {
        context.Line();
        context.Line("                         OpenHarmony device connector(hdc) ...");
        context.Line();
        context.Line("---------------------------------global commands:----------------------------------");
        context.Line(" -h/help [verbose]                     - Print hdc help, 'verbose' for more other cmds");
        context.Line(" -v/version                            - Print hdc version");
        context.Line(" -l[0-6]                               - Set runtime log level");
        context.Line(" -t connectkey                         - Use device with given connect key");
        context.Line(" checkserver                           - Check client-server version");
        context.Line();
        context.Line("---------------------------------component commands:-------------------------------");
        context.Line("session commands(on server):");
        context.Line(" list targets [-v]                     - List all devices status, -v for detail");
        context.Line(" tconn key [-remove]                   - Connect device via key, TCP use ip:port");
        context.Line("                                         example:192.168.0.100:10178/192.168.0.100");
        context.Line("                                         USB connect automatic, TCP need to connect manually");
        context.Line("                                         If with '-remove', will remove connection");
        context.Line(" start [-r]                            - Start server. If with '-r', will be restart server");
        context.Line(" kill [-r]                             - Kill server. If with '-r', will be restart server");
        context.Line();
        context.Line("service commands(on daemon):");
        context.Line(" target mount                          - Set /system /vendor partition read-write");
        context.Line(" wait                                  - Wait for the device to become available");
        context.Line(" target boot [-bootloader|-recovery]   - Reboot the device or boot into bootloader\\recovery.");
        context.Line(" tmode usb                             - Reboot the device, listening on USB");
        context.Line(" tmode port [port]                     - Reboot the device, listening on TCP port");
        context.Line();
        context.Line("---------------------------------task commands:-------------------------------------");
        context.Line("file commands:");
        context.Line(" file send [option] local remote       - Send file to device");
        context.Line(" file recv [option] remote local       - Recv file from device");
        context.Line("                                         option is -a|-s|-z");
        context.Line("                                         -a: hold target file timestamp");
        context.Line("                                         -sync: just update newer file");
        context.Line("                                         -z: compress transfer");
        context.Line();
        context.Line("app commands:");
        context.Line(" install [-r|-s|-cwd|option] src       - Send package(s) to device and install them");
        context.Line("                                         (.hap .hsp .app)");
        context.Line("                                         -r: replace existing application");
        context.Line(" uninstall [-k|-s|option] package      - Remove application package from device");
        context.Line("                                         -k: keep the data and cache directories");
        context.Line();
        context.Line("debug commands:");
        context.Line(" hilog [-h|parse]                      - Show device log, -h for detail");
        context.Line(" shell [-b bundlename] [COMMAND...]    - Run shell command (interactive shell if no command given)");
        context.Line("                                         -b: run command in specified debug application bundle path");
        context.Line(" bugreport [FILE]                      - Return all information from the device, stored in file if FILE is specified");
        return 0;
    }

    private int HdcVersion(Context context)
    {
        context.Line($"Version: {Banner} {context.Version}");
        return 0;
    }

    private int HdcCheckServer(Context context)
    {
        context.Line($"Client version: {Banner} {context.Version}");
        return 0;
    }

    private int HdcListTargets(WrappedCommand command, Context context)
    {
        var verbose = command.Arguments.Contains("-v");
        if (command.Arguments.FirstOrDefault() != "targets")
        {
            context.Error(WrappedTool.Hdc, "Unknown command, use 'hdc help' for help");
            return 1;
        }
        var targets = context.Gateway.Scan()
            .Where(device => device.Protocol == DeviceProtocolKind.Hdc)
            .OrderBy(device => device.Serial, StringComparer.Ordinal);
        foreach (var device in targets)
        {
            if (!verbose)
            {
                context.Line(device.Serial);
                continue;
            }
            var state = device.State == "device" ? "Connected" : device.State.Length == 0 ? "Disconnected" : device.State;
            context.Line($"exit\t{device.Serial}\t[{state}]\t{device.Key}");
        }
        return 0;
    }

    private async Task<int> HdcShellAsync(WrappedCommand command, Context context)
    {
        var arguments = command.Arguments.ToArray();
        var index = 0;
        while (index < arguments.Length)
        {
            if (arguments[index] == "-b" && index + 1 < arguments.Length) index += 2;
            else if (arguments[index].StartsWith('-')) index++;
            else break;
        }
        var rest = arguments[index..];
        var (device, error) = ResolveHdcTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Hdc, error!);
            return 1;
        }
        if (rest.Length == 0)
        {
            // Interactive shell: hand the session to the in-app device terminal panel.
            // 交互式 shell：把会话交给应用内设备终端面板。
            await context.Gateway.OpenDeviceShellAsync(device).ConfigureAwait(false);
            return 0;
        }
        var output = await context.Gateway.ExecuteHdcShellAsync(device, string.Join(' ', rest), context.CancellationToken)
            .ConfigureAwait(false);
        if (output.Length > 0) context.Raw(output);
        return 0;
    }

    private async Task<int> HdcFileAsync(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length < 3 || command.Arguments[0] is not ("send" or "recv"))
        {
            context.Error(WrappedTool.Hdc, "Unknown command, use 'hdc help' for help");
            return 1;
        }
        var mode = command.Arguments[0];
        var paths = command.Arguments.Skip(1).Where(argument => !argument.StartsWith('-')).ToArray();
        if (paths.Length != 2)
        {
            context.Error(WrappedTool.Hdc, "[E005003] The parameter is missing, correct your input by referring to hdc help");
            return 1;
        }
        var (device, error) = ResolveHdcTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Hdc, error!);
            return 1;
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (mode == "send")
            {
                await context.Gateway.HdcSendFileAsync(device, paths[0], paths[1], context.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await context.Gateway.HdcReceiveFileAsync(device, paths[0], paths[1], context.CancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Hdc, $"FileTransfer failed: {ex.Message}");
            return 1;
        }
        stopwatch.Stop();
        long size;
        if (mode == "send") size = File.Exists(paths[0]) ? new FileInfo(paths[0]).Length : 0;
        else size = File.Exists(paths[1]) ? new FileInfo(paths[1]).Length : 0;
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var rate = seconds > 0 ? size / seconds / 1024 : 0;
        context.Line(FormattableString.Invariant(
            $"FileTransfer finish, Size:{size}, File count = 1, time:{stopwatch.ElapsedMilliseconds}ms rate:{rate:F2}kB/s"));
        return 0;
    }

    private async Task<int> HdcInstallAsync(WrappedCommand command, Context context)
    {
        var replace = command.Arguments.Contains("-r");
        var paths = command.Arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        if (paths.Length != 1)
        {
            context.Error(WrappedTool.Hdc, "Unknown command, use 'hdc help' for help");
            return 1;
        }
        var (device, error) = ResolveHdcTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Hdc, error!);
            return 1;
        }
        if (!File.Exists(paths[0]))
        {
            context.Error(WrappedTool.Hdc, $"Failed to open the package: {paths[0]}");
            return 1;
        }
        try
        {
            var output = await context.Gateway.HdcInstallAsync(device, paths[0], replace, context.CancellationToken)
                .ConfigureAwait(false);
            if (output.Length > 0) context.Line(output);
            else context.Line("install bundle successfully");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Hdc, $"Failed to install the bundle: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> HdcUninstallAsync(WrappedCommand command, Context context)
    {
        var keepData = command.Arguments.Contains("-k");
        var bundles = command.Arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        if (bundles.Length != 1)
        {
            context.Error(WrappedTool.Hdc, "Unknown command, use 'hdc help' for help");
            return 1;
        }
        var (device, error) = ResolveHdcTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Hdc, error!);
            return 1;
        }
        try
        {
            var output = await context.Gateway.HdcUninstallAsync(device, bundles[0], keepData, context.CancellationToken)
                .ConfigureAwait(false);
            if (output.Length > 0) context.Line(output);
            else context.Line("uninstall bundle successfully");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Hdc, $"Failed to uninstall the bundle: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> HdcTargetAsync(WrappedCommand command, Context context)
    {
        if (command.Arguments.FirstOrDefault() != "boot")
        {
            context.Error(WrappedTool.Hdc, "Unknown command, use 'hdc help' for help");
            return 1;
        }
        var mode = command.Arguments.Skip(1).FirstOrDefault() switch
        {
            "-bootloader" => "bootloader",
            "-recovery" => "recovery",
            var other => other,
        };
        var (device, error) = ResolveHdcTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Hdc, error!);
            return 1;
        }
        await context.Gateway.HdcRebootAsync(device, mode, context.CancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HdcTconnAsync(WrappedCommand command, Context context)
    {
        var arguments = command.Arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        if (arguments.Length != 1)
        {
            context.Error(WrappedTool.Hdc, "Unknown command, use 'hdc help' for help");
            return 1;
        }
        var address = arguments[0];
        if (!address.Contains(':'))
        {
            context.Error(WrappedTool.Hdc, "An HDC network address requires an explicit host:port");
            return 1;
        }
        try
        {
            await context.Gateway.ConnectNetworkDeviceAsync(DeviceProtocolKind.Hdc, address, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error(WrappedTool.Hdc, $"Connect failed: {ex.Message}");
            return 1;
        }
        context.Line("Connect OK");
        return 0;
    }

    private async Task<int> HdcTdisAsync(WrappedCommand command, Context context)
    {
        var keys = command.Arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        var targets = context.Gateway.Scan()
            .Where(device => device.Protocol == DeviceProtocolKind.Hdc)
            .Where(device => keys.Length == 0
                ? device.Transport == DeviceTransportKind.Tcp
                : device.Serial == keys[0] || device.Key == $"hdc:{keys[0]}")
            .ToArray();
        if (keys.Length > 0 && targets.Length == 0)
        {
            context.Error(WrappedTool.Hdc, $"No such device: {keys[0]}");
            return 1;
        }
        foreach (var target in targets)
        {
            await context.Gateway.HdcDisconnectAsync(target, context.CancellationToken).ConfigureAwait(false);
        }
        context.Line("Disconnect OK");
        return 0;
    }

    private async Task<int> HdcWaitAsync(Context context)
    {
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var present = context.Gateway.Scan().Any(device => device.Protocol == DeviceProtocolKind.Hdc
                && device.State == "device");
            if (present) return 0;
            await Task.Delay(250, context.CancellationToken).ConfigureAwait(false);
        }
        return 130;
    }
}