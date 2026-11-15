using System.Diagnostics;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>Fastboot handlers of the wrapped command runner.<para>包装命令执行器的 fastboot 处理器。</para></summary>
internal sealed partial class WrappedCommandRunner
{
    private int FastbootVersion(Context context)
    {
        context.Line($"fastboot version {Banner} {context.Version}");
        context.Line("Installed as UotanStudio wrapped command line tool");
        return 0;
    }

    private int FastbootUsage(Context context)
    {
        context.Line("usage: fastboot [OPTION...] COMMAND...");
        context.Line();
        context.Line("flashing:");
        context.Line(" flash PARTITION [FILENAME]     flash given partition, using the image from $PWD");
        context.Line(" erase PARTITION                erase the given partition");
        context.Line(" format[:FS_TYPE[:SIZE]] PARTITION");
        context.Line("                                format a flash partition");
        context.Line(" set_active SLOT                sets the active slot");
        context.Line(" boot KERNEL [RAMDISK [SECOND]] download and boot kernel");
        context.Line(" update ZIP                     flash all partitions from an update.zip");
        context.Line();
        context.Line("variables:");
        context.Line(" getvar NAME                    display a bootloader variable");
        context.Line();
        context.Line("misc:");
        context.Line(" devices [-l]                   list all connected devices");
        context.Line(" reboot [bootloader|recovery|fastboot|edl]");
        context.Line("                                reboot the device; defaults to rebooting the system");
        context.Line(" continue                       continue with autoboot");
        context.Line(" download FILE                  download FILE to device");
        context.Line(" oem COMMAND ...                execute OEM specific command");
        context.Line(" flashing COMMAND ...           execute flashing specific command");
        context.Line();
        context.Line("options:");
        context.Line(" -s SERIAL                      specify a device");
        return 1;
    }

    private int FastbootDevices(WrappedCommand command, Context context)
    {
        var targets = context.Gateway.Scan()
            .Where(device => device.Protocol == DeviceProtocolKind.Fastboot)
            .OrderBy(device => device.Serial, StringComparer.Ordinal);
        foreach (var device in targets)
        {
            context.Line($"{device.Serial}\tfastboot");
        }
        return 0;
    }

