using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>一键刷写向导视图模型：镜像列表来自 <see cref="ImageViewModel"/> 单例（仅含可刷写格式），
/// 目标设备取自 <see cref="DeviceViewModel"/> 单例的当前选中设备，并按设备协议执行模式白名单校验
/// 后经 <see cref="FlashService.FlashImageAsync"/> 刷写，进度回报 <see cref="StatusText"/> 并转发
/// <see cref="FlashProgressMessage"/>；刷写前经 <see cref="FlashWizardConfirmRequestMessage"/> 请求确认，
/// 刷写完成后可选重启。</para>
/// One-click flash wizard view model: the partition image list comes from the
/// <see cref="ImageViewModel"/> singleton (flashable formats only), the target device is the
/// currently selected device of the <see cref="DeviceViewModel"/> singleton, and after a
/// protocol-based whitelist check the image is flashed through
/// <see cref="FlashService.FlashImageAsync"/>, reporting progress to <see cref="StatusText"/>
/// and forwarding <see cref="FlashProgressMessage"/>. Confirmation is requested through
/// <see cref="FlashWizardConfirmRequestMessage"/> before flashing, with an optional reboot
/// after a successful flash.
/// </summary>
public partial class FlashWizardViewModel : ViewModelBase, IDisposable
{
    /// <summary>本向导可刷写的镜像格式（与镜像面板“全选刷入”保持一致）。</summary>
    private static readonly string[] FlashableFormats = ["boot", "raw", "sparse"];

    private readonly ImageViewModel? _imageVm;
    private readonly DeviceViewModel? _deviceVm;
    private TaskCompletionSource<bool>? _pendingConfirm;

    /// <summary>
    /// <para>获取可用分区镜像列表（来自 <see cref="ImageViewModel"/> 单例，仅含可刷写格式）。
    /// 镜像路径为相对工程根目录，条目含大小与格式。</para>
    /// Gets the available partition image list (from the <see cref="ImageViewModel"/> singleton,
    /// flashable formats only). Paths are relative to the project root; each entry carries its
    /// size and format.
    /// </summary>
    public ObservableCollection<ImageEntryViewModel> Images { get; } = [];

    /// <summary>
    /// <para>获取或设置向导中当前选中的分区镜像。</para>
    /// Gets or sets the partition image currently selected in the wizard.
    /// </summary>
    [ObservableProperty]
    private ImageEntryViewModel? _selectedImage;

    /// <summary>
    /// <para>获取或设置目标设备（来自 <see cref="DeviceViewModel"/> 当前选中设备）。</para>
    /// Gets or sets the target device (the <see cref="DeviceViewModel"/> selected device).
    /// </summary>
    [ObservableProperty]
    private DeviceInfo? _targetDevice;

    /// <summary>
    /// <para>获取或设置向导状态文本，回报进度与结果。</para>
    /// Gets or sets the wizard status text, reporting progress and results.
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// <para>获取或设置刷写进度百分比（0-100）。</para>
    /// Gets or sets the flash progress percent (0-100).
    /// </summary>
    [ObservableProperty]
    private int _progressPercent;

    /// <summary>
    /// <para>获取或设置是否正忙（刷写/重启进行中，禁用相关命令）。</para>
    /// Gets or sets whether the wizard is busy (a flash/reboot is running; related commands disabled).
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// <para>获取或设置是否在刷写成功后自动重启设备。</para>
    /// Gets or sets whether to reboot the device automatically after a successful flash.
    /// </summary>
    [ObservableProperty]
    private bool _rebootAfterFlash;

    /// <summary>
    /// <para>创建刷写向导视图模型并同步镜像列表与当前设备，同时订阅镜像/设备变化。</para>
    /// Creates the flash wizard view model, syncing the image list and the current device, and
    /// subscribes to image/device changes so the wizard stays in sync while open.
    /// </summary>
    public FlashWizardViewModel()
    {
        _imageVm = ImageViewModel.Instance;
        _deviceVm = DeviceViewModel.EnsureInstance();

        if (_imageVm is not null)
        {
            _imageVm.Images.CollectionChanged += OnImagesChanged;
            RefreshImages();
            SelectedImage ??= _imageVm.SelectedImage ?? Images.FirstOrDefault();
        }

        if (_deviceVm is not null)
        {
            _deviceVm.PropertyChanged += OnDeviceVmPropertyChanged;
        }

        DeviceMonitorService.Instance.DevicesChanged += OnDevicesChanged;
        TargetDevice = _deviceVm?.SelectedDevice;
    }

