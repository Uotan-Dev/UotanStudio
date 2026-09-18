using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Composition;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Logging;
using UotanStudio.Services.Mcp;
using UotanStudio.Themes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;

namespace UotanStudio;

public partial class App : Application
{
    private DesktopApplicationServices? _desktopServices;
    private MacOSApplicationMenu? _macApplicationMenu;
    /// <summary>Existing files passed by desktop file associations, opened after window creation.</summary>
    public static string[] StartupFiles { get; set; } = [];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // 全局主题色：把 Themes/AccentColors.axaml 的强调色补进 Semi 主题作用域（见 AccentPalette）。
        // Global accent: copy Themes/AccentColors.axaml into the Semi theme scope (see AccentPalette).
        AccentPalette.Install(this);
        // Subscribe before startup settings are applied; retain LanguageChangedMessage for legacy callers.
        WeakReferenceMessenger.Default.Register<App, LanguageStateChangedMessage>(this,
            static (app, message) => app.UpdateSemiThemeLocale(message.CultureName));
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
        // 挂接首次机会异常记录器：捕获调试器输出窗中「引发的异常」（Socket/取消/超时）到全局日志。
        // Hook the first-chance exception logger: capture debugger-output "exception thrown"
        // notices (socket / cancellation / timeout) into the global log.
        FirstChanceExceptionLogger.Install();
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");

        // 幂等注册文件关联（后台执行，不阻塞启动；可用 --unregister-file-associations 注销）。
        // Idempotently register file associations (background, non-blocking; remove with
        // --unregister-file-associations).
        _ = Task.Run(() => FileAssociationService.Register());

        // 注册全部固件格式处理器，供后续格式自动识别与结构分析使用。
        UotanStudio.Firmware.SharedKernel.FirmwareFormatRegistry.RegisterAll();

