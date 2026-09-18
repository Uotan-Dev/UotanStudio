using System.Collections.ObjectModel;
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

    /// <summary>Maximum concurrent firmware workers, applied to newly started operations.</summary>
    public int FirmwareParallelism
    {
        get => _appSettings.FirmwareParallelism;
        set
        {
            var workers = Math.Clamp(value, 1, global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.MaximumWorkers);
            if (_appSettings.FirmwareParallelism == workers) return;
            _appSettings.FirmwareParallelism = workers;
            global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers = workers;
            OnPropertyChanged();
            DebouncedSaveSettings();
        }
    }

    private CancellationTokenSource? _saveCts;
    private bool _disposed;

    private LanguageInfo _selectedLanguage = LanguageInfo.SystemLanguage;
    private ThemeMode _selectedThemeMode;
    private bool _autoSave;
    private bool _debugMode;

    // MCP 设置字段
    // MCP settings fields
    private bool _mcpExposeEnabled;
    private int _mcpExposePort = AppSettings.DefaultMcpExposePort;

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
        get => _mcpExposeEnabled;
        set => SetProperty(ref _mcpExposeEnabled, value);
    }

    /// <summary>
    /// <para>获取或设置 MCP 服务器暴露端口（限制在有效端口范围内）。</para>
    /// Gets or sets the MCP server exposure port (clamped to the valid port range).
    /// </summary>
    public int McpExposePort
    {
        get => _mcpExposePort;
        set => SetProperty(ref _mcpExposePort, Math.Clamp(value, 1, 65535));
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
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                ApplyLanguageInfo(value);
                OnPropertyChanged(nameof(LanguagePreviewText));
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置选中的主题模式。</para>
    /// Gets or sets the selected theme mode.
    /// </summary>
    public ThemeMode SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (SetProperty(ref _selectedThemeMode, value))
            {
                // 通知三个 RadioButton 绑定属性同步更新，避免快速来回点击时 UI 选中态与 VM 脱节导致主题切换丢失
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsSystemTheme));
                WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(value));
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置是否为浅色主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether the light theme is selected (for RadioButton binding).
    /// </summary>
    public bool IsLightTheme
    {
        get => _selectedThemeMode == ThemeMode.Light;
        set
        {
            if (value && _selectedThemeMode != ThemeMode.Light)
                SelectedThemeMode = ThemeMode.Light;
        }
    }

    /// <summary>
    /// <para>获取或设置是否为深色主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether the dark theme is selected (for RadioButton binding).
    /// </summary>
    public bool IsDarkTheme
    {
        get => _selectedThemeMode == ThemeMode.Dark;
        set
        {
            if (value && _selectedThemeMode != ThemeMode.Dark)
                SelectedThemeMode = ThemeMode.Dark;
        }
    }

    /// <summary>
    /// <para>获取或设置是否跟随系统主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether to follow the system theme (for RadioButton binding).
    /// </summary>
    public bool IsSystemTheme
    {
        get => _selectedThemeMode == ThemeMode.System;
        set
        {
            if (value && _selectedThemeMode != ThemeMode.System)
                SelectedThemeMode = ThemeMode.System;
        }
    }

    /// <summary>
    /// <para>获取或设置是否启用自动保存。</para>
    /// Gets or sets whether auto-save is enabled.
    /// </summary>
    public bool AutoSave
    {
        get => _autoSave;
        set { if (SetProperty(ref _autoSave, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置是否启用调试模式（开启后日志显示 Trace/Debug 级别详细诊断信息）。</para>
    /// Gets or sets whether debug mode is enabled (when on, logs show Trace/Debug level diagnostic details).
    /// </summary>
    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetProperty(ref _debugMode, value))
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
    public SettingsViewModel()
    {
        _appSettings = SettingsPersistenceService.Instance.LoadSettings();

        // 初始化语言列表（从 LanguageService 动态获取）
        Languages = new ObservableCollection<LanguageInfo>(LanguageService.Instance.AvailableLanguages);

        InitializeFromAppSettings(_appSettings);

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
    private void InitializeFromAppSettings(AppSettings settings)
    {
        global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers = settings.FirmwareParallelism;
        // 根据 CultureName 查找或创建 LanguageInfo
        _selectedLanguage = string.IsNullOrEmpty(settings.CultureName)
            ? LanguageInfo.SystemLanguage
            : LanguageService.Instance.AvailableLanguages.FirstOrDefault(l => l.CultureName == settings.CultureName)
              ?? new LanguageInfo(settings.CultureName, settings.CultureName, settings.CultureName);
        _selectedThemeMode = settings.ThemeMode;
        _autoSave = settings.AutoSave;
        _debugMode = settings.DebugMode;
        // 启动恢复设置时同步应用调试模式，否则重启后 Debug 级别日志（含首次机会异常）被 Info 过滤。
        // Apply debug mode when restoring settings at startup; otherwise Debug-level logs
        // (including first-chance exceptions) stay filtered behind Info after a restart.
        LogService.Instance.ApplyDebugMode(_debugMode);

        // Apply language based on loaded preference
        ApplyLanguageInfo(_selectedLanguage);

        // 初始化 MCP 设置：连接列表按条目复制，避免编辑直接改写持久化实例。
        // Initialize the MCP settings: copy the connections so edits never mutate the persisted instance.
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

        _mcpExposeEnabled = settings.McpExposeEnabled;
        _mcpExposePort = settings.McpExposePort is > 0 and <= 65535 ? settings.McpExposePort : AppSettings.DefaultMcpExposePort;
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
        _appSettings.CultureName = _selectedLanguage.IsSystemLanguage ? string.Empty : _selectedLanguage.CultureName;
        _appSettings.ThemeMode = _selectedThemeMode;
        _appSettings.AutoSave = _autoSave;
        _appSettings.DebugMode = _debugMode;

        // 合并磁盘上最新的最近打开项目列表，避免防抖保存覆盖「最近打开」记录。
        // Merge the latest recent-projects list from disk so the debounced save never wipes the MRU record.
        var latest = SettingsPersistenceService.Instance.LoadSettings();
        _appSettings.RecentProjects = latest.RecentProjects;

        // MCP 设置
        _appSettings.McpServers = [.. McpServers];
        _appSettings.McpExposeEnabled = _mcpExposeEnabled;
        _appSettings.McpExposePort = _mcpExposePort;

        var saved = SettingsPersistenceService.Instance.TrySaveSettings(_appSettings, out var error);
        WeakReferenceMessenger.Default.Send(new SettingsSaveCompletedMessage(saved, error));

        // 保存成功后立即按新配置启动/停止内嵌 MCP 服务器；端口占用等失败只记录日志，不影响设置保存。
        // Apply the exposure state right after a successful save; a busy port only logs and never fails the save.
        if (saved)
        {
            McpExposureService.Instance.Apply(_mcpExposeEnabled, _mcpExposePort);
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
    }

    /// <summary>
    /// <para>监听语言包注册中心变更事件，动态更新可用语言列表。</para>
    /// Listens to language pack registry change events and dynamically updates the available languages list.
    /// </summary>
    private void OnLanguagePackChanged(object? sender, LanguagePackChangedEventArgs e)
    {
        // 同步更新语言列表
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
    /// <para>保存 MCP 设置：把连接列表与暴露开关、端口写入应用设置并持久化；保存成功后立即按新配置
    /// 启动、换端口或停止内嵌 MCP 服务器。</para>
    /// Saves the MCP settings: writes the connection list plus the exposure toggle and port into the
    /// application settings and persists them; a successful save immediately starts, re-targets or
    /// stops the embedded MCP server per the new configuration.
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
        McpExposureService.Instance.Apply(_mcpExposeEnabled, _mcpExposePort);

        // 同步语言列表
        Languages.Clear();
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            Languages.Add(lang);
        }

        // Notify all properties changed
        OnPropertyChanged(string.Empty);
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(_selectedThemeMode));
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