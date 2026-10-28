using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Mcp;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Mcp;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Settings view model, managing general settings, firmware tool settings, flash settings, and node editor settings.
/// <para>设置视图模型，管理应用程序的通用设置、固件工具设置、烧录设置和节点编辑器设置。</para>
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Shared, in-memory AI settings.
    /// <para>共用的内存 AI 设置。</para>
    /// </summary>
    public AiSettingsViewModel Ai { get; } = new();

    private AppSettings _appSettings;

    private readonly LocalizedOptionSet _usbBackendOptions =
        new("Settings_UsbAuto", "Settings_UsbNative", "Settings_UsbLibusb");

    private readonly LocalizedOptionSet _routeModeOptions =
        new("Settings_RouteModeAuto", "Settings_RouteModeServerOnly", "Settings_RouteModeDirectOnly",
            "Settings_RouteModeStartupFrozen");

    private readonly RenderingPlatform _renderingPlatform;
    private readonly RenderingBackend[] _renderingBackends;
    private readonly RenderingCompositionMode[] _compositionModes;
    private readonly LocalizedOptionSet _renderingBackendOptions;
    private readonly LocalizedOptionSet _compositionModeOptions;

    private string? _activeBackendLabelKey;
    private string? _activeCompositionLabelKey;

    private int _renderingBackendIndex;
    private int _compositionModeIndex;

    /// <summary>
    /// Gets the USB backend choices shown by the device-communication combo box. The entries
    /// re-resolve their display text when the application language changes, so the closed combo box
    /// switches immediately instead of waiting for the settings page to be rebuilt.
    /// <para>获取设备通信下拉框显示的 USB 后端选项。应用语言切换时条目会重新解析显示文本，
    /// 因此关闭状态的下拉框立即切换，而不是等待设置页重建。</para>
    /// </summary>
    /// <value>The localized options in backend order. <para>按后端顺序排列的本地化选项。</para></value>
    public IReadOnlyList<LocalizedOption> UsbBackendOptions => _usbBackendOptions.Options;

    /// <summary>Persisted USB mode; existing sessions retain their backend until restart.
    /// <para>已持久化的 USB 模式；已建立的会话在重启前保持原后端。</para>
    /// </summary>
    public int DeviceUsbBackendIndex
    {
        get => (int)_appSettings.DeviceCommunication.UsbBackend;
        set
        {
            if (value is < 0 or > 2 || DeviceUsbBackendIndex == value)
            {
                return;
            }
            _appSettings.DeviceCommunication = _appSettings.DeviceCommunication with
            {
                UsbBackend = (Comm.Transports.Usb.Abstractions.UsbApiKind)value
            };
            OnPropertyChanged();
            DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// Gets the debug-bridge route choices shown by the route-mode combo box.
    /// <para>获取路由模式下拉框显示的调试桥路由选项。</para>
    /// </summary>
    /// <value>The localized options in mode order. <para>按模式顺序排列的本地化选项。</para></value>
    public IReadOnlyList<LocalizedOption> RouteModeOptions => _routeModeOptions.Options;

    /// <summary>
    /// Gets or sets the persisted debug-bridge route mode. The running process keeps the mode it
    /// started with, so a change is saved and a restart prompt is shown instead of applying it live.
    /// <para>获取或设置持久化的调试桥路由模式。运行中的进程保持启动时使用的模式，因此修改只会保存并弹出
    /// 重启提示，不会立即生效。</para>
    /// </summary>
    public int DeviceRouteModeIndex
    {
        get => (int)_appSettings.DeviceCommunication.RouteMode;
        set
        {
            if (value is < 0 or > 3 || DeviceRouteModeIndex == value)
            {
                return;
            }

            _appSettings.DeviceCommunication = _appSettings.DeviceCommunication with
            {
                RouteMode = (Comm.Routing.DebugBridgeRouteMode)value
            };
            OnPropertyChanged();
            DebouncedSaveSettings();
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowRouteModeRestartPrompt);
        }
    }

    private void ShowRouteModeRestartPrompt()
    {
        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var language = LanguageService.Instance;
        _ = DialogService.ShowAsync(owner,
            language.GetString("Settings_RouteModeRestartTitle"),
            language.GetString("Settings_RouteModeRestartMessage"),
            Ursa.Controls.MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Gets the rendering backends the active platform supports, in display order.
    /// <para>获取当前平台支持的渲染后端，按显示顺序排列。</para>
    /// </summary>
    /// <value>The localized backend options; the first entry is automatic.<para>本地化后端选项；第一项为自动。</para></value>
    public IReadOnlyList<LocalizedOption> RenderingBackendOptions => _renderingBackendOptions.Options;

    /// <summary>
    /// Gets the composition modes the active platform supports, in display order.
    /// <para>获取当前平台支持的合成模式，按显示顺序排列。</para>
    /// </summary>
    /// <value>The localized composition options; empty outside Windows.<para>本地化合成选项；Windows 之外为空。</para></value>
    public IReadOnlyList<LocalizedOption> CompositionModeOptions => _compositionModeOptions.Options;

    /// <summary>
    /// Gets whether the active windowing backend exposes rendering-backend choices; the Wayland backend and
    /// platforms without selectable options keep only the automatic entry.
    /// <para>获取当前窗口后端是否提供渲染后端选项；Wayland 后端与没有可选选项的平台只保留自动项。</para>
    /// </summary>
    /// <value>True when the backend combo box should be shown.<para>应显示后端下拉框时为 true。</para></value>
    public bool IsRenderingBackendConfigurable => !IsWaylandRendering && _renderingBackends.Length > 1;

    /// <summary>
    /// Gets whether the application runs on the native Wayland backend, which has no configurable rendering mode.
    /// <para>获取应用是否运行在原生 Wayland 后端；该后端没有可配置的渲染模式。</para>
    /// </summary>
    /// <value>True when Wayland was detected from the running window.<para>从运行窗口检测到 Wayland 时为 true。</para></value>
    public bool IsWaylandRendering => _renderingPlatform == RenderingPlatform.LinuxWayland;

    /// <summary>
    /// Gets whether the active platform exposes no rendering choices and is not Wayland, so the generic
    /// unsupported hint applies.
    /// <para>获取当前平台是否既没有渲染选项也不是 Wayland，从而显示通用的不支持提示。</para>
    /// </summary>
    /// <value>True when no configurable backend exists outside Wayland.<para>Wayland 之外没有任何可配置后端时为 true。</para></value>
    public bool IsRenderingPlatformUnsupported => !IsWaylandRendering && _renderingBackends.Length <= 1;

    /// <summary>
    /// Gets whether the active platform exposes composition-mode choices.
    /// <para>获取当前平台是否提供合成模式选项。</para>
    /// </summary>
    /// <value>True on Windows.<para>Windows 上为 true。</para></value>
    public bool HasCompositionModeOptions => _compositionModes.Length > 0;

    /// <summary>
    /// Gets whether the selected rendering backend can honor a composition mode: only ANGLE/EGL with D3D11
    /// registers Win32 composition, so automatic and ANGLE/EGL selections keep the choice meaningful.
    /// <para>获取当前选择的渲染后端是否能配合合成模式：只有 ANGLE/EGL 取得 D3D11 时才注册 Win32 合成，
    /// 因此仅自动与 ANGLE/EGL 选择保持该选项有效。</para>
    /// </summary>
    /// <value>True when the composition combo box should accept input.<para>合成模式下拉框可输入时为 true。</para></value>
    public bool IsCompositionModeEnabled => HasCompositionModeOptions &&
        _renderingBackends[_renderingBackendIndex] is RenderingBackend.Auto or RenderingBackend.AngleEgl;

    /// <summary>
    /// Gets or sets the persisted rendering backend index. The running process keeps the backend it started
    /// with, so a change is saved and a restart prompt is shown instead of applying it live.
    /// <para>获取或设置持久化的渲染后端索引。运行中的进程保持启动时使用的后端，因此修改只会保存并弹出重启提示，
    /// 不会立即生效。</para>
    /// </summary>
    public int RenderingBackendIndex
    {
        get => _renderingBackendIndex;
        set
        {
            if (_loading || value < 0 || value >= _renderingBackends.Length || _renderingBackendIndex == value)
            {
                return;
            }

            _renderingBackendIndex = value;
            _appSettings.RenderingBackend = _renderingBackends[value];
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCompositionModeEnabled));
            DebouncedSaveSettings();
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowRenderingRestartPrompt);
        }
    }

    /// <summary>
    /// Gets or sets the persisted composition-mode index. The running process keeps the mode it started with,
    /// so a change is saved and a restart prompt is shown instead of applying it live.
    /// <para>获取或设置持久化的合成模式索引。运行中的进程保持启动时使用的模式，因此修改只会保存并弹出重启提示，
    /// 不会立即生效。</para>
    /// </summary>
    public int CompositionModeIndex
    {
        get => _compositionModeIndex;
        set
        {
            if (_loading || value < 0 || value >= _compositionModes.Length || _compositionModeIndex == value)
            {
                return;
            }

            _compositionModeIndex = value;
            _appSettings.RenderingCompositionMode = _compositionModes[value];
            OnPropertyChanged();
            DebouncedSaveSettings();
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowRenderingRestartPrompt);
        }
    }

    /// <summary>
    /// Prompts that the saved rendering preferences take effect only after the process restarts.
    /// <para>提示已保存的渲染偏好仅在进程重启后生效。</para>
    /// </summary>
    private void ShowRenderingRestartPrompt()
    {
        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var language = LanguageService.Instance;
        _ = DialogService.ShowAsync(owner,
            language.GetString("Settings_RenderingRestartTitle"),
            language.GetString("Settings_RenderingRestartMessage"),
            Ursa.Controls.MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Resolves the locale resource key of one rendering backend label.
    /// <para>解析一个渲染后端标签的语言资源键。</para>
    /// </summary>
    /// <param name="backend">The backend value.<para>后端取值。</para></param>
    /// <returns>The locale key.<para>语言资源键。</para></returns>
    private static string RenderingBackendLabelKey(RenderingBackend backend) => backend switch
    {
        RenderingBackend.Auto => "Settings_RenderingBackendAuto",
        RenderingBackend.Software => "Settings_RenderingBackendSoftware",
        RenderingBackend.AngleEgl => "Settings_RenderingBackendAngleEgl",
        RenderingBackend.Wgl => "Settings_RenderingBackendWgl",
        RenderingBackend.Vulkan => "Settings_RenderingBackendVulkan",
        RenderingBackend.Glx => "Settings_RenderingBackendGlx",
        RenderingBackend.Egl => "Settings_RenderingBackendEgl",
        RenderingBackend.Metal => "Settings_RenderingBackendMetal",
        RenderingBackend.OpenGl => "Settings_RenderingBackendOpenGl",
        _ => "Settings_RenderingBackendAuto",
    };

    /// <summary>
    /// Resolves the locale resource key of one composition-mode label.
    /// <para>解析一个合成模式标签的语言资源键。</para>
    /// </summary>
    /// <param name="mode">The composition-mode value.<para>合成模式取值。</para></param>
    /// <returns>The locale key.<para>语言资源键。</para></returns>
    private static string CompositionModeLabelKey(RenderingCompositionMode mode) => mode switch
    {
        RenderingCompositionMode.Auto => "Settings_CompositionModeAuto",
        RenderingCompositionMode.WinUIComposition => "Settings_CompositionModeWinUI",
        RenderingCompositionMode.DirectComposition => "Settings_CompositionModeDirect",
        RenderingCompositionMode.LowLatencyDxgiSwapChain => "Settings_CompositionModeLowLatency",
        RenderingCompositionMode.RedirectionSurface => "Settings_CompositionModeRedirection",
        _ => "Settings_CompositionModeAuto",
    };

    /// <summary>
    /// Builds the backend combo entries: the automatic entry reports the backend the running process actually
    /// uses, followed by the explicitly selectable backends.
    /// <para>构建后端下拉条目：自动项报告运行进程实际使用的后端，其后是显式可选的后端。</para>
    /// </summary>
    /// <returns>The localized entries in display order.<para>按显示顺序排列的本地化条目。</para></returns>
    private IEnumerable<LocalizedOption> BuildRenderingBackendOptions()
    {
        yield return LocalizedOption.FromFactory(() => ActiveBackendLabelKey() is { } key
            ? string.Format(
                LanguageService.Instance.GetString("Settings_RenderingBackendAuto"),
                LanguageService.Instance.GetString(key))
            : LanguageService.Instance.GetString("Settings_RenderingAutoSingle"));

        foreach (var backend in _renderingBackends.Skip(1))
        {
            yield return LocalizedOption.FromResource(RenderingBackendLabelKey(backend));
        }
    }

    /// <summary>
    /// Builds the composition-mode combo entries: the automatic entry reports the composition host the running
    /// process actually uses, followed by the explicitly selectable modes.
    /// <para>构建合成模式下拉条目：自动项报告运行进程实际使用的合成宿主，其后是显式可选的模式。</para>
    /// </summary>
    /// <returns>The localized entries in display order; empty outside Windows.<para>按显示顺序排列的本地化条目；Windows 之外为空。</para></returns>
    private IEnumerable<LocalizedOption> BuildCompositionModeOptions()
    {
        if (_compositionModes.Length == 0)
        {
            yield break;
        }

        yield return LocalizedOption.FromFactory(() => ActiveCompositionLabelKey() is { } key
            ? string.Format(
                LanguageService.Instance.GetString("Settings_CompositionModeAuto"),
                LanguageService.Instance.GetString(key))
            : LanguageService.Instance.GetString("Settings_RenderingAutoSingle"));

        foreach (var mode in _compositionModes.Skip(1))
        {
            yield return LocalizedOption.FromResource(CompositionModeLabelKey(mode));
        }
    }

    /// <summary>
    /// Gets the locale key of the backend the running process uses, probing once and caching a successful result
    /// so later language switches only re-resolve the label text.
    /// <para>获取运行进程实际使用的后端资源键；探测一次并缓存成功结果，后续语言切换只重新解析标签文本。</para>
    /// </summary>
    /// <returns>The backend label key, or null when the backend cannot be observed.
    /// <para>后端标签资源键；无法观测时为 null。</para></returns>
    private string? ActiveBackendLabelKey()
        => _activeBackendLabelKey ??= RenderingRuntimeProbe.DetectActiveBackendLabelKey();

    /// <summary>
    /// Gets the locale key of the composition host the running process uses, probing once and caching a successful
    /// result so later language switches only re-resolve the label text.
    /// <para>获取运行进程实际使用的合成宿主资源键；探测一次并缓存成功结果，后续语言切换只重新解析标签文本。</para>
    /// </summary>
    /// <returns>The composition label key, or null when the host cannot be observed.
    /// <para>合成模式标签资源键；无法观测时为 null。</para></returns>
    private string? ActiveCompositionLabelKey()
        => _activeCompositionLabelKey ??= RenderingRuntimeProbe.DetectActiveCompositionLabelKey();

    private bool _stoppingServer;
    private string _serverActionKey = "Settings_ServerActionNone";
    private string? _serverActionError;

    /// <summary>Localized result of the explicit server stop action.
    /// <para>显式停止服务器操作的本地化结果。</para>
    /// </summary>
    public string ServerActionStatus => _serverActionKey == "Settings_ServerActionNone" ? string.Empty : _serverActionError is null
        ? LanguageService.Instance.GetString(_serverActionKey)
        : string.Format(LanguageService.Instance.GetString(_serverActionKey), _serverActionError);

    private bool CanStopServer() => !_stoppingServer;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private Task StopAdbServerAsync(CancellationToken cancellationToken)
        => StopServerAsync(App.DeviceMonitor.StopAdbServerAsync, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private Task StopHdcServerAsync(CancellationToken cancellationToken)
        => StopServerAsync(App.DeviceMonitor.StopHdcServerAsync, cancellationToken);

    private async Task StopServerAsync(Func<CancellationToken, Task> stop, CancellationToken cancellationToken)
    {
        _stoppingServer = true;
        StopAdbServerCommand.NotifyCanExecuteChanged();
        StopHdcServerCommand.NotifyCanExecuteChanged();
        _serverActionKey = "Settings_ServerStopping";
        _serverActionError = null;
        OnPropertyChanged(nameof(ServerActionStatus));
        try
        {
            await stop(cancellationToken);
            _serverActionKey = "Settings_ServerStopped";
        }
        catch (Exception ex)
        {
            _serverActionKey = "Settings_ServerStopFailed";
            _serverActionError = UserError.Describe(ex);
        }
        finally
        {
            await RefreshCommunicationStatusAsync(CancellationToken.None);
            _stoppingServer = false;
            StopAdbServerCommand.NotifyCanExecuteChanged();
            StopHdcServerCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ServerActionStatus));
        }
    }

    private string _adbServerStatusKey = "Settings_ServerUnchecked";
    private string _hdcServerStatusKey = "Settings_ServerUnchecked";

    /// <summary>Localized result of the latest ADB endpoint probe.
    /// <para>最近一次 ADB 端点探测的本地化结果。</para>
    /// </summary>
    public string AdbServerStatus => LanguageService.Instance.GetString(_adbServerStatusKey);

    /// <summary>Localized result of the latest HDC endpoint probe.
    /// <para>最近一次 HDC 端点探测的本地化结果。</para>
    /// </summary>
    public string HdcServerStatus => LanguageService.Instance.GetString(_hdcServerStatusKey);

    [RelayCommand]
    private async Task RefreshCommunicationStatusAsync(CancellationToken cancellationToken)
    {
        var adb = ProbeServerStatusAsync(Comm.DeviceCommunicationConfiguration.ProbeAdbServerAsync, cancellationToken);
        var hdc = ProbeServerStatusAsync(Comm.DeviceCommunicationConfiguration.ProbeHdcServerAsync, cancellationToken);
        await Task.WhenAll(adb, hdc);
        _adbServerStatusKey = await adb;
        _hdcServerStatusKey = await hdc;
        OnPropertyChanged(nameof(AdbServerStatus));
        OnPropertyChanged(nameof(HdcServerStatus));
    }

    private static async Task<string> ProbeServerStatusAsync(Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        try
        {
            return await probe(cancellationToken) ? "Settings_ServerListening" : "Settings_ServerAbsent";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "Settings_ServerUnknown";
        }
    }

    /// <summary>Maximum concurrent firmware workers, applied to newly started operations.
    /// <para>固件并发工作线程上限，仅作用于新启动的操作。</para>
    /// </summary>
    public int FirmwareParallelism
    {
        get => _appSettings.FirmwareParallelism;
        set
        {
            var workers = Math.Clamp(value, 1, Firmware.Core.FirmwareParallelism.MaximumWorkers);
            if (_appSettings.FirmwareParallelism == workers) return;
            _appSettings.FirmwareParallelism = workers;
            Firmware.Core.FirmwareParallelism.Workers = workers;
            OnPropertyChanged();
            DebouncedSaveSettings();
        }
    }

    private CancellationTokenSource? _saveCts;
    private bool _disposed;
    private bool _loading;

    /// <summary>
    /// Gets the list of available languages, dynamically retrieved from the language service.
    /// <para>获取可用语言列表，从语言服务动态获取。</para>
    /// </summary>
    public ObservableCollection<LanguageInfo> Languages { get; }

    /// <summary>
    /// Gets the list of available theme modes.
    /// <para>获取可用主题模式列表。</para>
    /// </summary>
    public List<ThemeMode> Themes { get; } =
        [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    /// <summary>
    /// Gets the MCP server connection list (data source for the MCP settings section).
    /// <para>获取 MCP 服务器连接列表（MCP 设置区块数据源）。</para>
    /// </summary>
    public ObservableCollection<McpClientServerConfig> McpServers { get; } = [];

    /// <summary>
    /// Gets the MCP transport kind list (data source for the transport combo box).
    /// <para>获取 MCP 传输类型列表（传输下拉框数据源）。</para>
    /// </summary>
    public IReadOnlyList<McpTransportKind> McpTransportKinds { get; } = Enum.GetValues<McpTransportKind>();

    /// <summary>
    /// Gets or sets whether to expose this application's firmware capabilities as an MCP server.
    /// <para>获取或设置是否把本应用的固件能力暴露为 MCP 服务器。</para>
    /// </summary>
    public bool McpExposeEnabled
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Gets or sets whether a preserved EROFS rebuild searches the compression estimates captured at extraction
    /// for the smallest image.
    /// <para>获取或设置 EROFS 受保留重建是否在解包时采集的压缩估算之间搜索最小镜像。</para>
    /// </summary>
    public bool ErofsRebuildOptimization
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                Firmware.FileSystem.Erofs.Builder.ErofsBuildDefaults.RebuildOptimization = value;
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// Gets or sets the MCP server exposure port (clamped to the valid port range).
    /// <para>获取或设置 MCP 服务器暴露端口（限制在有效端口范围内）。</para>
    /// </summary>
    public int McpExposePort
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, 1, 65535));
    } = AppSettings.DefaultMcpExposePort;

    /// <summary>
    /// Gets or sets the filesystem root the embedded MCP server confines client paths to; empty means unrestricted.
    /// <para>获取或设置内嵌 MCP 服务器的文件沙箱根目录；空字符串表示不限制根目录。</para>
    /// </summary>
    public string McpExposeRootDirectory
    {
        get;
        set => SetProperty(ref field, value?.Trim() ?? string.Empty);
    } = string.Empty;

    /// <summary>
    /// Gets or sets whether the embedded MCP server exposes the flashing, wiping and reboot tools (off by default).
    /// <para>获取或设置内嵌 MCP 服务器是否暴露刷写、擦除、重启类工具（默认关闭）。</para>
    /// </summary>
    public bool McpExposeAllowFlash
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Picks the sandbox root directory for the embedded MCP server; keeps the current value when the platform
    /// has no folder picker or the user cancels.
    /// <para>选择内嵌 MCP 服务器的沙箱根目录；平台没有目录选择器或用户取消时保持原值。</para>
    /// </summary>
    [RelayCommand]
    private async Task BrowseMcpRootAsync()
    {
        if (TopLevelExtensions.GetTopLevel() is not { StorageProvider.CanPickFolder: true } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Settings_McpExposeRootPick"),
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            McpExposeRootDirectory = path;
        }
    }

    /// <summary>
    /// Gets the language preview text, dynamically updated based on the current language.
    /// <para>获取语言预览文本，根据当前语言动态更新。</para>
    /// </summary>
    public string LanguagePreviewText => LanguageService.Instance.GetString("Settings_LanguagePreviewText");

    /// <summary>
    /// Gets or sets the selected language information.
    /// <para>获取或设置选中的语言信息。</para>
    /// </summary>
    public LanguageInfo SelectedLanguage
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _loading)
            {
                return;
            }

            ApplyLanguageInfo(value);
            OnPropertyChanged(nameof(LanguagePreviewText));
            DebouncedSaveSettings();
        }
    } = LanguageInfo.SystemLanguage;

    /// <summary>
    /// Gets or sets the selected theme mode.
    /// <para>获取或设置选中的主题模式。</para>
    /// </summary>
    public ThemeMode SelectedThemeMode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _loading)
            {
                return;
            }

            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsSystemTheme));
            WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(value));
            DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// Gets or sets whether the light theme is selected (for RadioButton binding).
    /// <para>获取或设置是否为浅色主题（用于 RadioButton 绑定）。</para>
    /// </summary>
    public bool IsLightTheme
    {
        get => SelectedThemeMode == ThemeMode.Light;
        set
        {
            if (value && SelectedThemeMode != ThemeMode.Light)
            {
                SelectedThemeMode = ThemeMode.Light;
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the dark theme is selected (for RadioButton binding).
    /// <para>获取或设置是否为深色主题（用于 RadioButton 绑定）。</para>
    /// </summary>
    public bool IsDarkTheme
    {
        get => SelectedThemeMode == ThemeMode.Dark;
        set
        {
            if (value && SelectedThemeMode != ThemeMode.Dark)
            {
                SelectedThemeMode = ThemeMode.Dark;
            }
        }
    }

    /// <summary>
    /// Gets or sets whether to follow the system theme (for RadioButton binding).
    /// <para>获取或设置是否跟随系统主题（用于 RadioButton 绑定）。</para>
    /// </summary>
    public bool IsSystemTheme
    {
        get => SelectedThemeMode == ThemeMode.System;
        set
        {
            if (value && SelectedThemeMode != ThemeMode.System)
            {
                SelectedThemeMode = ThemeMode.System;
            }
        }
    }

    /// <summary>
    /// Gets or sets whether auto-save is enabled.
    /// <para>获取或设置是否启用自动保存。</para>
    /// </summary>
    public bool AutoSave
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether debug mode is enabled (when on, logs show Trace/Debug level diagnostic details).
    /// <para>获取或设置是否启用调试模式（开启后日志显示 Trace/Debug 级别详细诊断信息）。</para>
    /// </summary>
    public bool DebugMode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                LogService.Instance.ApplyDebugMode(value);
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// Initializes the settings view model, loading settings from the persistence service and initializing all properties.
    /// <para>初始化设置视图模型，从持久化服务加载设置并初始化所有属性。</para>
    /// </summary>
    public SettingsViewModel() : this(SettingsPersistenceService.Instance.LoadSettings(), applyLanguage: true)
    {
    }

    internal SettingsViewModel(AppSettings settings, bool applyLanguage)
    {
        _appSettings = settings;

        _renderingPlatform = RenderingRuntimeProbe.DetectWindowingPlatform();
        _renderingBackends = [.. RenderingPreferenceCatalog.SupportedBackends(_renderingPlatform)];
        _compositionModes = [.. RenderingPreferenceCatalog.SupportedCompositionModes(_renderingPlatform)];
        _renderingBackendOptions = LocalizedOptionSet.FromOptions([.. BuildRenderingBackendOptions()]);
        _compositionModeOptions = LocalizedOptionSet.FromOptions([.. BuildCompositionModeOptions()]);

        Languages = new ObservableCollection<LanguageInfo>(LanguageService.Instance.AvailableLanguages);

        InitializeFromAppSettings(_appSettings, applyLanguage);

        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Initializes all property values from an <see cref="AppSettings"/> instance.
    /// <para>从 <see cref="AppSettings"/> 实例初始化所有属性值。</para>
    /// </summary>
    /// <param name="settings">
    /// The application settings instance to load from.
    /// <para>要加载的应用程序设置实例。</para>
    /// </param>
    /// <param name="applyLanguage">Whether the loaded language still needs to be applied.</param>
    private void InitializeFromAppSettings(AppSettings settings, bool applyLanguage = true)
    {
        _loading = true;
        try
        {
            Firmware.Core.FirmwareParallelism.Workers = settings.FirmwareParallelism;
            Firmware.FileSystem.Erofs.Builder.ErofsBuildDefaults.RebuildOptimization = settings.ErofsRebuildOptimization;

            ErofsRebuildOptimization = settings.ErofsRebuildOptimization;
            SelectedLanguage = string.IsNullOrEmpty(settings.CultureName)
                ? LanguageInfo.SystemLanguage
                : LanguageService.Instance.AvailableLanguages.FirstOrDefault(l => l.CultureName == settings.CultureName)
                  ?? new LanguageInfo(settings.CultureName, settings.CultureName, settings.CultureName);
            SelectedThemeMode = settings.ThemeMode;
            AutoSave = settings.AutoSave;
            DebugMode = settings.DebugMode;

            var renderingPlatform = _renderingPlatform;
            _renderingBackendIndex = Math.Max(0, Array.IndexOf(
                _renderingBackends,
                RenderingPreferenceCatalog.Normalize(renderingPlatform, settings.RenderingBackend)));
            _compositionModeIndex = Math.Max(0, Array.IndexOf(
                _compositionModes,
                RenderingPreferenceCatalog.NormalizeComposition(renderingPlatform, settings.RenderingCompositionMode)));

            LogService.Instance.ApplyDebugMode(settings.DebugMode);

            if (applyLanguage)
            {
                ApplyLanguageInfo(SelectedLanguage);
            }

            McpServers.Clear();
            foreach (var server in settings.McpServers)
            {
                McpServers.Add(new McpClientServerConfig
                {
                    Name = server.Name,
                    Transport = server.Transport,
                    Command = server.Command,
                    Arguments = server.Arguments,
                    Url = server.Url,
                    Enabled = server.Enabled
                });
            }

            McpExposeEnabled = settings.McpExposeEnabled;
            McpExposePort = settings.McpExposePort is > 0 and <= 65535 ? settings.McpExposePort : AppSettings.DefaultMcpExposePort;
            McpExposeRootDirectory = settings.McpExposeRootDirectory?.Trim() ?? string.Empty;
            McpExposeAllowFlash = settings.McpExposeAllowFlash;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Applies the language setting based on the language information.
    /// <para>根据语言信息应用语言设置。</para>
    /// </summary>
    /// <param name="languageInfo">
    /// The language information.
    /// <para>语言信息。</para>
    /// </param>
    private void ApplyLanguageInfo(LanguageInfo languageInfo)
    {
        if (languageInfo.IsSystemLanguage)
        {
            LanguageService.Instance.InitializeFromSystemLanguage();
        }
        else if (!string.IsNullOrEmpty(languageInfo.CultureName))
        {
            LanguageService.Instance.ApplyLanguage(languageInfo.CultureName);
        }
    }

    /// <summary>
    /// Saves settings with a debounced strategy, persisting after 500ms of no changes.
    /// <para>使用防抖策略延迟保存设置，在 500 毫秒无变更后执行持久化。</para>
    /// </summary>
    private void DebouncedSaveSettings()
    {
        if (_loading)
        {
            return;
        }

        var oldCts = _saveCts;
        _saveCts = new CancellationTokenSource();
        oldCts?.Cancel();
        oldCts?.Dispose();

        _ = DebouncedSaveSettingsAsync(_saveCts.Token);
    }

    /// <summary>
    /// Async implementation of debounced save, waiting for the specified duration before saving.
    /// <para>防抖保存的异步实现，等待指定时间后执行保存。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation token to cancel this save operation.
    /// <para>用于取消本次保存操作的取消令牌。</para>
    /// </param>
    private async Task DebouncedSaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SaveSettings();
    }

    /// <summary>
    /// Syncs current property values to <see cref="AppSettings"/> and saves via the persistence service, then shows a toast notification.
    /// <para>将当前属性值同步到 <see cref="AppSettings"/> 并通过持久化服务保存，同时显示 Toast 通知。</para>
    /// </summary>
    private void SaveSettings()
    {
        _appSettings.CultureName = SelectedLanguage.IsSystemLanguage ? string.Empty : SelectedLanguage.CultureName;
        _appSettings.ThemeMode = SelectedThemeMode;
        _appSettings.AutoSave = AutoSave;
        _appSettings.DebugMode = DebugMode;
        _appSettings.ErofsRebuildOptimization = ErofsRebuildOptimization;
        if (IsRenderingBackendConfigurable)
        {
            _appSettings.RenderingBackend = _renderingBackends[_renderingBackendIndex];
        }

        if (HasCompositionModeOptions)
        {
            _appSettings.RenderingCompositionMode = _compositionModes[_compositionModeIndex];
        }

        var latest = SettingsPersistenceService.Instance.LoadSettings();
        _appSettings.RecentProjects = latest.RecentProjects;
        _appSettings.FavoriteNodeTypes = latest.FavoriteNodeTypes;
        _appSettings.ExpandedNodeCategories = latest.ExpandedNodeCategories;
        _appSettings.PairedNetworkTargets = latest.PairedNetworkTargets;
        _appSettings.MainWindowPlacement = latest.MainWindowPlacement;

        _appSettings.McpServers = [.. McpServers];
        _appSettings.McpExposeEnabled = McpExposeEnabled;
        _appSettings.McpExposePort = McpExposePort;
        _appSettings.McpExposeRootDirectory = McpExposeRootDirectory;
        _appSettings.McpExposeAllowFlash = McpExposeAllowFlash;

        var saved = SettingsPersistenceService.Instance.TrySaveSettings(_appSettings, out var error);
        WeakReferenceMessenger.Default.Send(new SettingsSaveCompletedMessage(saved, error));

        if (saved)
        {
            McpExposureService.Instance.Apply(McpExposeEnabled, McpExposePort, McpExposeRootDirectory, McpExposeAllowFlash);
            McpClientConnectionService.Instance.Apply(McpServers);
        }
    }

    internal void FlushPendingSave()
    {
        if (_saveCts is null) return;
        _saveCts.Cancel();
        SaveSettings();
    }

    /// <summary>
    /// Listens to language service change events and updates language-related properties.
    /// <para>监听语言服务变更事件，更新语言相关属性。</para>
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguagePreviewText));
        OnPropertyChanged(nameof(ServerActionStatus));
        OnPropertyChanged(nameof(AdbServerStatus));
        OnPropertyChanged(nameof(HdcServerStatus));
        foreach (var member in DevelopmentMembers.All)
        {
            member.RefreshLocalizedText();
        }
    }

    /// <summary>
    /// Adds a default MCP server config (HTTP transport).
    /// <para>添加一条默认的 MCP 服务器配置（HTTP 传输）。</para>
    /// </summary>
    [RelayCommand]
    private void AddMcpServer()
    {
        var index = McpServers.Count + 1;
        McpServers.Add(new McpClientServerConfig
        {
            Name = $"MCP Server {index}",
            Transport = McpTransportKind.Http,
            Url = "http://localhost:8080/mcp",
            Enabled = true
        });
    }

    /// <summary>
    /// Removes the given MCP server config.
    /// <para>删除指定的 MCP 服务器配置。</para>
    /// </summary>
    /// <param name="server">The server config to remove. <para>要删除的服务器配置。</para></param>
    [RelayCommand]
    private void RemoveMcpServer(McpClientServerConfig? server)
    {
        if (server is not null)
        {
            McpServers.Remove(server);
        }
    }

    /// <summary>
    /// Saves the MCP settings: writes the connection list plus the exposure toggle, port, sandbox root and
    /// flash gate into the application settings and persists them; a successful save immediately starts,
    /// re-targets or stops the embedded MCP server per the new configuration.
    /// <para>保存 MCP 设置：把连接列表与暴露开关、端口、沙箱根目录、刷写门控写入应用设置并持久化；保存成功后
    /// 立即按新配置启动、换配置或停止内嵌 MCP 服务器。</para>
    /// </summary>
    [RelayCommand]
    private void SaveMcpSettings() => SaveSettings();

    /// <summary>
    /// Resets all settings to default values, reloads them, and notifies of theme changes.
    /// <para>重置所有设置为默认值，重新加载并通知主题变更。</para>
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        Ai.Reset();
        _appSettings = SettingsPersistenceService.Instance.ResetToDefaults();
        InitializeFromAppSettings(_appSettings);
        McpExposureService.Instance.Apply(McpExposeEnabled, McpExposePort, McpExposeRootDirectory, McpExposeAllowFlash);

        Languages.Clear();
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            Languages.Add(lang);
        }

        OnPropertyChanged(string.Empty);
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(SelectedThemeMode));
    }

    /// <summary>
    /// Releases all resources, including cancellation token sources and event subscriptions.
    /// <para>释放所有资源，包括取消令牌源和事件订阅。</para>
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Core method for releasing resources.
    /// <para>释放资源的核心方法。</para>
    /// </summary>
    /// <param name="disposing">
    /// Whether to release managed resources.
    /// <para>是否释放托管资源。</para>
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        }

        _disposed = true;
    }
}