        // 仅初始化语言包注册表（纯内存操作，快速）；持久化设置的读取与主题/语言 XAML
        // 加载推迟到启动序列中执行，使启动窗能立刻显示，避免首帧出现前的同步阻塞。
        LanguageService.Instance.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsMacOS())
                _macApplicationMenu = new MacOSApplicationMenu(this,
                    () => GetDesktopMainViewModel()?.MenuVM.AboutCommand.Execute(null));
            _desktopServices = new DesktopApplicationServices();
            desktop.Exit += OnDesktopExit;
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
            var viewModel = new MainViewModel();
            singleViewPlatform.MainView = new MainView
            {
                DataContext = viewModel
            };
            _ = viewModel.InitializeAutoSaveAsync();
        }

        base.OnFrameworkInitializationCompleted();

        // Register messenger receivers
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, OnThemeChanged);
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
        WeakReferenceMessenger.Default.Register<ExitApplicationMessage>(this, OnExitApplication);
        WeakReferenceMessenger.Default.Register<OpenDocumentRequestMessage>(this, OnOpenDocument);
        WeakReferenceMessenger.Default.Register<OpenCompareRequestMessage>(this, OnOpenCompare);
        WeakReferenceMessenger.Default.Register<OpenScreenMirrorRequestMessage>(this, OnOpenScreenMirror);
        WeakReferenceMessenger.Default.Register<AddSubProjectRequestMessage>(this, OnAddSubProject);
        WeakReferenceMessenger.Default.Register<OpenFileCompareRequestMessage>(this, OnOpenFileCompare);
        WeakReferenceMessenger.Default.Register<OpenFileCompareInNewTabRequestMessage>(this, OnOpenFileCompareInNewTab);
        WeakReferenceMessenger.Default.Register<OpenFolderCompareRequestMessage>(this, OnOpenFolderCompare);

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
        global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.Workers = settings.FirmwareParallelism;

        // 按持久化设置启动内嵌 MCP 服务器（失败只记录日志，不阻断启动）。
        // Start the embedded MCP server per the persisted settings (failures only log, never block startup).
        McpExposureService.Instance.Apply(settings.McpExposeEnabled, settings.McpExposePort);

        // 2) 应用持久化主题与语言（须在主窗口创建前完成，使主窗口拿到正确的语言/主题；
        //    此时启动窗已显示，DynamicResource 会自动刷新启动窗文案）。
        ApplyTheme(settings.ThemeMode);
        ApplyLanguage(settings.CultureName);

        splash.StatusText = LanguageService.Instance.GetString("Splash_Scanning");
        LogService.Instance.Write(LogLevel.Info, "App", "Startup sequence: initial USB scan starting.");

        // 3) 启动设备监视并执行首次全局 USB 扫描，整体放后台线程：Start 内含 SetupAPI 初次
        //    全量快照与 adb server 探测（可达数百毫秒），不能阻塞 UI 线程（启动窗会冻结）。
        var scanTask = _desktopServices!.StartAndScanAsync();

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
        if (StartupFiles.Length > 0)
        {
            await ApplyStartupFilesAsync(splashWindow, StartupFiles).ConfigureAwait(true);
        }

        splash.StatusText = string.Format(
            LanguageService.Instance.GetString("Splash_FoundDevices"), devices.Count);
        LogService.Instance.Write(LogLevel.Info, "App",
            $"Startup sequence: initial USB scan found {devices.Count} device(s).");

        await minShowTask.ConfigureAwait(true);
        splash.StatusText = LanguageService.Instance.GetString("Splash_Preparing");
    }

    /// <summary>Releases application-wide monitoring only after desktop shutdown has been accepted.</summary>
    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (sender is IClassicDesktopStyleApplicationLifetime desktop) desktop.Exit -= OnDesktopExit;
        McpExposureService.Instance.Stop();
        try { _desktopServices?.Dispose(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Lifecycle", ex.Message); }
        finally
        {
            _macApplicationMenu?.Dispose();
            _macApplicationMenu = null;
            _desktopServices = null;
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    /// <summary>
    /// <para>应用命令行选项：打开指定解决方案并导入文件（失败仅记日志，不阻断启动）。</para>
    /// Applies the command-line options: opens the specified solution and imports files (failures only log,
    /// never block startup).
    /// </summary>
    private async Task ApplyStartupFilesAsync(MainSplashWindow splashWindow, string[] files)
    {
        try
        {
            if (splashWindow.PreparedMainWindow?.DataContext is not MainViewModel mainVm)
            {
                return;
            }

            if (files.LastOrDefault(path => path.EndsWith(".uotan", StringComparison.OrdinalIgnoreCase)) is { } projectPath)
            {
                await mainVm.MenuVM.OpenProjectFromPathAsync(projectPath);
            }

            var imports = files.Where(path => !path.EndsWith(".uotan", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (imports.Length > 0)
            {
                await mainVm.MenuVM.ImportFilesAsync(imports);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open startup files failed: {ex.Message}");
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
        => await RunMainRequestAsync("Open document failed", main => main.WorkspaceVM.OpenDocumentAsync(message.Path, message.Mode));

    /// <summary>
    /// <para>打开或聚焦工作区对比文档，并将右键选中的路径加入比较。</para>
    /// Opens or focuses the workspace compare document and adds the explorer selection when provided.
    /// </summary>
    private async void OnOpenCompare(object recipient, OpenCompareRequestMessage message)
        => await RunCompareRequestAsync(
            "Open compare failed",
            compare => message.Path is { } path ? compare.AddComparisonPathAsync(path) : Task.CompletedTask,
            openInNewTab: message.OpenInNewTab);

    /// <summary>
    /// <para>响应投屏请求：打开投屏文档并对指定设备启动会话。</para>
    /// Responds to the mirror request: opens the mirror document and starts the session for the device.
    /// </summary>
    private void OnOpenScreenMirror(object recipient, OpenScreenMirrorRequestMessage message)
    {
        try
        {
            if (GetDesktopMainViewModel()?.WorkspaceVM.OpenScreenMirrorDocument() is { } mirror)
            {
                mirror.StartForDevice(message.Serial);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Open screen mirror failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>响应添加子项目请求：转发到菜单命令（打开子项目模式的新建窗口）。</para>
    /// Responds to the add-sub-project request: forwards to the menu command (the new-project window
    /// in sub-project mode).
    /// </summary>
    private async void OnAddSubProject(object recipient, AddSubProjectRequestMessage message)
        => await RunMainRequestAsync("Open add sub-project failed", main => main.MenuVM.AddSubProjectCommand.ExecuteAsync(null));

    /// <summary>
    /// <para>响应文件比较请求消息，在工作区对比文档中载入两个文件。</para>
    /// Responds to file-compare requests by loading both files in the workspace compare document.
    /// </summary>
    private async void OnOpenFileCompare(object recipient, OpenFileCompareRequestMessage message)
        => await RunCompareRequestAsync("Open file compare failed",
            compare => compare.OpenCompareFilesAsync(message.LeftPath, message.RightPath));

    /// <summary>
    /// <para>响应「在新标签打开文件比较」请求：新建一个对比文档并载入两侧文件（一侧为空时该侧留白），
    /// 当前标签（目录对比）保持不变。</para>
    /// Responds to the "open the file comparison in a new tab" request: creates an additional compare document and
    /// loads both files (an empty side stays blank), leaving the current tab (the folder comparison) untouched.
    /// </summary>
    private async void OnOpenFileCompareInNewTab(object recipient, OpenFileCompareInNewTabRequestMessage message)
        => await RunMainRequestAsync("Open file compare in new tab failed", main =>
            main.WorkspaceVM.OpenNewCompareDocument() is { } compare
                ? compare.OpenCompareFilesAsync(message.LeftPath, message.RightPath)
                : Task.CompletedTask);

    /// <summary>
    /// <para>响应文件夹比较请求消息，在工作区对比文档中载入两个目录。</para>
    /// Responds to folder-compare requests by loading both directories in the workspace compare document.
    /// </summary>
    private async void OnOpenFolderCompare(object recipient, OpenFolderCompareRequestMessage message)
        => await RunCompareRequestAsync("Open folder compare failed",
            compare => compare.OpenCompareFoldersAsync(message.LeftDirectory, message.RightDirectory));

    /// <summary>
    /// <para>响应退出应用程序消息，关闭主窗口并退出。</para>
    /// Responds to the exit-application message by closing the main window and quitting.
    /// </summary>
    private void OnExitApplication(object recipient, ExitApplicationMessage message)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Close();
            return;
        }
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IControlledApplicationLifetime controlled)
        {
            controlled.Shutdown();
        }
    }

    /// <summary>Resolves the same desktop request target used by the existing message adapters.</summary>
    private MainViewModel? GetDesktopMainViewModel() => ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        ? desktop.MainWindow?.DataContext as MainViewModel : null;

    private async Task RunMainRequestAsync(string error, Func<MainViewModel, Task> operation)
    {
        try
        {
            if (GetDesktopMainViewModel() is { } main) await operation(main);
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "App", $"{error}: {ex.Message}"); }
    }

    private Task RunCompareRequestAsync(string error, Func<UotanStudio.ViewModels.Compare.CompareViewModel, Task> operation)
        => RunCompareRequestAsync(error, operation, openInNewTab: false);

    private Task RunCompareRequestAsync(
        string error,
        Func<UotanStudio.ViewModels.Compare.CompareViewModel, Task> operation,
        bool openInNewTab)
        => RunMainRequestAsync(error, main =>
        {
            var compare = openInNewTab
                ? main.WorkspaceVM.OpenNewCompareDocument()
                : main.WorkspaceVM.OpenCompareDocument();
            return compare is { } document ? operation(document) : Task.CompletedTask;
        });

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