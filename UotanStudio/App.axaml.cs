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

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

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
        var savedSettings = SettingsPersistenceService.Instance.LoadSettings();
        ApplyTheme(savedSettings.ThemeMode);
        ApplyLanguage(savedSettings.CultureName);
    }

    private void OnThemeChanged(object recipient, ThemeChangedMessage message)
    {
        ApplyTheme(message.ThemeMode);
    }

    private void OnLanguageChanged(object recipient, LanguageChangedMessage message)
    {
        UpdateSemiThemeLocale(message.CultureName);
    }

    private void ApplyTheme(ThemeMode themeMode)
    {
        RequestedThemeVariant = themeMode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        // Apply custom theme colors
        var themeKey = themeMode == ThemeMode.Dark ? "DarkColors" : "LightColors";
        LanguageService.Instance.ApplyTheme(themeKey);
    }

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

    private void UpdateSemiThemeLocale(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        foreach (var style in this.Styles)
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
