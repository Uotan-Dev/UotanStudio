using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
    private bool _isToastVisible;
    private string _toastMessage = string.Empty;

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
        // 根据 CultureName 查找或创建 LanguageInfo
        _selectedLanguage = string.IsNullOrEmpty(settings.CultureName)
            ? LanguageInfo.SystemLanguage
            : LanguageService.Instance.AvailableLanguages.FirstOrDefault(l => l.CultureName == settings.CultureName)
              ?? new LanguageInfo(settings.CultureName, settings.CultureName, settings.CultureName);
        _selectedThemeMode = settings.ThemeMode;
        _autoSave = settings.AutoSave;

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

        // 合并磁盘上最新的最近打开项目列表，避免防抖保存覆盖「最近打开」记录。
        // Merge the latest recent-projects list from disk so the debounced save never wipes the MRU record.
        var latest = SettingsPersistenceService.Instance.LoadSettings();
        _appSettings.RecentProjects = latest.RecentProjects;

        SettingsPersistenceService.Instance.SaveSettings(_appSettings);

        ShowToast(LanguageService.Instance.GetString("Settings_SettingsSaved"));
    }

    /// <summary>
    /// <para>显示 Toast 通知，2 秒后在 UI 线程上自动隐藏。</para>
    /// Shows a toast notification that automatically hides after 2 seconds on the UI thread.
    /// </summary>
    /// <param name="message">
    /// <para>要显示的通知消息。</para>
    /// The notification message to display.
    /// </param>
    private void ShowToast(string message)
    {
        // 取消上一次的 Toast 计时，避免遗留回调造成抖动
        _toastTimer?.Dispose();

        // Toast 是一个非模态覆盖层，设置可见性后立即返回，不阻塞调用方
        ToastMessage = message;
        IsToastVisible = true;

        // Toast 计时器回调在 UI 线程上执行隐藏操作，避免跨线程访问绑定属性。
        // 先把当前计时器存入字段，再在回调里比对引用，防止并发 ShowToast 时
        // 旧的 Dispatcher.Post 回调误将新计时器置空。
        Timer? createdTimer = null;
        createdTimer = new Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // 仅当仍然指向同一计时器时才隐藏，防止竞态条件下覆盖较新的 Toast
                if (ReferenceEquals(_toastTimer, createdTimer))
                {
                    IsToastVisible = false;
                    _toastTimer?.Dispose();
                    _toastTimer = null;
                }
            });
        }, null, 2000, Timeout.Infinite);
        _toastTimer = createdTimer;
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
    /// <para>重置所有设置为默认值，重新加载并通知主题变更。</para>
    /// Resets all settings to default values, reloads them, and notifies of theme changes.
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        _appSettings = SettingsPersistenceService.Instance.ResetToDefaults();
        InitializeFromAppSettings(_appSettings);

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