    /// <summary>
    /// <para>获取从选中镜像文件名推导的目标分区名（去扩展名）；无选中镜像时为空字符串。</para>
    /// Gets the target partition name derived from the selected image file name (extension
    /// stripped); empty when no image is selected.
    /// </summary>
    public string PartitionName => SelectedImage is null
        ? string.Empty
        : Path.GetFileNameWithoutExtension(SelectedImage.RelativePath);

    /// <summary>是否已选中镜像。 Whether an image is selected.</summary>
    public bool HasSelectedImage => SelectedImage is not null;

    /// <summary>目标设备显示文本。 Target device display text.</summary>
    public string DeviceDisplay => TargetDevice?.DisplayName
        ?? LanguageService.Instance.GetString("FlashWizard_NoDevice");

    /// <summary>目标设备详情文本（序列号/模式/状态/传输）。 Target device detail text.</summary>
    public string DeviceDetail => TargetDevice is null
        ? LanguageService.Instance.GetString("FlashWizard_NoDevice")
        : $"Serial: {TargetDevice.Serial}\nMode: {TargetDevice.Mode}\nState: {TargetDevice.State}\nTransport: {TargetDevice.Transport}";

    /// <summary>设备是否就绪（存在且状态不是 offline）。 Whether the device is ready (present and not offline).</summary>
    public bool IsDeviceReady => TargetDevice is not null
        && !string.Equals(TargetDevice.State, "offline", StringComparison.OrdinalIgnoreCase);

    /// <summary>目标设备是否为 Fastboot 模式。 Whether the target device is in Fastboot mode.</summary>
    public bool IsFastbootMode => TargetDevice?.Protocol == DeviceProtocolKind.Fastboot;

    /// <summary>目标设备是否为 EDL 模式。 Whether the target device is in EDL mode.</summary>
    public bool IsEdlMode => TargetDevice?.Protocol == DeviceProtocolKind.Edl;

    /// <summary>目标设备是否为 RKDL 模式。 Whether the target device is in RKDL mode.</summary>
    public bool IsRkdlMode => TargetDevice?.Protocol == DeviceProtocolKind.Rkdl;

    /// <summary>目标设备是否为 ADB 模式。 Whether the target device is in ADB mode.</summary>
    public bool IsAdbMode => TargetDevice?.Protocol == DeviceProtocolKind.Adb;

    /// <summary>
    /// <para>获取当前设备协议对应的模式白名单规则说明，展示在向导界面。</para>
    /// Gets the mode-whitelist rule text for the current device protocol, shown in the wizard UI.
    /// </summary>
    public string RuleText => TargetDevice?.Protocol switch
    {
        DeviceProtocolKind.Fastboot => LanguageService.Instance.GetString("FlashWizard_RuleFastboot"),
        DeviceProtocolKind.Edl => LanguageService.Instance.GetString("FlashWizard_RuleEdl"),
        DeviceProtocolKind.Rkdl => LanguageService.Instance.GetString("FlashWizard_RuleRkdl"),
        DeviceProtocolKind.Adb => LanguageService.Instance.GetString("FlashWizard_RuleAdbNotSupported"),
        _ => LanguageService.Instance.GetString("FlashWizard_NoDevice"),
    };

    /// <summary>
    /// <para>是否可刷写：未忙、已选镜像且分区名非空、目标设备就绪，且协议为
    /// Fastboot/EDL/RKDL（ADB 仅支持 sideload，不在本向导范围，直接禁用）。</para>
    /// Whether flashing is allowed: not busy, an image with a non-empty partition name is
    /// selected, the target device is ready, and the protocol is Fastboot/EDL/RKDL (ADB supports
    /// sideload only and is out of this wizard's scope, so it stays disabled).
    /// </summary>
    public bool CanFlash => !IsBusy
        && SelectedImage is not null
        && !string.IsNullOrWhiteSpace(PartitionName)
        && TargetDevice is not null
        && IsDeviceReady
        && TargetDevice.Protocol is DeviceProtocolKind.Fastboot or DeviceProtocolKind.Edl or DeviceProtocolKind.Rkdl;

    /// <summary>是否可重启：未忙且存在目标设备。 Whether reboot is allowed: not busy and a target device exists.</summary>
    public bool CanReboot => !IsBusy && TargetDevice is not null;

