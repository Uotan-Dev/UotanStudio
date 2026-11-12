using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.Services.Ui;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// USB tree-view window: shows the system USB bus topology in the same hierarchical shape as the Windows
/// device manager, with backend selection, an Android filter, node details, text export and auto-refresh
/// on device changes.
/// <para>USB 树状视图窗口：以 Windows 设备管理器同款层级展示系统 USB 总线拓扑，并提供后端选择、
/// Android 过滤、节点详情、文本导出与设备变更自动刷新。</para>
/// </summary>
public partial class UsbTreeWindow : Window
{
    private readonly UsbTreeViewModel _model;

    /// <summary>
    /// Initializes the USB tree window with the host device-monitor service as its topology source.
    /// <para>初始化 USB 树窗口，使用宿主设备监视服务作为拓扑来源。</para>
    /// </summary>
    public UsbTreeWindow()
        : this(ServiceHub.TryGet<UsbTreeViewModel>() ?? new UsbTreeViewModel())
    {
    }

    /// <summary>
    /// Initializes the USB tree window with an injected view model so tests can supply a fake topology
    /// source.
    /// <para>初始化 USB 树窗口并注入视图模型，供测试提供伪造的拓扑来源。</para>
    /// </summary>
    /// <param name="model">
    /// The view model the window consumes.
    /// <para>窗口消费的视图模型。</para>
    /// </param>
    internal UsbTreeWindow(UsbTreeViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = _model.RefreshAsync();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _model.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Writes the visible topology tree, with driver and interface diagnostics, to the clipboard; on
    /// failure the tree stays untouched and the status plus the unified log record the problem.
    /// <para>把当前可见拓扑树连同驱动与接口诊断写入剪贴板；失败时保留树内容并把状态写入统一日志。</para>
    /// </summary>
    private async void OnCopyTextClick(object? sender, RoutedEventArgs e)
    {
        var text = _model.BuildTextExport();
        if (text.Length == 0)
        {
            _model.StatusText = LanguageService.Instance.GetString("UsbTree_NothingToCopy");
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _model.StatusText = LanguageService.Instance.GetString("UsbTree_CopyFailed");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            await clipboard.FlushAsync();
        }
        catch (Exception ex)
        {
            _model.StatusText = LanguageService.Instance.GetString("UsbTree_CopyFailed");
            LogService.Instance.Write(LogLevel.Error, "UsbTreeWindow",
                $"USB topology copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks for the dangerous-operation confirmation and, when confirmed, configures the clicked row's
    /// device access: on Linux through the udev access configuration with its authorization prompt, on
    /// Windows through the self-signed WinUSB package. A restart notice is shown only when the operation
    /// reports that Windows still needs a restart or a replug.
    /// <para>弹出危险操作确认，确认后配置点击行对应的设备访问：Linux 上经带授权提示的 udev 访问配置，
    /// Windows 上经自签名 WinUSB 包；只有操作明确报告仍需重启或重新插拔时才弹出重启提示。</para>
    /// </summary>
    private async void OnConfigureDeviceClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not UsbTreeNodeViewModel node)
        {
            return;
        }

        var language = LanguageService.Instance;
        var detailKey = OperatingSystem.IsWindows()
            ? "UsbTree_ConfigureDetailWinUsb"
            : "UsbTree_ConfigureDetailUdev";
        var confirmed = await DialogService.ConfirmDangerousAsync(this, new DangerousConfirmRequest
        {
            TabTitle = language.GetString("Menu_UsbTreeWindow"),
            Title = language.GetString("UsbTree_ConfigureConfirmTitle"),
            Detail = language.GetString(detailKey, node.DisplayText),
            DeviceIdentifier = node.Node.InstanceId
        });
        if (!confirmed)
        {
            return;
        }

        var outcome = await _model.ConfigureDeviceAsync(node);
        if (outcome == UsbDriverChangeOutcome.RestartRequired && OperatingSystem.IsWindows())
        {
            await ShowRestartRequiredAsync();
        }
    }

    /// <summary>
    /// Asks for the dangerous-operation confirmation and, when confirmed, unconfigures the clicked row's
    /// device access: on Linux by removing the udev access rule and the usb-serial module block, on
    /// Windows by uninstalling the self-signed package and its certificate. A restart notice is shown only
    /// when the device still runs the takeover driver and needs a restart or a replug.
    /// <para>弹出危险操作确认，确认后取消点击行对应的设备访问配置：Linux 上移除 udev 访问规则与
    /// usb-serial 模块屏蔽，Windows 上卸载自签名包与证书；只有设备仍运行接管驱动、需要重启或重新插拔时
    /// 才弹出重启提示。</para>
    /// </summary>
    private async void OnUnconfigureDeviceClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not UsbTreeNodeViewModel node)
        {
            return;
        }

        var language = LanguageService.Instance;
        var detailKey = OperatingSystem.IsWindows()
            ? "UsbTree_UnconfigureDetailWinUsb"
            : "UsbTree_UnconfigureDetailUdev";
        var confirmed = await DialogService.ConfirmDangerousAsync(this, new DangerousConfirmRequest
        {
            TabTitle = language.GetString("Menu_UsbTreeWindow"),
            Title = language.GetString("UsbTree_UnconfigureConfirmTitle"),
            Detail = language.GetString(detailKey, node.DisplayText),
            DeviceIdentifier = node.Node.InstanceId
        });
        if (!confirmed)
        {
            return;
        }

        var outcome = await _model.UnconfigureDeviceAsync(node);
        if (outcome == UsbDriverChangeOutcome.RestartRequired && OperatingSystem.IsWindows())
        {
            await ShowRestartRequiredAsync();
        }
    }

    /// <summary>
    /// Shows the restart notice when a device-configuration change cannot finish without a restart or a
    /// replug; the dialog only informs the user and never restarts the computer on their behalf.
    /// <para>设备配置变更需要重启或重新插拔才能完成时显示提示；弹窗仅作告知，不会代替用户重启计算机。</para>
    /// </summary>
    private Task ShowRestartRequiredAsync()
    {
        var language = LanguageService.Instance;
        return DialogService.ShowAsync(this,
            language.GetString("UsbTree_RestartRequiredTitle"),
            language.GetString("UsbTree_RestartRequiredMessage"),
            Ursa.Controls.MessageBoxIcon.Information);
    }

    /// <summary>
    /// Installs the bundled vendor driver package covering the clicked row's device; the package only
    /// stages into the driver store and installs on driverless devices, so no confirmation is needed.
    /// <para>安装覆盖点击行设备的捆绑厂商驱动包；包只暂存到驱动仓库并在无驱动设备上安装，因此无需确认。</para>
    /// </summary>
    private void OnInstallDriverClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not UsbTreeNodeViewModel node)
        {
            return;
        }

        _ = _model.InstallDriverAsync(node);
    }
}