using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>设置视图模型，管理应用程序的通用设置、固件工具设置、烧录设置和节点编辑器设置。</para>
/// Settings view model, managing general settings, firmware tool settings, flash settings, and node editor settings.
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private AppSettings _appSettings;
    private CancellationTokenSource? _saveCts;
    private Timer? _toastTimer;
    private bool _disposed;

    private LanguageInfo _selectedLanguage = LanguageInfo.SystemLanguage;
    private ThemeMode _selectedThemeMode;
    private bool _autoSave;
    private string _unpackToolPath = string.Empty;
    private string _tempDirectory = string.Empty;
    private bool _autoVerifyAfterUnpack = true;
    private string _adbPath = string.Empty;
    private string _fastbootPath = string.Empty;
    private int _deviceTimeoutSeconds = 30;
    private bool _autoBackupBeforeFlash;
    private bool _snapToGrid = true;
    private double _gridSize = 20;
    private int _connectionStyleIndex;
    private bool _isCheckingUpdates;
    private string _updateCheckResult = string.Empty;
    private bool _isToastVisible;
    private string _toastMessage = string.Empty;
    private string? _adbToolStatus;
    private string? _fastbootToolStatus;
    private bool _isCheckingTools;

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

    private List<string> _connectionStyles = BuildConnectionStyles();

    /// <summary>
    /// <para>获取可用连接线样式列表。</para>
    /// Gets the list of available connection line styles.
    /// </summary>
    public List<string> ConnectionStyles
    {
        get => _connectionStyles;
        private set => SetProperty(ref _connectionStyles, value);
    }

    /// <summary>
    /// <para>根据当前语言构建连接线样式列表。</para>
    /// Builds the connection line styles list based on the current language.
    /// </summary>
    private static List<string> BuildConnectionStyles() =>
        [LanguageService.Instance.GetString("Settings_ConnBezier"),
         LanguageService.Instance.GetString("Settings_ConnLine"),
         LanguageService.Instance.GetString("Settings_ConnStep")];

    /// <summary>
    /// <para>获取语言预览文本，根据当前语言动态更新。</para>
    /// Gets the language preview text, dynamically updated based on the current language.
    /// </summary>
    public string LanguagePreviewText => LanguageService.Instance.GetString("Settings_LanguagePreviewText");

    /// <summary>
    /// <para>获取固件解包模块版本号。</para>
    /// Gets the firmware unpack module version.
    /// </summary>
    public string FirmwareModuleVersion { get; } = "1.0.0";

    /// <summary>
    /// <para>获取烧录模块版本号。</para>
    /// Gets the board flash module version.
    /// </summary>
    public string FlashModuleVersion { get; } = "1.0.0";

    /// <summary>
    /// <para>获取可视化编程模块版本号。</para>
    /// Gets the visual programming module version.
    /// </summary>
    public string VisualProgrammingModuleVersion { get; } = "1.0.0";

    /// <summary>
    /// <para>获取版本信息卡片列表，供设置页 WrapLayout 呈现。</para>
    /// Gets the list of module version cards for the settings page WrapLayout.
    /// </summary>
    public ObservableCollection<ModuleCardInfo> ModuleCards { get; } = [];

    /// <summary>
    /// <para>根据当前语言刷新版本信息卡片列表（名称本地化）。</para>
    /// Refreshes the module version cards list based on the current language (localized names).
    /// </summary>
    private void RefreshModuleCards()
    {
        ModuleCards.Clear();
        ModuleCards.Add(new ModuleCardInfo(
            LanguageService.Instance.GetString("Settings_FirmwareModule"),
            FirmwareModuleVersion));
        ModuleCards.Add(new ModuleCardInfo(
            LanguageService.Instance.GetString("Settings_FlashModule"),
            FlashModuleVersion));
        ModuleCards.Add(new ModuleCardInfo(
            LanguageService.Instance.GetString("Settings_VisualProgrammingModule"),
            VisualProgrammingModuleVersion));
    }

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
    /// <para>获取或设置默认解包工具路径。</para>
    /// Gets or sets the default unpack tool path.
    /// </summary>
    public string UnpackToolPath
    {
        get => _unpackToolPath;
        set { if (SetProperty(ref _unpackToolPath, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置临时文件目录。</para>
    /// Gets or sets the temporary file directory.
    /// </summary>
    public string TempDirectory
    {
        get => _tempDirectory;
        set { if (SetProperty(ref _tempDirectory, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置解包后是否自动校验。</para>
    /// Gets or sets whether to auto-verify after unpacking.
    /// </summary>
    public bool AutoVerifyAfterUnpack
    {
        get => _autoVerifyAfterUnpack;
        set { if (SetProperty(ref _autoVerifyAfterUnpack, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置 ADB 路径。</para>
    /// Gets or sets the ADB path.
    /// </summary>
    public string AdbPath
    {
        get => _adbPath;
        set { if (SetProperty(ref _adbPath, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置 Fastboot 路径。</para>
    /// Gets or sets the Fastboot path.
    /// </summary>
    public string FastbootPath
    {
        get => _fastbootPath;
        set { if (SetProperty(ref _fastbootPath, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置设备超时时间（秒）。</para>
    /// Gets or sets the device timeout in seconds.
    /// </summary>
    public int DeviceTimeoutSeconds
    {
        get => _deviceTimeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 5, 300);
            if (SetProperty(ref _deviceTimeoutSeconds, clamped))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置烧录前是否自动备份。</para>
    /// Gets or sets whether to auto-backup before flashing.
    /// </summary>
    public bool AutoBackupBeforeFlash
    {
        get => _autoBackupBeforeFlash;
        set { if (SetProperty(ref _autoBackupBeforeFlash, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置是否启用网格对齐。</para>
    /// Gets or sets whether snap-to-grid is enabled.
    /// </summary>
    public bool SnapToGrid
    {
        get => _snapToGrid;
        set { if (SetProperty(ref _snapToGrid, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置网格大小。</para>
    /// Gets or sets the grid size.
    /// </summary>
    public double GridSize
    {
        get => _gridSize;
        set { if (SetProperty(ref _gridSize, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置连接线样式索引。</para>
    /// Gets or sets the connection line style index.
    /// </summary>
    public int ConnectionStyleIndex
    {
        get => _connectionStyleIndex;
        set { if (SetProperty(ref _connectionStyleIndex, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置是否正在检查更新。</para>
    /// Gets or sets whether an update check is in progress.
    /// </summary>
    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        set => SetProperty(ref _isCheckingUpdates, value);
    }

    /// <summary>
    /// <para>获取或设置更新检查结果消息。</para>
    /// Gets or sets the update check result message.
    /// </summary>
    public string UpdateCheckResult
    {
        get => _updateCheckResult;
        set => SetProperty(ref _updateCheckResult, value);
    }

    /// <summary>
    /// <para>获取或设置 Toast 通知是否可见。</para>
    /// Gets or sets whether the toast notification is visible.
    /// </summary>
    public bool IsToastVisible
    {
        get => _isToastVisible;
        set => SetProperty(ref _isToastVisible, value);
    }

    /// <summary>
    /// <para>获取或设置 Toast 通知消息内容。</para>
    /// Gets or sets the toast notification message content.
    /// </summary>
    public string ToastMessage
    {
        get => _toastMessage;
        set => SetProperty(ref _toastMessage, value);
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

        // 初始化完成后通知 UI 刷新主题相关 RadioButton 绑定
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));

        // 初始化版本信息卡片（本地化名称）
        RefreshModuleCards();
        OnPropertyChanged(nameof(IsDarkTheme));

        // 显式发送一次主题变更消息，确保 App 与 VM 状态同步
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(_selectedThemeMode));

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
        // 直接赋值私有字段以避免触发 setter 中的防抖保存
        _selectedLanguage = string.IsNullOrEmpty(settings.CultureName)
            ? LanguageInfo.SystemLanguage
            : LanguageService.Instance.AvailableLanguages.FirstOrDefault(l => l.CultureName == settings.CultureName)
              ?? new LanguageInfo(settings.CultureName, settings.CultureName, settings.CultureName);
        _selectedThemeMode = settings.ThemeMode;
        _autoSave = settings.AutoSave;
        _unpackToolPath = settings.UnpackToolPath;
        _tempDirectory = settings.TempDirectory;
        _autoVerifyAfterUnpack = settings.AutoVerifyAfterUnpack;
        _adbPath = settings.AdbPath;
        _fastbootPath = settings.FastbootPath;
        _deviceTimeoutSeconds = Math.Clamp(settings.DeviceTimeoutSeconds, 5, 300);
        _autoBackupBeforeFlash = settings.AutoBackupBeforeFlash;
        _snapToGrid = settings.SnapToGrid;
        _gridSize = settings.GridSize;
        _connectionStyleIndex = settings.ConnectionStyleIndex;

        // 统一通知 UI 刷新所有绑定属性
        OnPropertyChanged(string.Empty);

        // Apply language based on loaded preference
        ApplyLanguageInfo(_selectedLanguage);
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
        _appSettings.UnpackToolPath = _unpackToolPath;
        _appSettings.TempDirectory = _tempDirectory;
        _appSettings.AutoVerifyAfterUnpack = _autoVerifyAfterUnpack;
        _appSettings.AdbPath = _adbPath;
        _appSettings.FastbootPath = _fastbootPath;
        _appSettings.DeviceTimeoutSeconds = _deviceTimeoutSeconds;
        _appSettings.AutoBackupBeforeFlash = _autoBackupBeforeFlash;
        _appSettings.SnapToGrid = _snapToGrid;
        _appSettings.GridSize = _gridSize;
        _appSettings.ConnectionStyleIndex = _connectionStyleIndex;

        SettingsPersistenceService.Instance.SaveSettings(_appSettings);

        ShowToast(LanguageService.Instance.GetString("Settings_SettingsSaved"));
    }

    /// <summary>
    /// <para>显示 Toast 通知，2 秒后自动隐藏。</para>
    /// Shows a toast notification that automatically hides after 2 seconds.
    /// </summary>
    /// <param name="message">
    /// <para>要显示的通知消息。</para>
    /// The notification message to display.
    /// </param>
    private void ShowToast(string message)
    {
        _toastTimer?.Dispose();

        ToastMessage = message;
        IsToastVisible = true;

        _toastTimer = new Timer(_ =>
        {
            IsToastVisible = false;
            _toastTimer?.Dispose();
            _toastTimer = null;
        }, null, 2000, Timeout.Infinite);
    }

    /// <summary>
    /// <para>监听语言服务变更事件，更新语言相关属性。</para>
    /// Listens to language service change events and updates language-related properties.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguagePreviewText));
        // 重建连接线样式列表以反映新语言文本
        ConnectionStyles = BuildConnectionStyles();
        // 刷新版本卡片以反映新语言名称
        RefreshModuleCards();
    }

    /// <summary>
    /// <para>监听语言包注册中心变更事件，动态更新可用语言列表。</para>
    /// Listens to language pack registry change events and dynamically updates the available languages list.
    /// </summary>
    private void OnLanguagePackChanged(object? sender, LanguagePackChangedEventArgs e)
    {
        // 同步更新语言列表，保留"跟随系统"头部
        var currentSelected = _selectedLanguage;
        Languages.Clear();
        Languages.Add(LanguageInfo.SystemLanguage);
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            if (!lang.IsSystemLanguage)
                Languages.Add(lang);
        }

        // 若当前选中语言已被卸载，回退到"跟随系统"
        if (!currentSelected.IsSystemLanguage &&
            !Languages.Any(l => l.CultureName == currentSelected.CultureName))
        {
            SelectedLanguage = LanguageInfo.SystemLanguage;
        }
    }

    /// <summary>
    /// <para>异步检查更新，模拟网络请求后显示结果。</para>
    /// Asynchronously checks for updates, simulating a network request before displaying the result.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdates = true;
        UpdateCheckResult = LanguageService.Instance.GetString("Settings_CheckingUpdates");

        try
        {
            await Task.Delay(1000);
            UpdateCheckResult = LanguageService.Instance.GetString("Settings_UpToDate");
        }
        catch (Exception)
        {
            UpdateCheckResult = LanguageService.Instance.GetString("Settings_UpdateCheckFailed");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    /// <summary>
    /// <para>获取或设置 ADB 工具可用性检测结果文本。</para>
    /// Gets or sets the ADB tool availability check result text.
    /// </summary>
    public string? AdbToolStatus
    {
        get => _adbToolStatus;
        set => SetProperty(ref _adbToolStatus, value);
    }

    /// <summary>
    /// <para>获取或设置 Fastboot 工具可用性检测结果文本。</para>
    /// Gets or sets the Fastboot tool availability check result text.
    /// </summary>
    public string? FastbootToolStatus
    {
        get => _fastbootToolStatus;
        set => SetProperty(ref _fastbootToolStatus, value);
    }

    /// <summary>
    /// <para>获取或设置是否正在检测工具可用性。</para>
    /// Gets or sets whether tool availability check is in progress.
    /// </summary>
    public bool IsCheckingTools
    {
        get => _isCheckingTools;
        set => SetProperty(ref _isCheckingTools, value);
    }

    /// <summary>
    /// <para>异步检测 ADB 与 Fastboot 工具可用性，更新状态文本。</para>
    /// Asynchronously verifies ADB and Fastboot tool availability, updating status text.
    /// <para>调用 AdbService 实例的 GetDevicesAsync，成功即工具可达。</para>
    /// Invokes AdbService.Instance.GetDevicesAsync; success means the tool is reachable.
    /// </summary>
    [RelayCommand]
    private async Task VerifyToolPathsAsync()
    {
        if (IsCheckingTools) return;
        IsCheckingTools = true;
        AdbToolStatus = LanguageService.Instance.GetString("Settings_Checking");
        FastbootToolStatus = LanguageService.Instance.GetString("Settings_Checking");

        try
        {
            // 同步当前路径配置到 AdbService 单例
            AdbService.Instance.UpdateConfiguration(AdbPath, FastbootPath, DeviceTimeoutSeconds);

            // ADB 可用性：枚举设备命令成功即工具可达
            var adbResult = await AdbService.Instance.GetDevicesAsync();
            AdbToolStatus = string.IsNullOrEmpty(AdbPath)
                ? $"{LanguageService.Instance.GetString("Settings_ToolDefault")}: {adbResult.Count} device(s)"
                : $"{LanguageService.Instance.GetString("Settings_ToolAvailable")}: {adbResult.Count} device(s)";

            // Fastboot 可用性：直接尝试 fastboot devices 等价命令（通过 ExecuteFastbootAsync）
            var fbResult = await AdbService.Instance.ExecuteFastbootAsync(string.Empty, "devices", System.Threading.CancellationToken.None);
            FastbootToolStatus = fbResult.Success
                ? LanguageService.Instance.GetString("Settings_ToolAvailable")
                : (string.IsNullOrEmpty(FastbootPath)
                    ? LanguageService.Instance.GetString("Settings_ToolDefault")
                    : LanguageService.Instance.GetString("Settings_ToolUnavailable"));
        }
        catch (System.Exception ex)
        {
            AdbToolStatus = $"{LanguageService.Instance.GetString("Settings_ToolUnavailable")}: {ex.Message}";
            FastbootToolStatus = LanguageService.Instance.GetString("Settings_ToolUnavailable");
        }
        finally
        {
            IsCheckingTools = false;
        }
    }

    /// <summary>
    /// <para>异步重置设置为默认值，需通过确认对话框二次确认。</para>
    /// Asynchronously resets settings to defaults, requiring confirmation via a dialog.
    /// <para>通过 ResetToDefaultsRequested 事件触发 UI 层弹出确认对话框；用户确认后调用 ResetToDefaults。</para>
    /// Triggers the UI layer confirmation dialog via the ResetToDefaultsRequested event; calls ResetToDefaults after user confirmation.
    /// </summary>
    [RelayCommand]
    private async Task ResetToDefaultsWithConfirmAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        ResetToDefaultsRequested?.Invoke(this, tcs);
        var confirmed = await tcs.Task;
        if (!confirmed)
        {
            ShowToast(LanguageService.Instance.GetString("Settings_ResetCanceled"));
            return;
        }
        ResetToDefaultsCore();
    }

    /// <summary>
    /// <para>重置确认请求事件，由 UI 层（SettingsView）订阅以弹出确认对话框。</para>
    /// Reset confirmation request event, subscribed by the UI layer (SettingsView) to pop a confirmation dialog.
    /// </summary>
    public event EventHandler<TaskCompletionSource<bool>>? ResetToDefaultsRequested;

    /// <summary>
    /// <para>重置所有设置为默认值，重新加载并通知主题变更。</para>
    /// Resets all settings to default values, reloads them, and notifies of theme changes.
    /// </summary>
    private void ResetToDefaultsCore()
    {
        _appSettings = SettingsPersistenceService.Instance.ResetToDefaults();
        InitializeFromAppSettings(_appSettings);

        // 通知主题相关 RadioButton 绑定刷新
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));

        // 同步语言列表，保留"跟随系统"头部
        Languages.Clear();
        Languages.Add(LanguageInfo.SystemLanguage);
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            if (!lang.IsSystemLanguage)
                Languages.Add(lang);
        }

        // 实际应用重置后的语言（默认为跟随系统）
        ApplyLanguageInfo(_selectedLanguage);

        // Notify all properties changed
        OnPropertyChanged(string.Empty);
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(_selectedThemeMode));

        // 刷新版本卡片以反映当前语言
        RefreshModuleCards();

        ShowToast(LanguageService.Instance.GetString("Settings_SettingsReset"));
    }

    /// <summary>
    /// <para>重置所有设置为默认值，重新加载并通知主题变更。</para>
    /// Resets all settings to default values, reloads them, and notifies of theme changes.
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        _appSettings = SettingsPersistenceService.Instance.ResetToDefaults();
        InitializeFromAppSettings(_appSettings);

        // 通知主题相关 RadioButton 绑定刷新
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));

        // 同步语言列表，保留"跟随系统"头部
        Languages.Clear();
        Languages.Add(LanguageInfo.SystemLanguage);
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            if (!lang.IsSystemLanguage)
                Languages.Add(lang);
        }

        // 实际应用重置后的语言（默认为跟随系统）
        ApplyLanguageInfo(_selectedLanguage);

        // Notify all properties changed
        OnPropertyChanged(string.Empty);
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(_selectedThemeMode));

        ShowToast(LanguageService.Instance.GetString("Settings_SettingsSaved"));
    }

    /// <summary>
    /// <para>释放所有资源，包括取消令牌源、定时器和事件订阅。</para>
    /// Releases all resources, including cancellation token sources, timers, and event subscriptions.
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
            _toastTimer?.Dispose();
            LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
            LanguagePackRegistry.Instance.LanguagePackChanged -= OnLanguagePackChanged;
        }

        _disposed = true;
    }
}

/// <summary>
/// <para>模块版本信息卡片，供设置页 WrapLayout 呈现。</para>
/// Module version info card for the settings page WrapLayout.
/// </summary>
public sealed class ModuleCardInfo
{
    /// <summary>模块本地化名称。</summary>
    public string Name { get; }
    /// <summary>模块版本号。</summary>
    public string Version { get; }

    /// <summary>
    /// <para>创建模块版本信息卡片。</para>
    /// Creates a module version info card.
    /// </summary>
    /// <param name="name">模块本地化名称。</param>
    /// <param name="version">模块版本号。</param>
    public ModuleCardInfo(string name, string version)
    {
        Name = name ?? string.Empty;
        Version = version ?? string.Empty;
    }
}
