using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
    private static bool TryHdc(DeviceInfo device)
    {
        return App.DeviceMonitor.HdcShellAsync(device.Device, "echo __UOTAN_HDC_READY__")
            .GetAwaiter().GetResult().Trim() == "__UOTAN_HDC_READY__";
    }

    private async Task EnrichHdcInfoAsync(DeviceInfo device)
    {
        var monitor = App.DeviceMonitor;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;

        // One direct session for the whole detail pass. Each additional direct session costs a USB interface
        // open, a stale-block drain and a fresh daemon handshake, and measured on a real target that came to
        // roughly 260 ms per command against about 80 ms when the command reused an open session.
        HdcSessionHandle? lease = null;
        HdcSession? session = null;
        if (!DeviceMonitorService.UseHdcServerFor(device.Device))
        {
            var openedLease = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var opened = monitor.OpenHdcLease(device.Device);
                if (opened is { } value)
                {
                    lease = value.Lease;
                    session = value.Session;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An interface another host owns is a property of the target, not of the refresh: record it once
                // here instead of failing the whole device list and repeating the same failed open per command.
                _log.Write(LogLevel.Warn, "DeviceManager",
                    $"HDC direct session for {device.Serial} could not be opened: {ex.GetType().Name}: {ex.Message}");
            }

            openedLease.Stop();
            if (session is null)
            {
                _log.Write(LogLevel.Debug, "DeviceManager",
                    $"HDC detail for {device.Serial} was skipped after {openedLease.ElapsedMilliseconds} ms without a direct session.");
                return;
            }

            _log.Write(LogLevel.Debug, "DeviceManager",
                $"HDC direct session for {device.Serial} opened in {openedLease.ElapsedMilliseconds} ms.");
        }

        var detailWatch = System.Diagnostics.Stopwatch.StartNew();
        var queryCount = 0;
        var cacheEndpoint = device.Device.Endpoint;
        var cached = cacheEndpoint is null ? null : monitor.CachedDeviceProperties(cacheEndpoint);
        var fresh = new Dictionary<string, string>(StringComparer.Ordinal);

        async Task<string> Run(string command)
        {
            queryCount++;
            if (session is not null)
            {
                return await HdcDeviceService.ShellOnSessionAsync(session, command, token);
            }

            return await monitor.HdcShellAsync(device.Device, command, token);
        }

        try
        {
            // Stable parameters are cached per physical device; a cached key skips its command entirely and
            // its value fills the device entry directly.
            async Task Parameter(string name, string cacheKey, Action<string> apply)
            {
                if (cached is not null && cached.TryGetValue(cacheKey, out var cachedValue))
                {
                    apply(cachedValue);
                    return;
                }

                string value;
                try
                {
                    value = (await Run("param get " + HdcDeviceServices.QuoteShellArgument(name)).ConfigureAwait(true)).Trim();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Write(LogLevel.Warn, "DeviceManager",
                        $"HDC parameter '{name}' for {device.Serial} failed: {ex.GetType().Name}: {ex.Message}");
                    value = "--";
                }

                if (value != "--")
                {
                    fresh[cacheKey] = value;
                }

                apply(value);
            }

            // One unusable command must not blank the rest of the pass; the warning names it so a target
            // whose hidumper or df is missing is distinguishable in the log.
            async Task Optional(string command, Action<string> apply)
            {
                string text;
                try
                {
                    text = await Run(command).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Write(LogLevel.Warn, "DeviceManager",
                        $"HDC detail command '{command}' for {device.Serial} failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                apply(text);
            }

            await Parameter("const.product.brand", "hdc:brand", value => device.Brand = value);
            await Parameter("const.product.model", "hdc:model", value => device.Model = value);
            await Parameter("const.product.name", "hdc:codename", value => device.CodeName = value);
            await Parameter("const.ohos.fullname", "hdc:android", value => device.AndroidVersion = value);
            await Parameter("const.ohos.apiversion", "hdc:sdk", value => device.SdkVersion = value);
            await Parameter("const.product.cpu.abilist", "hdc:cpuabi", value => device.CpuAbi = value);
            await Parameter("const.product.software.version", "hdc:compile", value => device.CompileVersion = value);
            if (cached is not null && cached.TryGetValue("hdc:kernel", out var kernel))
            {
                device.Kernel = kernel;
            }
            else
            {
                await Optional("uname -r", text =>
                {
                    device.Kernel = text.Trim();
                    fresh["hdc:kernel"] = device.Kernel;
                });
            }
            if (cached is not null
                && cached.TryGetValue("hdc:cpucode", out var cpuCode)
                && cached.TryGetValue("hdc:boardid", out var boardId)
                && cached.TryGetValue("hdc:bl", out var blStatus)
                && cached.TryGetValue("hdc:slot", out var slot))
            {
                device.CpuCode = cpuCode;
                device.BoardId = boardId;
                device.BlStatus = blStatus;
                device.Slot = slot;
            }
            else
            {
                await Optional("hidumper -c base", text =>
                {
                    var details = DevicePropertyReader.ParseHarmonyBootDetails(text, _localizer.GetString);
                    device.CpuCode = details.SocPlatform;
                    device.BoardId = details.BoardId;
                    device.BlStatus = details.LockState;
                    device.Slot = details.Slot;
                    if (details.SocPlatform != "--")
                    {
                        fresh["hdc:cpucode"] = details.SocPlatform;
                    }

                    if (details.BoardId != "--")
                    {
                        fresh["hdc:boardid"] = details.BoardId;
                    }

                    if (details.LockState != "--")
                    {
                        fresh["hdc:bl"] = details.LockState;
                    }

                    if (details.Slot != "--")
                    {
                        fresh["hdc:slot"] = details.Slot;
                    }
                });
            }

            await Optional("hidumper -s DisplayManagerService -a '-a'", text =>
            {
                device.Resolution = DevicePropertyReader.ParseHarmonyResolution(text);
                device.Density = DevicePropertyReader.ParseHarmonyDensity(text);
            });
            await Optional("hidumper -s BatteryService -a '-i'", text =>
            {
                var (level, description) = DevicePropertyReader.ParseHarmonyBattery(text);
                device.BatteryLevel = level;
                device.BatteryText = description;
            });
            await Optional("uptime", text =>
            {
                var powerOn = DevicePropertyReader.FormatUptime(text, _localizer.GetString);
                if (powerOn == "--")
                {
                    _log.Write(LogLevel.Warn, "DeviceManager",
                        $"HDC uptime for {device.Serial} could not be parsed: '{text.Trim()}'");
                }

                device.PowerOnTime = powerOn;
            });
            await Optional("cat /proc/meminfo", text =>
            {
                var (level, description) = DevicePropertyReader.ParseMem(text);
                device.MemLevel = level;
                device.MemText = description;
            });
            await Optional("df -k /data 2>/dev/null || df -k /storage/emulated 2>/dev/null || df -k /sdcard 2>/dev/null", text =>
            {
                var (level, description) = DevicePropertyReader.ParseDisk(text);
                device.DiskLevel = level;
                device.DiskText = description;
            });
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Debug, "DeviceManager", $"HDC details unavailable: {ex.Message}");
        }
        finally
        {
            if (cacheEndpoint is not null && fresh.Count > 0)
            {
                monitor.CacheDeviceProperties(cacheEndpoint, fresh);
            }

            detailWatch.Stop();
            _log.Write(LogLevel.Info, "DeviceManager",
                $"HDC detail for {device.Serial}: {queryCount} query(ies) in {detailWatch.ElapsedMilliseconds} ms "
                + $"over {(session is null ? "the server route" : "one shared direct session")}.");
            lease?.Dispose();
        }
    }

    private bool CanHdcOperation() => SelectedDevice?.IsHdc == true;

    [RelayCommand(CanExecute = nameof(CanHdcOperation), IncludeCancelCommand = true)]
    private async Task HdcOperationAsync(string operation, CancellationToken cancellationToken)
    {
        var device = SelectedDevice;
        var owner = DialogService.GetOwnerWindow();
        if (device?.IsHdc != true || owner is null) return;
        var language = _localizer;
        var title = language.GetString("Device_Hdc" + operation);
        var monitor = App.DeviceMonitor;
        try
        {
            string result;
            if (operation is "Send" or "Install")
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = operation == "Install"
                        ? [new FilePickerFileType("HarmonyOS") { Patterns = ["*.hap", "*.hsp", "*.app"] }]
                        : [FilePickerFileTypes.All]
                });
                var path = files.FirstOrDefault()?.TryGetLocalPath();
                if (path is null) return;
                var remote = operation == "Send"
                    ? await DialogService.InputAsync(owner, language.GetString("Device_HdcRemotePath"), "/data/local/tmp/" + Path.GetFileName(path))
                    : null;
                if (operation == "Send" && remote is null) return;
                if (!await DialogService.ConfirmAsync(owner, title, $"{device.Serial}\n{path}\n{remote}")) return;
                result = operation == "Install" ? await monitor.HdcInstallAsync(device.Device, path, cancellationToken)
                    : await monitor.HdcSendFileAsync(device.Device, path, remote!, cancellationToken);
            }
            else if (operation == "Receive")
            {
                var remote = await DialogService.InputAsync(owner, language.GetString("Device_HdcRemotePath"));
                if (remote is null) return;
                var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = title, SuggestedFileName = Path.GetFileName(remote) });
                var path = file?.TryGetLocalPath();
                if (path is null) return;
                result = await monitor.HdcReceiveFileAsync(device.Device, remote, path, cancellationToken);
            }
            else if (operation == "Uninstall")
            {
                var bundle = await DialogService.InputAsync(owner, language.GetString("Device_HdcBundleName"));
                if (bundle is null) return;
                if (!await DialogService.ConfirmAsync(owner, title, $"{device.Serial}\n{bundle}")) return;
                result = await monitor.HdcUninstallAsync(device.Device, bundle, cancellationToken);
            }
            else throw new NotSupportedException(operation);
            StatusText = $"{title}: {result.Trim()}";
            _log.Write(LogLevel.Info, "DeviceManager", StatusText);
        }
        catch (OperationCanceledException) { StatusText = language.GetString("Device_HdcCancelled"); }
        catch (Exception ex)
        {
            StatusText = $"{title}: {(UserError.Describe(ex))}";
            _log.Write(LogLevel.Error, "DeviceManager", StatusText);
            await DialogService.ShowAsync(owner, title, UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }
}