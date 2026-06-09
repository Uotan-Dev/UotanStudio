using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Localization;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>主题模式枚举，定义应用程序可用的主题选项。</para>
/// Theme mode enum, defining the available theme options for the application.
/// </summary>
public enum ThemeMode
{
    /// <summary>
    /// <para>跟随系统主题。</para>
    /// Follow the system theme.
    /// </summary>
    FollowSystem = 0,

    /// <summary>
    /// <para>浅色主题。</para>
    /// Light theme.
    /// </summary>
    Light = 1,

    /// <summary>
    /// <para>深色主题。</para>
    /// Dark theme.
    /// </summary>
    Dark = 2
}

/// <summary>
/// <para>设置视图模型，管理应用程序的通用设置、固件工具设置、烧录设置和节点编辑器设置。</para>
/// Settings view model, managing general settings, firmware tool settings, flash settings, and node editor settings.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private AppSettings _appSettings;
    private CancellationTokenSource? _saveCts;
    private Timer? _toastTimer;

    private int _selectedLanguageIndex;
    private ThemeMode _selectedTheme;
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

    /// <summary>
    /// <para>当主题模式变更时触发，供外部订阅以应用主题切换。</para>
    /// Fired when the theme mode changes, for external subscribers to apply theme switching.
    /// </summary>
    public static event EventHandler<ThemeMode>? ThemeChanged;

    /// <summary>
    /// <para>获取可用语言列表，首项为"自动检测"选项。</para>
    /// Gets the list of available languages, with "Auto Detect" as the first item.
    /// </summary>
    public List<LanguageOption> Languages { get; }

    /// <summary>
    /// <para>获取可用主题列表（仅用于显示）。</para>
    /// Gets the list of available themes (for display only).
    /// </summary>
    public List<string> Themes { get; }

    /// <summary>
    /// <para>获取可用连接线样式列表。</para>
    /// Gets the list of available connection line styles.
    /// </summary>
    public List<string> ConnectionStyles { get; }

    /// <summary>
    /// <para>获取语言预览文本，根据当前语言动态更新。</para>
    /// Gets the language preview text, dynamically updated based on the current language.
    /// </summary>
    public string LanguagePreviewText => LocalizationService.Instance["Settings_LanguagePreviewText"];

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
    /// <para>获取或设置选中的语言索引。0=自动检测，1=English，2=简体中文。</para>
    /// Gets or sets the selected language index. 0=Auto Detect, 1=English, 2=简体中文.
    /// </summary>
    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set
        {
            if (SetProperty(ref _selectedLanguageIndex, value) && value >= 0 && value < Languages.Count)
            {
                if (value == 0)
                {
                    LocalizationService.Instance.InitializeFromSystemLanguage();
                }
                else
                {
                    LocalizationService.Instance.Culture = new CultureInfo(Languages[value].Code);
                }

                OnPropertyChanged(nameof(LanguagePreviewText));
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置选中的主题模式。</para>
    /// Gets or sets the selected theme mode.
    /// </summary>
    public ThemeMode SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                OnPropertyChanged(nameof(SelectedThemeIndex));
                ThemeChanged?.Invoke(this, value);
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置选中的主题索引，用于界面绑定。与 <see cref="SelectedTheme"/> 保持同步。</para>
    /// Gets or sets the selected theme index, for UI binding. Synchronized with <see cref="SelectedTheme"/>.
    /// </summary>
    public int SelectedThemeIndex
    {
        get => (int)_selectedTheme;
        set => SelectedTheme = (ThemeMode)value;
    }

    /// <summary>
    /// <para>获取或设置是否启用自动保存。</para>
    /// Gets or sets whether auto-save is enabled.
    /// </summary>
    public bool AutoSave
    {
        get => _autoSave;
        set
        {
            if (SetProperty(ref _autoSave, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置默认解包工具路径。</para>
    /// Gets or sets the default unpack tool path.
    /// </summary>
    public string UnpackToolPath
    {
        get => _unpackToolPath;
        set
        {
            if (SetProperty(ref _unpackToolPath, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置临时文件目录。</para>
    /// Gets or sets the temporary file directory.
    /// </summary>
    public string TempDirectory
    {
        get => _tempDirectory;
        set
        {
            if (SetProperty(ref _tempDirectory, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置解包后是否自动校验。</para>
    /// Gets or sets whether to auto-verify after unpacking.
    /// </summary>
    public bool AutoVerifyAfterUnpack
    {
        get => _autoVerifyAfterUnpack;
        set
        {
            if (SetProperty(ref _autoVerifyAfterUnpack, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置 ADB 路径。</para>
    /// Gets or sets the ADB path.
    /// </summary>
    public string AdbPath
    {
        get => _adbPath;
        set
        {
            if (SetProperty(ref _adbPath, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置 Fastboot 路径。</para>
    /// Gets or sets the Fastboot path.
    /// </summary>
    public string FastbootPath
    {
        get => _fastbootPath;
        set
        {
            if (SetProperty(ref _fastbootPath, value))
                DebouncedSaveSettings();
        }
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
            if (SetProperty(ref _deviceTimeoutSeconds, value))
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
        set
        {
            if (SetProperty(ref _autoBackupBeforeFlash, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置是否启用网格对齐。</para>
    /// Gets or sets whether snap-to-grid is enabled.
    /// </summary>
    public bool SnapToGrid
    {
        get => _snapToGrid;
        set
        {
            if (SetProperty(ref _snapToGrid, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置网格大小。</para>
    /// Gets or sets the grid size.
    /// </summary>
    public double GridSize
    {
        get => _gridSize;
        set
        {
            if (SetProperty(ref _gridSize, value))
                DebouncedSaveSettings();
        }
    }

    /// <summary>
    /// <para>获取或设置连接线样式索引。</para>
    /// Gets or sets the connection line style index.
    /// </summary>
    public int ConnectionStyleIndex
    {
        get => _connectionStyleIndex;
        set
        {
            if (SetProperty(ref _connectionStyleIndex, value))
                DebouncedSaveSettings();
        }
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
        Languages =
        [
            new LanguageOption(LocalizationService.Instance["Settings_AutoDetect"], "auto"),
            new LanguageOption("English", "en-US"),
            new LanguageOption("简体中文", "zh-CN")
        ];

        Themes =
        [
            LocalizationService.Instance["Settings_ThemeFollowSystem"],
            LocalizationService.Instance["Settings_ThemeLightMode"],
            LocalizationService.Instance["Settings_ThemeDarkMode"]
        ];

        ConnectionStyles =
        [
            LocalizationService.Instance["Settings_ConnBezier"],
            LocalizationService.Instance["Settings_ConnLine"],
            LocalizationService.Instance["Settings_ConnStep"]
        ];

        _appSettings = SettingsPersistenceService.Instance.LoadSettings();
        InitializeFromAppSettings(_appSettings);

        LocalizationService.Instance.PropertyChanged += OnLocalizationServicePropertyChanged;
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
        _selectedLanguageIndex = settings.SelectedLanguageIndex;
        _selectedTheme = (ThemeMode)settings.SelectedThemeIndex;
        _autoSave = settings.AutoSave;
        _unpackToolPath = settings.UnpackToolPath;
        _tempDirectory = settings.TempDirectory;
        _autoVerifyAfterUnpack = settings.AutoVerifyAfterUnpack;
        _adbPath = settings.AdbPath;
        _fastbootPath = settings.FastbootPath;
        _deviceTimeoutSeconds = settings.DeviceTimeoutSeconds;
        _autoBackupBeforeFlash = settings.AutoBackupBeforeFlash;
        _snapToGrid = settings.SnapToGrid;
        _gridSize = settings.GridSize;
        _connectionStyleIndex = settings.ConnectionStyleIndex;

        // Apply language based on loaded index
        if (_selectedLanguageIndex == 0)
        {
            LocalizationService.Instance.InitializeFromSystemLanguage();
        }
        else if (_selectedLanguageIndex > 0 && _selectedLanguageIndex < Languages.Count)
        {
            LocalizationService.Instance.Culture = new CultureInfo(Languages[_selectedLanguageIndex].Code);
        }
    }

    /// <summary>
    /// <para>使用防抖策略延迟保存设置，在 500 毫秒无变更后执行持久化。</para>
    /// Saves settings with a debounced strategy, persisting after 500ms of no changes.
    /// </summary>
    private void DebouncedSaveSettings()
    {
        _saveCts?.Cancel();

        _saveCts = new CancellationTokenSource();
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
        _appSettings.SelectedLanguageIndex = _selectedLanguageIndex;
        _appSettings.SelectedThemeIndex = (int)_selectedTheme;
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

        ShowToast(LocalizationService.Instance["Settings_SettingsSaved"]);
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
    /// <para>监听本地化服务属性变更，更新语言预览文本。</para>
    /// Listens to localization service property changes and updates the language preview text.
    /// </summary>
    private void OnLocalizationServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.Culture))
        {
            OnPropertyChanged(nameof(LanguagePreviewText));
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
        UpdateCheckResult = LocalizationService.Instance["Settings_CheckingUpdates"];

        try
        {
            await Task.Delay(1000);
            UpdateCheckResult = LocalizationService.Instance["Settings_UpToDate"];
        }
        catch (Exception)
        {
            UpdateCheckResult = LocalizationService.Instance["Settings_UpdateCheckFailed"];
        }
        finally
        {
            IsCheckingUpdates = false;
        }
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

        // Notify all properties changed
        OnPropertyChanged(string.Empty);
        ThemeChanged?.Invoke(this, _selectedTheme);
    }
}

/// <summary>
/// <para>语言选项，包含显示名称和语言代码。</para>
/// Language option, containing display name and language code.
/// </summary>
public class LanguageOption
{
    /// <summary>
    /// <para>获取语言显示名称。</para>
    /// Gets the language display name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>获取语言代码。</para>
    /// Gets the language code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// <para>创建语言选项。</para>
    /// Creates a language option.
    /// </summary>
    /// <param name="name">
    /// <para>语言显示名称。</para>
    /// The language display name.
    /// </param>
    /// <param name="code">
    /// <para>语言代码。</para>
    /// The language code.
    /// </param>
    public LanguageOption(string name, string code)
    {
        Name = name;
        Code = code;
    }

    /// <summary>
    /// <para>返回语言显示名称。</para>
    /// Returns the language display name.
    /// </summary>
    public override string ToString() => Name;
}
