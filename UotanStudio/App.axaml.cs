using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Composition;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Logging;
using UotanStudio.Services.Mcp;
using UotanStudio.Services.Plugins;
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

    /// <summary>
    /// <para>桌面进程唯一的设备监视组合实例，由应用组合根拥有并经由
    /// <see cref="DesktopApplicationServices"/> 在退出时释放；界面与设备服务统一读取此实例，
    /// <c>DeviceMonitorService</c> 不提供类型级全局单例。</para>
    /// The single composition-root-owned device monitor for the desktop process, released on exit via
    /// <see cref="DesktopApplicationServices"/>; UI and device services read this instance, since
    /// <c>DeviceMonitorService</c> provides no type-level global singleton.
    /// </summary>
    internal static UotanStudio.Services.Devices.DeviceMonitorService DeviceMonitor { get; } =
        UotanStudio.Services.Devices.DeviceMonitorService.Create();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AccentPalette.Install(this);
        WeakReferenceMessenger.Default.Register<App, LanguageStateChangedMessage>(this,
            static (app, message) => app.UpdateSemiThemeLocale(message.CultureName));
        ActualThemeVariantChanged += (_, _) =>
        {
            var key = ActualThemeVariant == ThemeVariant.Dark ? "DarkColors" : "LightColors";
            if (LanguageService.Instance.CurrentTheme != key)
            {
                LanguageService.Instance.ApplyTheme(key);
            }
        };
#if DEBUG
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            this.AttachDeveloperTools();
        }
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        UsbLogBridge.Install();
        FirstChanceExceptionLogger.Install();
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");

        _ = Task.Run(() => FileAssociationService.Register());

        UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();

        _ = PluginHost.Current.Capability;

        LanguageService.Instance.Initialize();
        LanguageService.Instance.InitializeFromSystemLanguage();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsMacOS())
                _macApplicationMenu = new MacOSApplicationMenu(this,
                    () => GetDesktopMainViewModel()?.MenuVM.AboutCommand.Execute(null));
            _desktopServices = new DesktopApplicationServices();
            desktop.Exit += OnDesktopExit;
            var splash = new MainSplashWindow
            {
                DataContext = new SplashViewModel(LanguageService.Instance.GetString("Splash_Initializing"))
            };
            desktop.MainWindow = splash;
            splash.Show();
            splash.StartupTask = RunStartupSequenceAsync(splash, (SplashViewModel)splash.DataContext);
            splash.Close();
        }

        base.OnFrameworkInitializationCompleted();

        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, OnThemeChanged);
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
        WeakReferenceMessenger.Default.Register<ExitApplicationMessage>(this, OnExitApplication);
        WeakReferenceMessenger.Default.Register<OpenDocumentRequestMessage>(this, OnOpenDocument);
        WeakReferenceMessenger.Default.Register<OpenNodeProjectRequestMessage>(this, OnOpenNodeProject);
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
        var stageTimer = Stopwatch.StartNew();
        var previousPhase = SplashPhase.Initializing;
        async Task ReportAsync(SplashPhase phase)
        {
            LogService.Instance.Write(LogLevel.Info, "Startup",
                $"{previousPhase}: {stageTimer.ElapsedMilliseconds} ms; starting {phase}.");
            await splash.AdvanceAsync(phase);
            previousPhase = phase;
            stageTimer.Restart();
        }
        await ReportAsync(SplashPhase.Settings);
        var settings = await Task.Run(() => SettingsPersistenceService.Instance.LoadSettings()).ConfigureAwait(true);
        global::UotanStudio.Comm.DeviceCommunicationConfiguration.Current = settings.DeviceCommunication;
        global::UotanStudio.Firmware.Core.FirmwareParallelism.Workers = settings.FirmwareParallelism;

        await ReportAsync(SplashPhase.ApplyingSettings);
        ApplyTheme(settings.ThemeMode);
        ApplyLanguage(settings.CultureName);
        await splash.AdvanceAsync(SplashPhase.ApplyingSettings);
        McpExposureService.Instance.Apply(settings.McpExposeEnabled, settings.McpExposePort);
        McpClientConnectionService.Instance.Apply();

        await ReportAsync(SplashPhase.Scanning);
        LogService.Instance.Write(LogLevel.Info, "App", "Startup sequence: initial USB scan starting.");

        var scanTask = _desktopServices!.StartAndScanAsync();

        var devices = await scanTask.ConfigureAwait(true);

        MainViewModel? preparedModel = null;
        try
        {
            preparedModel = new MainViewModel(await ShellComposition.CreateAsync(ReportAsync, settings.CultureName));
            await ReportAsync(SplashPhase.Window);
            splashWindow.PreparedMainWindow = new MainWindow(preparedModel);
        }
        catch (Exception ex)
        {
            preparedModel?.Dispose();
            LogService.Instance.Write(LogLevel.Error, "App", $"Pre-building main window failed: {ex.Message}");
        }

        if (StartupFiles.Length > 0)
        {
            await ReportAsync(SplashPhase.OpeningFiles);
            await ApplyStartupFilesAsync(splashWindow, StartupFiles).ConfigureAwait(true);
        }

        LogService.Instance.Write(LogLevel.Info, "App",
            $"Startup sequence: initial USB scan found {devices.Count} device(s).");

        if (splashWindow.PreparedMainWindow is not null)
        {
            await ReportAsync(SplashPhase.Ready);
            await splashWindow.PresentCompletionAsync();
        }
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
    /// Applies startup file-association inputs: opens the specified solution and imports files (failures only log,
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
        => await RunMainRequestAsync("Open document failed", main => main.WorkspaceVM.OpenDocumentAsync(message.ToRequest()));

    /// <summary>
    /// <para>响应打开节点项目请求消息：把解决方案登记的节点项目载入节点编辑器画布并切到该选项卡。</para>
    /// Responds to open-node-project requests by loading a solution-registered node project into the node
    /// editor canvas and switching to that tab.
    /// </summary>
    private async void OnOpenNodeProject(object recipient, OpenNodeProjectRequestMessage message)
        => await RunMainRequestAsync("Open node project failed", main => main.MenuVM.OpenNodeProjectAsync(message.NodeProjectName));

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