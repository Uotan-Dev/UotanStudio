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
        // 将 Comm 层 USB 诊断桥接到统一日志服务。
        UsbLogBridge.Install();
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");

        // 注册全部固件格式处理器，供后续格式自动识别与结构分析使用。
        UotanStudio.Firmware.FirmwareFormatRegistry.RegisterAll();

        // 仅初始化语言包注册表（纯内存操作，快速）；持久化设置的读取与主题/语言 XAML
        // 加载推迟到启动序列中执行，使启动窗能立刻显示，避免首帧出现前的同步阻塞。
        LanguageService.Instance.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 桌面端：立即显示 Ursa 主题启动窗，启动序列（应用设置 + 首次全局 USB 扫描
            // + 预构建主窗口）完成后关闭，由 SplashWindow 过渡到主窗口。
            var splash = new MainSplashWindow
            {
                DataContext = new SplashViewModel(string.Empty)
            };
            desktop.MainWindow = splash;
            splash.Show();
            splash.StartupTask = RunStartupSequenceAsync(splash, (SplashViewModel)splash.DataContext);
            // Close 触发 Ursa SplashWindow 的 CanClose（等待启动序列）→ CreateNextWindow 过渡。
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
        WeakReferenceMessenger.Default.Register<ExitApplicationMessage>(this, OnExitApplication);
        WeakReferenceMessenger.Default.Register<OpenDocumentRequestMessage>(this, OnOpenDocument);
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
        var settings = await Task.Run(() => SettingsPersistenceService.Instance.LoadSettings()).ConfigureAwait(true);

        // 2) 应用持久化主题与语言（须在主窗口创建前完成，使主窗口拿到正确的语言/主题；
        //    此时启动窗已显示，DynamicResource 会自动刷新启动窗文案）。
        ApplyTheme(settings.ThemeMode);
        ApplyLanguage(settings.CultureName);

        splash.StatusText = LanguageService.Instance.GetString("Splash_Scanning");
        LogService.Instance.Write(LogLevel.Info, "App", "Startup sequence: initial USB scan starting.");

        // 3) 启动设备监视并执行首次全局 USB 扫描，整体放后台线程：Start 内含 SetupAPI 初次
        //    全量快照与 adb server 探测（可达数百毫秒），不能阻塞 UI 线程（启动窗会冻结）。
        var scanTask = Task.Run(() =>
        {
            DeviceMonitorService.Instance.Start();
            return DeviceMonitorService.Instance.Scan();
        });

        // 最小可见时长与扫描并行计时，缩短总启动耗时。
        var minShowTask = Task.Delay(600);

        var devices = await scanTask.ConfigureAwait(true);

        // 4) 扫描完成、启动窗仍可见时，在 UI 线程预构建主窗口（含全部子视图与 ViewModel，
        //    把构造开销藏到启动窗显示期，消除过渡到主窗体时的卡顿）。
        //    DataContext 由 MainWindow 构造函数自行设置，这里无需再赋值。
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

    private void OnThemeChanged(object recipient, ThemeChangedMessage message)
    {
        ApplyTheme(message.ThemeMode);
    }

    private void OnLanguageChanged(object recipient, LanguageChangedMessage message)
    {
        UpdateSemiThemeLocale(message.CultureName);
    }

    /// <summary>
    /// <para>响应打开文档请求消息，将请求转发给主窗口工作区视图模型，由其打开对应文档。</para>
    /// Responds to open-document request messages by forwarding them to the main window's workspace
    /// view model, which opens the requested document.
    /// </summary>
    private async void OnOpenDocument(object recipient, OpenDocumentRequestMessage message)
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainViewModel mainVm)
            {
                await mainVm.WorkspaceVM.OpenDocumentAsync(message.Path);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open document failed: {ex.Message}");
        }
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