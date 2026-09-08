using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
    private static bool TryHdc(DeviceInfo device)
    {
        // Do not route HDC devices into ADB or claim a host-only handshake proves device reachability.
        return HdcDeviceService.FromSettings().ShellAsync(device.Device, "echo __UOTAN_HDC_READY__")
            .GetAwaiter().GetResult().Trim() == "__UOTAN_HDC_READY__";
    }

    private async Task EnrichHdcInfoAsync(DeviceInfo device)
    {
        var service = HdcDeviceService.FromSettings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<string> Parameter(string name)
        {
            try { return (await service.GetPropertyAsync(device.Device, name, timeout.Token)).Trim(); }
            catch (Exception ex) when (ex is IOException or TimeoutException) { return "--"; }
        }
        async Task Optional(string command, Action<string> apply)
        {
            try { apply(await service.ShellAsync(device.Device, command, timeout.Token)); }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                LogService.Instance.Write(LogLevel.Debug, "DeviceManager", $"Optional HDC detail unavailable: {ex.Message}");
            }
        }
        try
        {
            device.Brand = await Parameter("const.product.brand");
            device.Model = await Parameter("const.product.model");
            device.CodeName = await Parameter("const.product.name");
            device.AndroidVersion = await Parameter("const.ohos.fullname");
            device.SdkVersion = await Parameter("const.ohos.apiversion");
            device.CpuAbi = await Parameter("const.product.cpu.abilist");
            device.CompileVersion = await Parameter("const.product.software.version");
            await Optional("uname -r", text => device.Kernel = text.Trim());
            await Optional("cat /proc/uptime", text => device.PowerOnTime = FormatUptime(text));
            await Optional("cat /proc/meminfo", text =>
            {
                var (level, description) = ParseMem(text);
                device.MemLevel = level;
                device.MemText = description;
            });
            await Optional("df /data", text =>
            {
                var (level, description) = ParseDisk(text);
                device.DiskLevel = level;
                device.DiskText = description;
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceManager", $"HDC details unavailable: {ex.Message}");
        }
    }

    private bool CanHdcOperation() => SelectedDevice?.IsHdc == true;

    [RelayCommand(CanExecute = nameof(CanHdcOperation), IncludeCancelCommand = true)]
    private async Task HdcOperationAsync(string operation, CancellationToken cancellationToken)
    {
        var device = SelectedDevice;
        var owner = DialogService.GetOwnerWindow();
        if (device?.IsHdc != true || owner is null) return;
        var language = LanguageService.Instance;
        var title = language.GetString("Device_Hdc" + operation);
        var service = HdcDeviceService.FromSettings();
        try
        {
            string result;
            if (operation is "Send" or "Install")
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title, AllowMultiple = false,
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
                result = operation == "Install" ? await service.InstallAsync(device.Device, path, cancellationToken)
                    : await service.SendFileAsync(device.Device, path, remote!, cancellationToken);
            }
            else if (operation == "Receive")
            {
                var remote = await DialogService.InputAsync(owner, language.GetString("Device_HdcRemotePath"));
                if (remote is null) return;
                var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = title, SuggestedFileName = Path.GetFileName(remote) });
                var path = file?.TryGetLocalPath();
                if (path is null) return;
                result = await service.ReceiveFileAsync(device.Device, remote, path, cancellationToken);
            }
            else if (operation == "Uninstall")
            {
                var bundle = await DialogService.InputAsync(owner, language.GetString("Device_HdcBundleName"));
                if (bundle is null) return;
                if (!await DialogService.ConfirmAsync(owner, title, $"{device.Serial}\n{bundle}")) return;
                result = await service.UninstallAsync(device.Device, bundle, cancellationToken);
            }
            else throw new NotSupportedException(operation);
            StatusText = $"{title}: {result.Trim()}";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager", StatusText);
        }
        catch (OperationCanceledException) { StatusText = language.GetString("Device_HdcCancelled"); }
        catch (Exception ex)
        {
            StatusText = $"{title}: {ex.Message}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", StatusText);
            await DialogService.ShowAsync(owner, title, ex.Message, Ursa.Controls.MessageBoxIcon.Error);
        }
    }
}
