using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using System.Globalization;
using UotanStudio.Abstractions.Plugin;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Plugins.BuiltIn;
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
        // 初始化语言服务并应用保存的语言设置，必须在创建 MainViewModel 之前完成，
        // 因为 Dock 布局在构造时会通过 LanguageService.GetString 解析面板标题。
        LanguageService.Instance.Initialize();
        var savedSettings = SettingsPersistenceService.Instance.LoadSettings();
        ApplyTheme(savedSettings.ThemeMode);
        ApplyLanguage(savedSettings.CultureName);

        // 注册内置插件（编译期已知，AOT 安全；零反射）
        RegisterBuiltInPlugins();

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
        WeakReferenceMessenger.Default.Register<ExitApplicationMessage>(this, OnExitApplication);
    }

    /// <summary>
    /// <para>注册所有内置插件工厂委托到 <see cref="PluginRegistry"/>，并触发异步初始化。</para>
    /// Registers all built-in plugin factory delegates into <see cref="PluginRegistry"/>, and triggers async initialization.
    /// </summary>
    /// <remarks>
    /// <para>工厂委托在编译期已知，运行时仅通过 <c>Activator.CreateInstance</c> 等价的委托调用构造实例，全程 AOT 友好、零反射。</para>
    /// The factory delegates are known at compile time; at runtime instances are constructed via delegate invocation equivalent to <c>Activator.CreateInstance</c>, fully AOT-friendly with no reflection.
    /// </remarks>
    private async void RegisterBuiltInPlugins()
    {
        var registry = PluginRegistry.Instance;
        registry.RegisterBuiltIn(() => new FirmwarePlugin());
        registry.RegisterBuiltIn(() => new FlashPlugin());
        registry.RegisterBuiltIn(() => new DataPlugin());
        registry.RegisterBuiltIn(() => new FlowPlugin());
        registry.RegisterBuiltIn(() => new OutputPlugin());

        try
        {
            await registry.InitializeAsync(
                plugin => new DefaultPluginContext(LogService.Instance, plugin.Id),
                System.Threading.CancellationToken.None).ConfigureAwait(false);
            LogService.Instance.Write(Abstractions.Logging.LogLevel.Info, "App",
                $"Plugins initialized: {registry.PluginInfos.Count} plugins, {registry.NodeDefinitions.Count} node definitions");
        }
        catch (System.Exception ex)
        {
            LogService.Instance.Write(Abstractions.Logging.LogLevel.Error, "App",
                $"Plugin initialization failed: {ex.Message}");
        }
    }

    private void OnThemeChanged(object recipient, ThemeChangedMessage message)
    {
        ApplyTheme(message.ThemeMode);
    }

    private void OnLanguageChanged(object recipient, LanguageChangedMessage message)
    {
        UpdateSemiThemeLocale(message.CultureName);
    }

    /// <summary>
    /// <para>响应退出应用程序消息，关闭主窗口并退出。</para>
    /// Responds to the exit-application message by closing the main window and quitting.
    /// </summary>
    private void OnExitApplication(object recipient, ExitApplicationMessage message)
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IControlledApplicationLifetime controlled)
        {
            controlled.Shutdown();
        }
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