    /// <summary>
    /// <para>一键刷写：白名单校验 → 请求确认 → <see cref="FlashService.FlashImageAsync"/> 刷写，
    /// 进度回报状态文本并转发 <see cref="FlashProgressMessage"/>；成功后按需重启。</para>
    /// One-click flash: whitelist check → confirmation request → flash via
    /// <see cref="FlashService.FlashImageAsync"/>, reporting progress to the status text and
    /// forwarding <see cref="FlashProgressMessage"/>; reboots on demand after success.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashAsync()
    {
        var image = SelectedImage;
        var device = TargetDevice;
        if (image is null || device is null)
        {
            return;
        }

        var partition = PartitionName;
        if (string.IsNullOrWhiteSpace(partition))
        {
            StatusText = LanguageService.Instance.GetString("FlashWizard_InvalidPartition");
            return;
        }

        var imagePath = ProjectContextService.Instance.ResolvePath(image.RelativePath);
        if (imagePath is null || !File.Exists(imagePath))
        {
            StatusText = LanguageService.Instance.GetString("FlashWizard_ImageNotFound");
            return;
        }

        // 模式白名单校验（Fastboot 任意分区；EDL/RKDL 需设备就绪并做分区预期校验）。
        var validationError = ValidateWhitelist(device, partition, imagePath);
        if (validationError is not null)
        {
            StatusText = validationError;
            LogService.Instance.Write(LogLevel.Warn, "FlashWizard", validationError);
            return;
        }

        // 刷写前请求确认（由向导窗口弹出对话框并回写结果）。
        var tcs = new TaskCompletionSource<bool>();
        _pendingConfirm = tcs;
        try
        {
            WeakReferenceMessenger.Default.Send(new FlashWizardConfirmRequestMessage(
                LanguageService.Instance.GetString("FlashWizard_ConfirmTitle"),
                string.Format(LanguageService.Instance.GetString("FlashWizard_ConfirmMessage"), partition, device.Serial, device.Mode),
                tcs));
            if (!await tcs.Task.ConfigureAwait(true))
            {
                StatusText = LanguageService.Instance.GetString("FlashWizard_Canceled");
                return;
            }
        }
        finally
        {
            if (ReferenceEquals(_pendingConfirm, tcs))
            {
                _pendingConfirm = null;
            }
        }

        IsBusy = true;
        try
        {
            ProgressPercent = 0;
            StatusText = $"{LanguageService.Instance.GetString("Flash_Flashing")} {partition} -> {device.Serial}...";
            var progress = new Progress<FlashProgress>(fp =>
            {
                var percent = Math.Clamp(fp.Percent, 0, 100);
                ProgressPercent = percent;
                StatusText = $"[{percent}%] {fp.Status}";
                WeakReferenceMessenger.Default.Send(new FlashProgressMessage(fp.Partition, percent, fp.Status));
            });

            var result = await FlashService.Default.FlashImageAsync(device, partition, imagePath, progress, CancellationToken.None).ConfigureAwait(true);
            if (result.Success)
            {
                ProgressPercent = 100;
            }

            StatusText = result.Success
                ? $"{LanguageService.Instance.GetString("Flash_Done")} {partition} -> {device.Serial}"
                : $"{LanguageService.Instance.GetString("Flash_Failed")} {partition}: {result.Summary}";
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "FlashWizard",
                $"Flash {partition} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");

            if (result.Success && RebootAfterFlash)
            {
                await RebootCoreAsync(device).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Flash_Failed")} {partition}: {ex.Message}";
            LogService.Instance.Write(LogLevel.Error, "FlashWizard",
                $"Flash {partition} to {device.Serial} failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>重启目标设备到系统模式。</para>
    /// Reboots the target device into system mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReboot))]
    private async Task RebootDeviceAsync()
    {
        var device = TargetDevice;
        if (device is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RebootCoreAsync(device).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>执行重启并把结果回报到状态文本与统一日志。</para>
    /// Performs the reboot and reports the result to the status text and the unified log.
    /// </summary>
    private async Task RebootCoreAsync(DeviceInfo device)
    {
        StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> system...";
        try
        {
            var ok = await DeviceMonitorService.Instance.RebootAsync(device.Device, "system").ConfigureAwait(true);
            StatusText = ok
                ? $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> system OK"
                : $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> system FAIL";
            LogService.Instance.Write(ok ? LogLevel.Info : LogLevel.Error, "FlashWizard",
                $"Reboot {device.Serial} -> system: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> system FAIL";
            LogService.Instance.Write(LogLevel.Error, "FlashWizard",
                $"Reboot {device.Serial} -> system failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>按设备协议执行模式白名单校验：Fastboot 可刷任意分区（分区名非空即可）；
    /// EDL 需设备就绪且分区名匹配镜像目录 rawprogram 预期（rawprogram 缺失或解析失败时回退为
    /// “分区名非空+设备在线”基础校验）；RKDL 需设备就绪且分区名非空；ADB 仅支持 sideload 类操作，
    /// 不在本向导范围，返回提示。</para>
    /// Runs the protocol-based whitelist check: Fastboot accepts any partition (non-empty name);
    /// EDL requires a ready device and the partition matched against the rawprogram in the image
    /// directory (falling back to the basic "non-empty partition + online device" check when the
    /// rawprogram is missing or cannot be parsed); RKDL requires a ready device and a non-empty
    /// partition; ADB supports sideload only and is out of this wizard's scope (returns a hint).
    /// </summary>
    /// <returns>校验失败时的错误提示；通过时返回 <c>null</c>。 The error hint when validation fails; <c>null</c> when it passes.</returns>
    private static string? ValidateWhitelist(DeviceInfo device, string partition, string imagePath)
    {
        if (device.Protocol is DeviceProtocolKind.Fastboot or DeviceProtocolKind.Rkdl)
        {
            return null;
        }

        if (device.Protocol == DeviceProtocolKind.Edl)
        {
            var imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? string.Empty;
            var rawProgramPath = Directory.EnumerateFiles(imageDir, "rawprogram*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (rawProgramPath is not null)
            {
                try
                {
                    var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
                    if (FlashService.FindEdlTarget(entries, partition, imagePath) is null)
                    {
                        return string.Format(LanguageService.Instance.GetString("FlashWizard_EdlPartitionNotFound"), partition);
                    }
                }
                catch (Exception ex)
                {
                    // rawprogram 解析失败：回退到基础校验（分区名非空 + 设备在线）。
                    // Parsing failed: fall back to the basic check (non-empty partition + online device).
                    LogService.Instance.Write(LogLevel.Warn, "FlashWizard",
                        $"rawprogram parse failed, falling back to the basic EDL check: {ex.Message}");
                }
            }

            return null;
        }

        return LanguageService.Instance.GetString("FlashWizard_AdbNotSupported");
    }

    partial void OnSelectedImageChanged(ImageEntryViewModel? value)
    {
        OnPropertyChanged(nameof(PartitionName));
        OnPropertyChanged(nameof(HasSelectedImage));
        FlashCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetDeviceChanged(DeviceInfo? value)
    {
        OnPropertyChanged(nameof(DeviceDisplay));
        OnPropertyChanged(nameof(DeviceDetail));
        OnPropertyChanged(nameof(IsDeviceReady));
        OnPropertyChanged(nameof(IsFastbootMode));
        OnPropertyChanged(nameof(IsEdlMode));
        OnPropertyChanged(nameof(IsRkdlMode));
        OnPropertyChanged(nameof(IsAdbMode));
        OnPropertyChanged(nameof(RuleText));
        FlashCommand.NotifyCanExecuteChanged();
        RebootDeviceCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        FlashCommand.NotifyCanExecuteChanged();
        RebootDeviceCommand.NotifyCanExecuteChanged();
    }

    private void OnImagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshImages();

    private void RefreshImages()
    {
        Images.Clear();
        if (_imageVm is null)
        {
            return;
        }

        foreach (var image in _imageVm.Images)
        {
            if (FlashableFormats.Contains(image.Format))
            {
                Images.Add(image);
            }
        }

        if (SelectedImage is null && Images.Count > 0)
        {
            SelectedImage = Images[0];
        }
    }

    private void OnDeviceVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.SelectedDevice) or nameof(DeviceViewModel.Devices))
        {
            SyncDevice();
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(SyncDevice);

    private void SyncDevice()
    {
        // 刷写/重启进行中不切换目标设备，避免刷到一半换机。
        // Do not swap the target device while a flash/reboot is running.
        if (IsBusy)
        {
            return;
        }

        TargetDevice = _deviceVm?.SelectedDevice;
    }

    /// <summary>
    /// <para>取消订阅镜像/设备变化；若仍处于确认等待，则把结果置为 false 使刷写流程安全退出。</para>
    /// Unsubscribes from image/device changes; if a confirmation is still pending, resolves it to
    /// false so the flash flow exits safely.
    /// </summary>
    public void Dispose()
    {
        if (_imageVm is not null)
        {
            _imageVm.Images.CollectionChanged -= OnImagesChanged;
        }

        if (_deviceVm is not null)
        {
            _deviceVm.PropertyChanged -= OnDeviceVmPropertyChanged;
        }

        DeviceMonitorService.Instance.DevicesChanged -= OnDevicesChanged;
        _pendingConfirm?.TrySetResult(false);
    }
}