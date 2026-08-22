using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using System.Globalization;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.Views;

namespace UotanStudio;

/// <summary>
/// The main application class.
/// <para>主应用程序类。</para>
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Initializes the application by loading the XAML.
    /// <para>通过加载 XAML 初始化应用程序。</para>
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called when the framework initialization is completed.
    /// <para>当框架初始化完成时调用。</para>
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();

        // Register messenger receivers
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, OnThemeChanged);
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);

        // Load and apply saved settings
        // 初始化语言服务，自动发现内置语言包
        LanguageService.Instance.Initialize();
        AppSettings savedSettings = SettingsPersistenceService.Instance.LoadSettings();
        ApplyTheme(savedSettings.ThemeMode);
        ApplyLanguage(savedSettings.CultureName);
    }

    /// <summary>
    /// Handles theme change messages.
    /// <para>处理主题更改消息。</para>
    /// </summary>
    private void OnThemeChanged(object recipient, ThemeChangedMessage message) => ApplyTheme(message.ThemeMode);

    /// <summary>
    /// Handles language change messages.
    /// <para>处理语言更改消息。</para>
    /// </summary>
    private void OnLanguageChanged(object recipient, LanguageChangedMessage message) => UpdateSemiThemeLocale(message.CultureName);

    /// <summary>
    /// Applies the specified theme mode.
    /// <para>应用指定的主题模式。</para>
    /// </summary>
    /// <param name="themeMode">The theme mode to apply.</param>
    private void ApplyTheme(ThemeMode themeMode)
    {
        RequestedThemeVariant = themeMode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        // 应用自定义主题颜色：跟随系统时根据实际主题变体动态选择
        string themeKey;
        if (themeMode == ThemeMode.System)
        {
            ThemeVariant actualVariant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Default;
            themeKey = actualVariant == ThemeVariant.Dark ? "DarkColors" : "LightColors";
        }
        else
        {
            themeKey = themeMode == ThemeMode.Dark ? "DarkColors" : "LightColors";
        }
        LanguageService.Instance.ApplyTheme(themeKey);
    }

    /// <summary>
    /// Applies the specified language culture.
    /// <para>应用指定的语言文化。</para>
    /// </summary>
    /// <param name="cultureName">The culture name to apply.</param>
    private void ApplyLanguage(string cultureName)
    {
        if (string.IsNullOrEmpty(cultureName))
        {
            LanguageService.Instance.InitializeFromSystemLanguage();
        }
        else
        {
            LanguageService.Instance.ApplyLanguage(cultureName);
        }
    }

    /// <summary>
    /// Updates the Semi theme locale for the specified culture.
    /// <para>更新指定文化的 Semi 主题区域设置。</para>
    /// </summary>
    /// <param name="cultureName">The culture name to set.</param>
    private void UpdateSemiThemeLocale(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        foreach (IStyle style in this.Styles)
        {
            switch (style)
            {
                case Semi.Avalonia.SemiTheme semiTheme:
                    semiTheme.Locale = culture;
                    break;
                case Ursa.Themes.Semi.UrsaSemiTheme ursaTheme:
                    ursaTheme.Locale = culture;
                    break;
            }
        }
    }
}
