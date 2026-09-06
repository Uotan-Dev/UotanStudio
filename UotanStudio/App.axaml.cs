using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Firmware.Common;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Mcp;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;

namespace UotanStudio;

public partial class App : Application
{
    /// <summary>
    /// <para>由 <c>Program.Main</c> 解析并注入的命令行选项；启动序列在主窗口预构建后据此打开解决方案/导入文件。</para>
    /// Command-line options parsed and injected by <c>Program.Main</c>; the startup sequence opens a solution
    /// and/or imports files based on them after the main window is pre-built.
    /// </summary>
    public static CommandLineOptions? StartupOptions { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // 跟随系统主题时，ActualThemeVariant 会随系统切换；据此重新合并
        // LightColors/DarkColors，使 Hex* 等自定义画刷同步刷新。
        ActualThemeVariantChanged += (_, _) =>
        {
            var key = ActualThemeVariant == ThemeVariant.Dark ? "DarkColors" : "LightColors";
            if (LanguageService.Instance.CurrentTheme != key)
            {
                LanguageService.Instance.ApplyTheme(key);
            }
        };
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 将 Comm 层 USB 诊断桥接到统一日志服务。
        UsbLogBridge.Install();
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");

        // 幂等注册文件关联（后台执行，不阻塞启动；可用 --unregister-file-associations 注销）。
        // Idempotently register file associations (background, non-blocking; remove with
        // --unregister-file-associations).
        _ = Task.Run(() => FileAssociationService.Register());

        // 注册全部固件格式处理器，供后续格式自动识别与结构分析使用。
        UotanStudio.Firmware.Common.FirmwareFormatRegistry.RegisterAll();

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
        WeakReferenceMessenger.Default.Register<OpenCompareRequestMessage>(this, OnOpenCompare);
        WeakReferenceMessenger.Default.Register<OpenFileCompareRequestMessage>(this, OnOpenFileCompare);
        WeakReferenceMessenger.Default.Register<OpenFolderCompareRequestMessage>(this, OnOpenFolderCompare);

        // 启动 MCP 服务器宿主（设置页「暴露为 MCP 服务器」开启时）。
        // Starts the MCP server host (when the settings page's "expose as MCP server" is enabled).
        StartMcpHostIfEnabled();
    }

    /// <summary>
    /// <para>按设置启动 MCP 服务器宿主（暴露开关与端口）。</para>
    /// Starts the MCP server host per the settings (expose toggle and port).
    /// </summary>
    private static void StartMcpHostIfEnabled()
    {
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        if (settings.McpExposeEnabled && settings.McpExposePort > 0)
        {
            McpHostService.Instance.Start(settings.McpExposePort);
        }
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

        // 应用命令行选项：打开解决方案 / 导入文件（失败仅记日志，不阻断启动）。
        // Apply command-line options: open a solution / import files (failures only log, never block startup).
        if (StartupOptions is { } startupOptions)
        {
            await ApplyStartupOptionsAsync(splashWindow, startupOptions).ConfigureAwait(true);
        }

        splash.StatusText = string.Format(
            LanguageService.Instance.GetString("Splash_FoundDevices"), devices.Count);
        LogService.Instance.Write(LogLevel.Info, "App",
            $"Startup sequence: initial USB scan found {devices.Count} device(s).");

        await minShowTask.ConfigureAwait(true);
        splash.StatusText = LanguageService.Instance.GetString("Splash_Preparing");
    }

    /// <summary>
    /// <para>应用命令行选项：打开指定解决方案并导入文件（失败仅记日志，不阻断启动）。</para>
    /// Applies the command-line options: opens the specified solution and imports files (failures only log,
    /// never block startup).
    /// </summary>
    private async Task ApplyStartupOptionsAsync(MainSplashWindow splashWindow, CommandLineOptions options)
    {
        try
        {
            if (splashWindow.PreparedMainWindow?.DataContext is not MainViewModel mainVm)
            {
                return;
            }

            if (options.ProjectPath is { } projectPath)
            {
                await mainVm.MenuVM.OpenProjectFromPathAsync(projectPath);
            }

            if (options.ImportFiles.Count > 0)
            {
                await mainVm.MenuVM.ImportFilesAsync([.. options.ImportFiles]);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Apply startup options failed: {ex.Message}");
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
                await mainVm.WorkspaceVM.OpenDocumentAsync(message.Path, message.Mode);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open document failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>打开或聚焦工作区对比文档，并将右键选中的路径加入比较。</para>
    /// Opens or focuses the workspace compare document and adds the explorer selection when provided.
    /// </summary>
    private async void OnOpenCompare(object recipient, OpenCompareRequestMessage message)
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainViewModel mainVm
                && mainVm.WorkspaceVM.OpenCompareDocument() is { } compare)
            {
                if (message.Path is { } path)
                {
                    await compare.AddComparisonPathAsync(path);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open compare failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>响应文件比较请求消息，在工作区对比文档中载入两个文件。</para>
    /// Responds to file-compare requests by loading both files in the workspace compare document.
    /// </summary>
    private async void OnOpenFileCompare(object recipient, OpenFileCompareRequestMessage message)
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainViewModel mainVm
                && mainVm.WorkspaceVM.OpenCompareDocument() is { } compare)
            {
                await compare.OpenCompareFilesAsync(message.LeftPath, message.RightPath);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open file compare failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>响应文件夹比较请求消息，在工作区对比文档中载入两个目录。</para>
    /// Responds to folder-compare requests by loading both directories in the workspace compare document.
    /// </summary>
    private async void OnOpenFolderCompare(object recipient, OpenFolderCompareRequestMessage message)
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainViewModel mainVm
                && mainVm.WorkspaceVM.OpenCompareDocument() is { } compare)
            {
                await compare.OpenCompareFoldersAsync(message.LeftDirectory, message.RightDirectory);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open folder compare failed: {ex.Message}");
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

        // 根据实际生效的主题变体选择颜色字典：跟随系统（Default）时由 Avalonia 解析
        // ActualThemeVariant，据此合并 LightColors/DarkColors，确保深色系统下 Hex* 等画刷也切换。
        var actual = ActualThemeVariant;
        var themeKey = actual == ThemeVariant.Dark ? "DarkColors" : "LightColors";
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