    private int FastbootGetVar(WrappedCommand command, Context context)
    {
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var key = command.Arguments.FirstOrDefault() ?? "all";
        var stopwatch = Stopwatch.StartNew();
        if (key == "all")
        {
            var variables = context.Gateway.GetFastbootAllVariables(device);
            foreach (var (name, value) in variables)
            {
                context.Line($"(bootloader) {name}: {value}");
            }
        }
        else
        {
            var (value, found) = context.Gateway.GetFastbootVariable(device, key);
            if (!found)
            {
                context.Error(WrappedTool.Fastboot, $"GetVariable {key} FAILED (variable not found)");
                return 1;
            }
            context.Line($"(bootloader) {key}: {value}");
        }
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootFlash(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length < 2)
        {
            context.Error(WrappedTool.Fastboot, "usage: fastboot flash PARTITION [FILENAME]");
            return 1;
        }
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var partition = command.Arguments[0];
        var file = command.Arguments[1];
        if (!File.Exists(file))
        {
            context.Error(WrappedTool.Fastboot, $"could not read image '{file}'");
            return 1;
        }
        var stopwatch = Stopwatch.StartNew();
        using var stream = File.OpenRead(file);
        var length = stream.Length;
        context.FastbootStatus($"Sending '{partition}' ({length / 1024} KB)");
        var phaseStart = stopwatch.Elapsed.TotalSeconds;
        var download = context.Gateway.FastbootDownload(device, stream, length, context.CancellationToken)
            ;
        if (!download.IsSuccess)
        {
            context.FailFastboot(download);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds - phaseStart);
        context.FastbootStatus($"Writing '{partition}'");
        phaseStart = stopwatch.Elapsed.TotalSeconds;
        var flash = context.Gateway.RunFastbootCommand(device, $"flash:{partition}", context.CancellationToken)
            ;
        if (!flash.IsSuccess)
        {
            context.FailFastboot(flash);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds - phaseStart);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootErase(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length != 1)
        {
            context.Error(WrappedTool.Fastboot, "usage: fastboot erase PARTITION");
            return 1;
        }
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var partition = command.Arguments[0];
        var stopwatch = Stopwatch.StartNew();
        context.FastbootStatus($"Erasing '{partition}'");
        var response = context.Gateway.RunFastbootCommand(device, $"erase:{partition}", context.CancellationToken)
            ;
        if (!response.IsSuccess)
        {
            context.FailFastboot(response);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootFormat(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length != 1)
        {
            context.Error(WrappedTool.Fastboot, "usage: fastboot format[:FS_TYPE[:SIZE]] PARTITION");
            return 1;
        }
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var partition = command.Arguments[0];
        var stopwatch = Stopwatch.StartNew();
        context.FastbootStatus($"Formatting '{partition}'");
        var response = context.Gateway.RunFastbootCommand(device, $"format:{partition}", context.CancellationToken)
            ;
        if (!response.IsSuccess)
        {
            context.FailFastboot(response);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootBoot(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length < 1)
        {
            context.Error(WrappedTool.Fastboot, "usage: fastboot boot KERNEL [RAMDISK [SECOND]]");
            return 1;
        }
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var file = command.Arguments[0];
        if (!File.Exists(file))
        {
            context.Error(WrappedTool.Fastboot, $"could not read image '{file}'");
            return 1;
        }
        var stopwatch = Stopwatch.StartNew();
        using var stream = File.OpenRead(file);
        var length = stream.Length;
        context.FastbootStatus("Downloading 'boot.img'");
        var phaseStart = stopwatch.Elapsed.TotalSeconds;
        var download = context.Gateway.FastbootDownload(device, stream, length, context.CancellationToken)
            ;
        if (!download.IsSuccess)
        {
            context.FailFastboot(download);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds - phaseStart);
        context.FastbootStatus("Booting");
        phaseStart = stopwatch.Elapsed.TotalSeconds;
        var boot = context.Gateway.RunFastbootCommand(device, "boot", context.CancellationToken)
            ;
        if (!boot.IsSuccess)
        {
            context.FailFastboot(boot);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds - phaseStart);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootDownload(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length != 1)
        {
            context.Error(WrappedTool.Fastboot, "usage: fastboot download FILE");
            return 1;
        }
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var file = command.Arguments[0];
        if (!File.Exists(file))
        {
            context.Error(WrappedTool.Fastboot, $"could not read image '{file}'");
            return 1;
        }
        var stopwatch = Stopwatch.StartNew();
        using var stream = File.OpenRead(file);
        var length = stream.Length;
        context.FastbootStatus($"Downloading '{Path.GetFileName(file)}'");
        var response = context.Gateway.FastbootDownload(device, stream, length, context.CancellationToken)
            ;
        if (!response.IsSuccess)
        {
            context.FailFastboot(response);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootReboot(WrappedCommand command, Context context)
    {
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var mode = command.Command == "reboot" ? command.Arguments.FirstOrDefault() ?? string.Empty
            : command.Command["reboot-".Length..];
        var target = mode switch
        {
            "" or "bootloader" or "recovery" or "fastboot" or "edl" => mode.Length == 0 ? null : mode,
            _ => null,
        };
        var stopwatch = Stopwatch.StartNew();
        context.FastbootStatus("Rebooting");
        var raw = target is null ? "reboot" : $"reboot:{target}";
        var response = context.Gateway.RunFastbootCommand(device, raw, context.CancellationToken)
            ;
        if (!response.IsSuccess)
        {
            context.FailFastboot(response);
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }

    private int FastbootPassthrough(WrappedCommand command, Context context)
    {
        if (command.Arguments.Length == 0)
        {
            context.Error(WrappedTool.Fastboot, $"usage: fastboot {command.Command} COMMAND...");
            return 1;
        }
        var (device, error) = ResolveFastbootTarget(command);
        if (device is null)
        {
            context.Error(WrappedTool.Fastboot, error!);
            return 1;
        }
        var raw = command.Command == "set_active"
            ? $"set_active:{command.Arguments[0]}"
            : $"{command.Command} {string.Join(' ', command.Arguments)}";
        var stopwatch = Stopwatch.StartNew();
        var phaseStart = stopwatch.Elapsed.TotalSeconds;
        var response = context.Gateway.RunFastbootCommand(device, raw, context.CancellationToken)
            ;
        foreach (var line in response.InfoLines)
        {
            context.Line($"(bootloader) {line}");
        }
        if (!response.IsSuccess)
        {
            stopwatch.Stop();
            context.Line($"FAILED ({response.Text})");
            context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
            return 1;
        }
        context.Okay(stopwatch.Elapsed.TotalSeconds - phaseStart);
        stopwatch.Stop();
        context.Line($"Finished. Total time: {Context.FormatSeconds(stopwatch.Elapsed.TotalSeconds)}s");
        return 0;
    }
}