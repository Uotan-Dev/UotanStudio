using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Called when the framework initialization is completed.
    /// <para>当框架初始化完成时调用。</para>
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // 将 Comm 层 USB 诊断桥接到统一日志服务（输出受 FIRMWAREKIT_USB_DEBUG=1 控制），
        // 并记录应用启动日志（历史缓冲由 LogService 保留，日志面板订阅时回填）。
        UsbLogBridge.Install();
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");

        // 仅初始化语言包注册表（纯内存操作，快速）；持久化设置的读取与主题/语言 XAML
        // 加载推迟到启动序列中执行，使启动窗能立刻显示，避免首帧出现前的同步阻塞。
        // Only initialize the language pack registry (in-memory, fast); persisted settings
        // and theme/language XAML loading are deferred to the startup sequence so the splash
        // window can appear immediately without synchronous blocking before its first frame.
        LanguageService.Instance.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 桌面端：立即显示 Ursa 主题启动窗（状态文本留空，语言应用后由启动序列填充），
            // 启动序列（应用设置 + 首次全局 USB 扫描 + 预构建主窗口）完成后关闭，
            // 由 SplashWindow 过渡到主窗口。
            // Desktop: show the Ursa-themed splash right away (status text left empty until the
            // language is applied); once the startup sequence (settings + initial full USB scan
            // + pre-built main window) completes, the splash closes and transitions to the main window.
            var splash = new MainSplashWindow
            {
                DataContext = new SplashViewModel(string.Empty)
            };
            desktop.MainWindow = splash;
            splash.Show();
            splash.StartupTask = RunStartupSequenceAsync(splash, (SplashViewModel)splash.DataContext);
            // Close 触发 Ursa SplashWindow 的 CanClose（等待启动序列）→ CreateNextWindow 过渡。
            // Close triggers the Ursa SplashWindow's CanClose (awaiting the startup sequence)
            // and then transitions via CreateNextWindow.
            splash.Close();
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
    }

    /// <summary>
    /// <para>启动序列：应用持久化设置（主题/语言）、启动设备监视并执行首次全局 USB 扫描，
    /// 期间更新启动窗状态文本；扫描完成后预构建主窗口。完成后由调用方关闭启动窗
    /// （<see cref="MainSplashWindow.CanClose"/> 等待本任务）。</para>
    /// Startup sequence: applies persisted settings (theme/language), starts the device monitor
    /// and performs the initial full USB scan, updating the splash status text along the way;
    /// the main window is pre-built once the scan completes. The caller closes the splash when
    /// done (<see cref="MainSplashWindow.CanClose"/> awaits this task).
    /// </summary>
    /// <param name="splashWindow">The splash window receiving the pre-built main window. <para>接收预构建主窗口的启动窗。</para></param>
    /// <param name="splash">The splash view model receiving status updates. <para>接收状态更新的启动窗视图模型。</para></param>
    private async Task RunStartupSequenceAsync(MainSplashWindow splashWindow, SplashViewModel splash)
    {
        // 1) 后台读取持久化设置（文件 I/O 不应阻塞 UI 线程）。
        // 1) Load persisted settings on a background thread (file I/O must not block the UI).
        var settings = await Task.Run(() => SettingsPersistenceService.Instance.LoadSettings()).ConfigureAwait(true);

        // 2) 应用持久化主题与语言（须在主窗口创建前完成，使主窗口拿到正确的语言/主题；
        //    此时启动窗已显示，DynamicResource 会自动刷新启动窗文案）。
        // 2) Apply the persisted theme and language (must run before the main window is created
        //    so it picks up the right locale/theme; the splash is already visible and its
        //    DynamicResource texts refresh automatically).
        ApplyTheme(settings.ThemeMode);
        ApplyLanguage(settings.CultureName);

        splash.StatusText = LanguageService.Instance.GetString("Splash_Scanning");
        LogService.Instance.Write(LogLevel.Info, "App", "Startup sequence: initial USB scan starting.");

        // 3) 启动设备监视并执行首次全局 USB 扫描，整体放后台线程：Start 内含 SetupAPI 初次
        //    全量快照与 adb server 探测（可达数百毫秒），不能阻塞 UI 线程（启动窗会冻结）。
        // 3) Start the device monitor and perform the initial full USB scan entirely on a
        //    background thread: Start includes the first SetupAPI snapshot and the adb-server
        //    probe (potentially hundreds of milliseconds) and must not freeze the splash UI.
        var scanTask = Task.Run(() =>
        {
            DeviceMonitorService.Instance.Start();
            return DeviceMonitorService.Instance.Scan();
        });

        // 最小可见时长与扫描并行计时，缩短总启动耗时。
        // Run the minimum-visibility window in parallel with the scan.
        var minShowTask = Task.Delay(600);

        var devices = await scanTask.ConfigureAwait(true);

        // 4) 扫描完成、启动窗仍可见时，在 UI 线程预构建主窗口（含全部子视图与 ViewModel），
        //    把构造开销藏到启动窗显示期，消除过渡到主窗体时的卡顿。
        // 4) Pre-build the main window (views + view models) on the UI thread while the splash
        //    is still visible, hiding the construction cost and removing the transition jank.
        try
        {
            splashWindow.PreparedMainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Pre-building main window failed: {ex.Message}");
        }

        splash.StatusText = string.Format(
            LanguageService.Instance.GetString("Splash_FoundDevices"), devices.Count);
        LogService.Instance.Write(LogLevel.Info, "App",
            $"Startup sequence: initial USB scan found {devices.Count} device(s).");

        await minShowTask.ConfigureAwait(true);
        splash.StatusText = LanguageService.Instance.GetString("Splash_Preparing");
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
            ThemeMode.System => ThemeVariant.Default,
            _ => ThemeVariant.Default
        };

        string themeKey;
        if (themeMode == ThemeMode.System)
        {
            var actualVariant = Current?.ActualThemeVariant ?? ThemeVariant.Default;
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
        foreach (var style in Styles)
        {
            switch (style)
            {
                case Semi.Avalonia.SemiTheme semiTheme:
                    semiTheme.Locale = culture;
                    break;
                case Ursa.Themes.Semi.UrsaSemiTheme ursaTheme:
                    ursaTheme.Locale = culture;
                    break;
                default:
                    break;
            }
        }
    }
}