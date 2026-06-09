using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System.Globalization;
using UotanStudio.Localization;
using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.Views;

namespace UotanStudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        LocalizationService.Instance.InitializeFromSystemLanguage();
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
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
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        SettingsViewModel.ThemeChanged += OnThemeChanged;
        var savedSettings = SettingsPersistenceService.Instance.LoadSettings();
        ApplyTheme((ThemeMode)savedSettings.SelectedThemeIndex);
    }

    private void OnThemeChanged(object? sender, ThemeMode themeMode)
    {
        ApplyTheme(themeMode);
    }

    private void ApplyTheme(ThemeMode themeMode)
    {
        RequestedThemeVariant = themeMode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.Culture))
        {
            var culture = LocalizationService.Instance.Culture;
            UpdateSemiThemeLocale(culture);
        }
    }

    private void UpdateSemiThemeLocale(CultureInfo culture)
    {
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