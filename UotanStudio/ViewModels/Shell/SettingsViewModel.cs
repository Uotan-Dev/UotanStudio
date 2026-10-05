using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Mcp;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>设置视图模型，管理应用程序的通用设置、固件工具设置、烧录设置和节点编辑器设置。</para>
/// Settings view model, managing general settings, firmware tool settings, flash settings, and node editor settings.
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>Shared, in-memory AI settings. 共用的内存 AI 设置。</summary>
    public AiSettingsViewModel Ai { get; } = new();

    private AppSettings _appSettings;

    /// <summary>Persisted USB mode; existing sessions retain their backend until restart.</summary>
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
                UsbBackend = (global::UotanStudio.Comm.Transports.Usb.Abstractions.UsbApiKind)value
            };
            OnPropertyChanged();
            DebouncedSaveSettings();
        }
    }

    private bool _stoppingServer;
    private string _serverActionKey = "Settings_ServerActionNone";
    private string? _serverActionError;

    /// <summary>Localized result of the explicit server stop action.</summary>
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
            _serverActionError = global::UotanStudio.Services.UserError.Describe(ex);
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

    /// <summary>Localized result of the latest ADB endpoint probe.</summary>
    public string AdbServerStatus => LanguageService.Instance.GetString(_adbServerStatusKey);

    /// <summary>Localized result of the latest HDC endpoint probe.</summary>
    public string HdcServerStatus => LanguageService.Instance.GetString(_hdcServerStatusKey);

    [RelayCommand]
    private async Task RefreshCommunicationStatusAsync(CancellationToken cancellationToken)
    {
        var adb = ProbeServerStatusAsync(global::UotanStudio.Comm.DeviceCommunicationConfiguration.ProbeAdbServerAsync, cancellationToken);
        var hdc = ProbeServerStatusAsync(global::UotanStudio.Comm.DeviceCommunicationConfiguration.ProbeHdcServerAsync, cancellationToken);
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

    /// <summary>Maximum concurrent firmware workers, applied to newly started operations.</summary>
    public int FirmwareParallelism
    {
        get => _appSettings.FirmwareParallelism;
        set
        {
            var workers = Math.Clamp(value, 1, global::UotanStudio.Firmware.Core.FirmwareParallelism.MaximumWorkers);
            if (_appSettings.FirmwareParallelism == workers) return;
            _appSettings.FirmwareParallelism = workers;
            global::UotanStudio.Firmware.Core.FirmwareParallelism.Workers = workers;
            OnPropertyChanged();
            DebouncedSaveSettings();
        }
    }

    private CancellationTokenSource? _saveCts;
    private bool _disposed;
    private bool _loading;

    /// <summary>
    /// <para>获取可用语言列表，从语言服务动态获取。</para>
    /// Gets the list of available languages, dynamically retrieved from the language service.
    /// </summary>
    public ObservableCollection<LanguageInfo> Languages { get; }

    /// <summary>
    /// <para>获取可用主题模式列表。</para>
    /// Gets the list of available theme modes.
    /// </summary>
    public List<ThemeMode> Themes { get; } =
        [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    /// <summary>
    /// <para>获取 MCP 服务器连接列表（MCP 设置区块数据源）。</para>
    /// Gets the MCP server connection list (data source for the MCP settings section).
    /// </summary>
    public ObservableCollection<McpServerConfig> McpServers { get; } = [];

    /// <summary>
    /// <para>获取 MCP 传输类型列表（传输下拉框数据源）。</para>
    /// Gets the MCP transport kind list (data source for the transport combo box).
    /// </summary>
    public IReadOnlyList<McpTransportKind> McpTransportKinds { get; } = Enum.GetValues<McpTransportKind>();

    /// <summary>
    /// <para>获取或设置是否把本应用的固件能力暴露为 MCP 服务器。</para>
    /// Gets or sets whether to expose this application's firmware capabilities as an MCP server.
    /// </summary>
    public bool McpExposeEnabled
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取或设置新建 EROFS 镜像是否使用 legacy（pre-5.13）布局，供旧内核挂载。</para>
    /// Gets or sets whether newly built EROFS images use the legacy (pre-5.13) layout for older kernels.
    /// </summary>
    public bool ErofsLegacyCompatibility
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                global::UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuildDefaults.LegacyCompatibility = value;
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置 MCP 服务器暴露端口（限制在有效端口范围内）。</para>
    /// Gets or sets the MCP server exposure port (clamped to the valid port range).
    /// </summary>
    public int McpExposePort
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, 1, 65535));
    } = AppSettings.DefaultMcpExposePort;

    /// <summary>
    /// <para>获取或设置内嵌 MCP 服务器的文件沙箱根目录；空字符串表示不限制根目录。</para>
    /// Gets or sets the filesystem root the embedded MCP server confines client paths to; empty means unrestricted.
    /// </summary>
    public string McpExposeRootDirectory
    {
        get;
        set => SetProperty(ref field, value?.Trim() ?? string.Empty);
    } = string.Empty;

    /// <summary>
    /// <para>获取或设置内嵌 MCP 服务器是否暴露刷写、擦除、重启类工具（默认关闭）。</para>
    /// Gets or sets whether the embedded MCP server exposes the flashing, wiping and reboot tools (off by default).
    /// </summary>
    public bool McpExposeAllowFlash
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>选择内嵌 MCP 服务器的沙箱根目录；平台没有目录选择器或用户取消时保持原值。</para>
    /// Picks the sandbox root directory for the embedded MCP server; keeps the current value when the platform
    /// has no folder picker or the user cancels.
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
    /// <para>获取语言预览文本，根据当前语言动态更新。</para>
    /// Gets the language preview text, dynamically updated based on the current language.
    /// </summary>
    public string LanguagePreviewText => LanguageService.Instance.GetString("Settings_LanguagePreviewText");

    /// <summary>
    /// <para>获取或设置选中的语言信息。</para>
    /// Gets or sets the selected language information.
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
    /// <para>获取或设置选中的主题模式。</para>
    /// Gets or sets the selected theme mode.
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
    /// <para>获取或设置是否为浅色主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether the light theme is selected (for RadioButton binding).
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
    /// <para>获取或设置是否为深色主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether the dark theme is selected (for RadioButton binding).
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
    /// <para>获取或设置是否跟随系统主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether to follow the system theme (for RadioButton binding).
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
    /// <para>获取或设置是否启用自动保存。</para>
    /// Gets or sets whether auto-save is enabled.
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
    /// <para>获取或设置刷写分区前是否自动按当前协议备份该分区。</para>
    /// Gets or sets whether a partition is backed up over the selected protocol before it is flashed.
    /// </summary>
    public bool FlashAutoBackup
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
    /// <para>获取或设置是否启用调试模式（开启后日志显示 Trace/Debug 级别详细诊断信息）。</para>
    /// Gets or sets whether debug mode is enabled (when on, logs show Trace/Debug level diagnostic details).
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
    /// <para>初始化设置视图模型，从持久化服务加载设置并初始化所有属性。</para>
    /// Initializes the settings view model, loading settings from the persistence service and initializing all properties.
    /// </summary>
    public SettingsViewModel() : this(SettingsPersistenceService.Instance.LoadSettings(), applyLanguage: true)
    {
    }

    internal SettingsViewModel(AppSettings settings, bool applyLanguage)
    {
        _appSettings = settings;

        Languages = new ObservableCollection<LanguageInfo>(LanguageService.Instance.AvailableLanguages);

        InitializeFromAppSettings(_appSettings, applyLanguage);

        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
        LanguagePackRegistry.Instance.LanguagePackChanged += OnLanguagePackChanged;
    }

    /// <summary>
    /// <para>从 <see cref="AppSettings"/> 实例初始化所有属性值。</para>
    /// Initializes all property values from an <see cref="AppSettings"/> instance.
    /// </summary>
    /// <param name="settings">
    /// <para>要加载的应用程序设置实例。</para>
    /// The application settings instance to load from.
    /// </param>
    /// <param name="applyLanguage">Whether the loaded language still needs to be applied.</param>
    private void InitializeFromAppSettings(AppSettings settings, bool applyLanguage = true)
    {
        _loading = true;
        try
        {
            global::UotanStudio.Firmware.Core.FirmwareParallelism.Workers = settings.FirmwareParallelism;
            global::UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuildDefaults.LegacyCompatibility = settings.ErofsLegacyCompatibility;

            ErofsLegacyCompatibility = settings.ErofsLegacyCompatibility;
            SelectedLanguage = string.IsNullOrEmpty(settings.CultureName)
                ? LanguageInfo.SystemLanguage
                : LanguageService.Instance.AvailableLanguages.FirstOrDefault(l => l.CultureName == settings.CultureName)
                  ?? new LanguageInfo(settings.CultureName, settings.CultureName, settings.CultureName);
            SelectedThemeMode = settings.ThemeMode;
            AutoSave = settings.AutoSave;
            FlashAutoBackup = settings.AutoBackupBeforeFlash;
            DebugMode = settings.DebugMode;

            LogService.Instance.ApplyDebugMode(settings.DebugMode);

            if (applyLanguage)
            {
                ApplyLanguageInfo(SelectedLanguage);
            }

            McpServers.Clear();
            foreach (var server in settings.McpServers)
            {
                McpServers.Add(new McpServerConfig
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
    /// <para>根据语言信息应用语言设置。</para>
    /// Applies the language setting based on the language information.
    /// </summary>
    /// <param name="languageInfo">
    /// <para>语言信息。</para>
    /// The language information.
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
    /// <para>使用防抖策略延迟保存设置，在 500 毫秒无变更后执行持久化。</para>
    /// Saves settings with a debounced strategy, persisting after 500ms of no changes.
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
    /// <para>防抖保存的异步实现，等待指定时间后执行保存。</para>
    /// Async implementation of debounced save, waiting for the specified duration before saving.
    /// </summary>
    /// <param name="cancellationToken">
    /// <para>用于取消本次保存操作的取消令牌。</para>
    /// Cancellation token to cancel this save operation.
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
    /// <para>将当前属性值同步到 <see cref="AppSettings"/> 并通过持久化服务保存，同时显示 Toast 通知。</para>
    /// Syncs current property values to <see cref="AppSettings"/> and saves via the persistence service, then shows a toast notification.
    /// </summary>
    private void SaveSettings()
    {
        _appSettings.CultureName = SelectedLanguage.IsSystemLanguage ? string.Empty : SelectedLanguage.CultureName;
        _appSettings.ThemeMode = SelectedThemeMode;
        _appSettings.AutoSave = AutoSave;
        _appSettings.AutoBackupBeforeFlash = FlashAutoBackup;
        _appSettings.DebugMode = DebugMode;
        _appSettings.ErofsLegacyCompatibility = ErofsLegacyCompatibility;

        var latest = SettingsPersistenceService.Instance.LoadSettings();
        _appSettings.RecentProjects = latest.RecentProjects;
        _appSettings.FavoriteNodeTypes = latest.FavoriteNodeTypes;
        _appSettings.RecentNodeTypes = latest.RecentNodeTypes;
        _appSettings.ExpandedNodeCategories = latest.ExpandedNodeCategories;
        _appSettings.PairedNetworkTargets = latest.PairedNetworkTargets;

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
    /// <para>监听语言服务变更事件，更新语言相关属性。</para>
    /// Listens to language service change events and updates language-related properties.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguagePreviewText));
        OnPropertyChanged(nameof(ServerActionStatus));
        OnPropertyChanged(nameof(AdbServerStatus));
        OnPropertyChanged(nameof(HdcServerStatus));
    }

    /// <summary>
    /// <para>监听语言包注册中心变更事件，动态更新可用语言列表。</para>
    /// Listens to language pack registry change events and dynamically updates the available languages list.
    /// </summary>
    private void OnLanguagePackChanged(object? sender, LanguagePackChangedEventArgs e)
    {

        Languages.Clear();
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            Languages.Add(lang);
        }
    }

    /// <summary>
    /// <para>添加一条默认的 MCP 服务器配置（HTTP 传输）。</para>
    /// Adds a default MCP server config (HTTP transport).
    /// </summary>
    [RelayCommand]
    private void AddMcpServer()
    {
        var index = McpServers.Count + 1;
        McpServers.Add(new McpServerConfig
        {
            Name = $"MCP Server {index}",
            Transport = McpTransportKind.Http,
            Url = "http://localhost:8080/mcp",
            Enabled = true
        });
    }

    /// <summary>
    /// <para>删除指定的 MCP 服务器配置。</para>
    /// Removes the given MCP server config.
    /// </summary>
    /// <param name="server"><para>要删除的服务器配置。</para>The server config to remove.</param>
    [RelayCommand]
    private void RemoveMcpServer(McpServerConfig? server)
    {
        if (server is not null)
        {
            McpServers.Remove(server);
        }
    }

    /// <summary>
    /// <para>保存 MCP 设置：把连接列表与暴露开关、端口、沙箱根目录、刷写门控写入应用设置并持久化；保存成功后
    /// 立即按新配置启动、换配置或停止内嵌 MCP 服务器。</para>
    /// Saves the MCP settings: writes the connection list plus the exposure toggle, port, sandbox root and
    /// flash gate into the application settings and persists them; a successful save immediately starts,
    /// re-targets or stops the embedded MCP server per the new configuration.
    /// </summary>
    [RelayCommand]
    private void SaveMcpSettings() => SaveSettings();

    /// <summary>
    /// <para>重置所有设置为默认值，重新加载并通知主题变更。</para>
    /// Resets all settings to default values, reloads them, and notifies of theme changes.
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
    /// <para>释放所有资源，包括取消令牌源和事件订阅。</para>
    /// Releases all resources, including cancellation token sources and event subscriptions.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <para>释放资源的核心方法。</para>
    /// Core method for releasing resources.
    /// </summary>
    /// <param name="disposing">
    /// <para>是否释放托管资源。</para>
    /// Whether to release managed resources.
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
            LanguagePackRegistry.Instance.LanguagePackChanged -= OnLanguagePackChanged;
        }

        _disposed = true;
    }
}