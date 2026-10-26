using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
#if DEBUG
using Dock.Avalonia.Diagnostics;
#endif
using Microsoft.Extensions.DependencyInjection;
using UotanStudio.Composition;
using UotanStudio.Fonts;
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
    private static readonly Lazy<DeviceMonitorService> _defaultDeviceMonitor =
        new(() => DeviceMonitorService.Create());
    private DesktopApplicationServices? _desktopServices;
    private MacOSApplicationMenu? _macApplicationMenu;
    /// <summary>Existing files passed by desktop file associations, opened after window creation.<para>桌面文件关联传入的已存在文件，在窗口创建后打开。</para></summary>
    /// <value>The file paths collected before the window exists.<para>在窗口存在之前收集到的文件路径。</para></value>
    public static string[] StartupFiles { get; set; } = [];

    /// <summary>
    /// The device serial carried by the <c>--open-screen-mirror</c> command line.
    /// <para>由命令行 <c>--open-screen-mirror</c> 携带的设备序列号。</para>
    /// </summary>
    /// <value>The requested serial, or null when no mirror was requested.<para>请求的序列号；未请求投屏时为 null。</para></value>
    public static string? StartupScreenMirrorSerial { get; set; }

    /// <summary>
    /// The application service provider built by <see cref="Initialize"/>; null until the container
    /// exists, so callers can keep their pre-container fallback paths.
    /// <para>由 <see cref="Initialize"/> 构建的应用服务提供程序；容器尚未建立时为 null，使调用方可以保留
    /// 容器建立前的回退路径。</para>
    /// </summary>
    /// <value>The composed provider, or null before initialization. <para>组合后的提供程序；初始化之前为 null。</para></value>
    internal static IServiceProvider? Services => ServiceHub.Services;

    /// <summary>
    /// The single device monitor instance owned by the composition root and released on desktop exit;
    /// <c>DeviceMonitorService</c> provides no type-level global singleton. The container registration
    /// wins once it exists; before that, the lazily created composition-root fallback is used.
    /// <para>组合根拥有、在桌面退出时释放的唯一设备监视实例；<c>DeviceMonitorService</c> 不提供类型级全局单例。
    /// 容器注册存在时优先取容器实例；此前使用惰性创建的组合根回退实例。</para>
    /// </summary>
    /// <value>The shared desktop device monitor.<para>共享的桌面设备监视实例。</para></value>
    internal static DeviceMonitorService DeviceMonitor =>
        ServiceHub.TryGet<DeviceMonitorService>() ?? _defaultDeviceMonitor.Value;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var services = ServiceRegistration.BuildProvider(
            ServiceRegistration.Register(new ServiceCollection(), DeviceMonitor, Views.UiWindowService.Shared));
        ServiceHub.Initialize(services);
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
        this.AttachDeveloperTools();
        this.AttachDockDebugOverlay(new KeyGesture(Key.F9));
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        UsbLogBridge.Install();
        FirstChanceExceptionLogger.Install();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");

        _ = Task.Run(FileAssociationService.Register);
        _ = UotanStudio.Services.Scrcpy.ScrcpyServerService.Shared.RunBackgroundCheckAsync(TimeSpan.FromSeconds(8));

        Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();

        _ = PluginHost.Current.Capability;

        LanguageService.Instance.Initialize();
        LanguageService.Instance.InitializeFromSystemLanguage();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsMacOS())
                _macApplicationMenu = new MacOSApplicationMenu(this,
                    () => GetDesktopMainViewModel()?.MenuVM.AboutCommand.Execute(null));
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
    /// Startup sequence: applies persisted settings, starts device monitoring with the initial full
    /// USB scan while reporting stage status, and pre-builds the main window once the scan completes.
    /// <para>启动序列：应用持久化设置，启动设备监视并执行首次全局 USB 扫描且上报阶段状态，扫描完成后预构建主窗口。</para>
    /// </summary>
    /// <param name="splashWindow">The splash window receiving the pre-built main window. <para>接收预构建主窗口的启动窗。</para></param>
    /// <param name="splash">The splash status target receiving stage updates. <para>接收阶段状态更新的启动提示目标。</para></param>
    private async Task RunStartupSequenceAsync(MainSplashWindow splashWindow, SplashViewModel splash)
    {
        var stageTimer = Stopwatch.StartNew();
        var previousPhase = SplashPhase.Initializing;
        async Task ReportAsync(SplashPhase phase, int deviceCount = -1)
        {
            LogService.Instance.Write(LogLevel.Info, "Startup",
                $"{previousPhase}: {stageTimer.ElapsedMilliseconds} ms; starting {phase}.");
            await splash.AdvanceAsync(phase, deviceCount);
            previousPhase = phase;
            stageTimer.Restart();
        }
        await ReportAsync(SplashPhase.Resources);
        try
        {
            FontManager.Current.AddFontCollection(new ZstdEmbeddedFontCollection());
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Startup", $"Bundled font registration failed: {ex.Message}");
        }

        await ReportAsync(SplashPhase.Settings);
        var settings = await Task.Run(() => SettingsPersistenceService.Instance.LoadSettings()).ConfigureAwait(true);
        Comm.DeviceCommunicationConfiguration.Current = settings.DeviceCommunication;
        Firmware.Core.FirmwareParallelism.Workers = settings.FirmwareParallelism;
        _desktopServices = new DesktopApplicationServices();

        await ReportAsync(SplashPhase.ApplyingSettings);
        ApplyTheme(settings.ThemeMode);
        ApplyLanguage(settings.CultureName);
        McpExposureService.Instance.Apply(settings.McpExposeEnabled, settings.McpExposePort);
        McpClientConnectionService.Instance.Apply();

        await ReportAsync(SplashPhase.Scanning);
        LogService.Instance.Write(LogLevel.Info, "App", "Startup sequence: initial USB scan starting.");

        var scanTask = _desktopServices!.StartAndScanAsync();

        var devices = await scanTask.ConfigureAwait(true);
        await ReportAsync(SplashPhase.DevicesFound, devices.Count);

        MainViewModel? preparedModel = null;
        try
        {
            preparedModel = new MainViewModel(await ShellComposition.CreateAsync(
                phase => ReportAsync(phase), settings.CultureName));
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

        if (StartupScreenMirrorSerial is { Length: > 0 } mirrorSerial)
        {
            await ReportAsync(SplashPhase.OpeningFiles);
            ApplyStartupScreenMirror(splashWindow, mirrorSerial);
        }

        LogService.Instance.Write(LogLevel.Info, "App",
            $"Startup sequence: initial USB scan found {devices.Count} device(s).");

        await ReportAsync(SplashPhase.Ready);
        if (splashWindow.PreparedMainWindow is not null)
        {
            await splashWindow.PresentCompletionAsync();
        }
    }

    /// <summary>
    /// Last-chance device release when the process exits without raising the desktop exit event (unhandled exception,
    /// crash reporting, console close). Bounded, best effort and a no-op once the normal exit path already released.
    /// <para>进程未经桌面退出事件而退出（未处理异常、崩溃上报、关闭控制台）时的最后一道设备释放。有界、尽力而为，
    /// 正常退出路径已释放时为空操作。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件源。</para></param>
    /// <param name="e">Event arguments. <para>事件参数。</para></param>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            _desktopServices?.ReleaseUsbOccupancy(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Lifecycle",
                $"Device occupancy release during process exit failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Releases application-wide monitoring only after desktop shutdown has been accepted.<para>仅在桌面关闭已被接受后释放应用级监视资源。</para></summary>
    /// <param name="sender">The desktop lifetime raising the exit event.<para>引发退出事件的桌面生命周期实例。</para></param>
    /// <param name="e">The exit arguments describing the accepted shutdown.<para>描述已接受关闭操作的退出参数。</para></param>
    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (sender is IClassicDesktopStyleApplicationLifetime desktop) desktop.Exit -= OnDesktopExit;
        McpExposureService.Instance.Stop();
        try { GetDesktopMainViewModel()?.Dispose(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Lifecycle", $"Closing views during shutdown failed: {ex.GetType().Name}: {ex.Message}"); }
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
    /// Applies startup file-association inputs: opens the specified solution and imports files;
    /// failures only log and never block startup.
    /// <para>应用文件关联启动输入：打开指定解决方案并导入文件，失败仅记录日志且不阻断启动。</para>
    /// </summary>
    /// <param name="splashWindow">The splash window whose pre-built main window receives the inputs.<para>其预构建主窗口接收这些输入的启动窗。</para></param>
    /// <param name="files">The startup paths to open as a project or import as files.<para>作为项目打开或作为文件导入的启动路径。</para></param>
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

    /// <summary>
    /// Applies the startup screen-mirror input: opens the mirror document and starts a session for
    /// the given serial; failures only log and never block startup.
    /// <para>应用投屏启动输入：打开投屏文档并为指定序列号启动会话，失败仅记录日志且不阻断启动。</para>
    /// </summary>
    /// <param name="splashWindow">The splash window whose pre-built main window hosts the document.<para>其预构建主窗口承载该文档的启动窗。</para></param>
    /// <param name="serial">The serial of the device to mirror.<para>需要投屏的设备序列号。</para></param>
    private void ApplyStartupScreenMirror(MainSplashWindow splashWindow, string serial)
    {
        try
        {
            if (splashWindow.PreparedMainWindow?.DataContext is MainViewModel mainVm &&
                mainVm.WorkspaceVM.OpenScreenMirrorDocument(serial) is { } mirror)
            {
                mirror.StartForDevice(serial);
                LogService.Instance.Write(LogLevel.Info, "App", $"Startup screen mirror requested for {serial}.");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Startup screen mirror failed: {ex.Message}");
        }
    }

    private void OnThemeChanged(object recipient, ThemeChangedMessage message)
    {
        ApplyTheme(message.ThemeMode);
    }

    /// <summary>
    /// Responds to an open-document request by opening the requested workspace document.
    /// <para>响应打开文档请求，打开所请求的工作区文档。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request describing the document to open.<para>描述待打开文档的请求。</para></param>
    private async void OnOpenDocument(object recipient, OpenDocumentRequestMessage message)
        => await RunMainRequestAsync("Open document failed", main => main.WorkspaceVM.OpenDocumentAsync(message.ToRequest()));

    /// <summary>
    /// Responds to an open-node-project request by loading a solution-registered node project and
    /// switching to its tab.
    /// <para>响应打开节点项目请求，载入解决方案登记的节点项目并切换到对应标签页。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request naming the node project to open.<para>指明待打开节点项目名的请求。</para></param>
    private async void OnOpenNodeProject(object recipient, OpenNodeProjectRequestMessage message)
        => await RunMainRequestAsync("Open node project failed", main => main.MenuVM.OpenNodeProjectAsync(message.NodeProjectName));

    /// <summary>
    /// Opens or focuses the workspace compare document and adds the selected path when provided.
    /// <para>打开或聚焦工作区对比文档，并在给出路径时将其加入比较。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request carrying the optional path and the new-tab preference.<para>携带可选路径与新标签页偏好的请求。</para></param>
    private async void OnOpenCompare(object recipient, OpenCompareRequestMessage message)
        => await RunCompareRequestAsync(
            "Open compare failed",
            compare => message.Path is { } path ? compare.AddComparisonPathAsync(path) : Task.CompletedTask,
            openInNewTab: message.OpenInNewTab);

    /// <summary>
    /// Responds to a screen-mirror request by opening the mirror document and starting its session.
    /// <para>响应投屏请求，打开投屏文档并启动对应会话。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request carrying the device serial to mirror.<para>携带待投屏设备序列号的请求。</para></param>
    private void OnOpenScreenMirror(object recipient, OpenScreenMirrorRequestMessage message)
    {
        try
        {
            if (GetDesktopMainViewModel()?.WorkspaceVM.OpenScreenMirrorDocument(message.Serial) is { } mirror)
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
    /// Responds to an add-sub-project request by starting the new-project flow in sub-project mode.
    /// <para>响应添加子项目请求，以子项目模式启动新建项目流程。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request, which carries no additional payload.<para>该请求不携带附加数据。</para></param>
    private async void OnAddSubProject(object recipient, AddSubProjectRequestMessage message)
        => await RunMainRequestAsync("Open add sub-project failed", main => main.MenuVM.AddSubProjectCommand.ExecuteAsync(null));

    /// <summary>
    /// Responds to a file-compare request by loading both files into a workspace compare document.
    /// <para>响应文件比较请求，把两个文件载入工作区对比文档。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request carrying the left and right file paths.<para>携带左右两侧文件路径的请求。</para></param>
    private async void OnOpenFileCompare(object recipient, OpenFileCompareRequestMessage message)
        => await RunCompareRequestAsync("Open file compare failed",
            compare => compare.OpenCompareFilesAsync(message.LeftPath, message.RightPath));

    /// <summary>
    /// Responds to a new-compare-tab request by creating an additional compare document and loading
    /// both files, leaving the existing tab untouched; a blank side stays blank.
    /// <para>响应新建比较标签请求：创建额外的对比文档并载入两侧文件，已有标签保持不变；空缺的一侧保持留白。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request carrying the left and right file paths.<para>携带左右两侧文件路径的请求。</para></param>
    private async void OnOpenFileCompareInNewTab(object recipient, OpenFileCompareInNewTabRequestMessage message)
        => await RunMainRequestAsync("Open file compare in new tab failed", main =>
            main.WorkspaceVM.OpenNewCompareDocument() is { } compare
                ? compare.OpenCompareFilesAsync(message.LeftPath, message.RightPath)
                : Task.CompletedTask);

    /// <summary>
    /// Responds to a folder-compare request by loading both directories into a compare document.
    /// <para>响应目录比较请求，把两个目录载入对比文档。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request carrying the left and right directory paths.<para>携带左右两侧目录路径的请求。</para></param>
    private async void OnOpenFolderCompare(object recipient, OpenFolderCompareRequestMessage message)
        => await RunCompareRequestAsync("Open folder compare failed",
            compare => compare.OpenCompareFoldersAsync(message.LeftDirectory, message.RightDirectory));

    /// <summary>
    /// Responds to the exit-application message by closing the main window or shutting the
    /// controlled lifetime down.
    /// <para>响应退出应用程序消息，关闭主窗口或终止受控生命周期。</para>
    /// </summary>
    /// <param name="recipient">The registered handler instance targeted by the message.<para>该消息指向的已注册处理实例。</para></param>
    /// <param name="message">The request asking the application to exit.<para>请求退出应用程序的消息。</para></param>
    private void OnExitApplication(object recipient, ExitApplicationMessage message)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Close();
            return;
        }
        if (ApplicationLifetime is IControlledApplicationLifetime controlled)
        {
            controlled.Shutdown();
        }
    }

    /// <summary>
    /// Resolves the desktop main model bound as the main window's data context.
    /// <para>解析绑定为主窗口数据上下文的桌面主模型。</para>
    /// </summary>
    /// <returns>The main model, or null when no desktop lifetime or bound window exists.<para>桌面主模型；没有桌面生命周期或已绑定窗口时为 null。</para></returns>
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

    private Task RunCompareRequestAsync(string error, Func<ViewModels.Compare.CompareViewModel, Task> operation)
        => RunCompareRequestAsync(error, operation, openInNewTab: false);

    private Task RunCompareRequestAsync(
        string error,
        Func<ViewModels.Compare.CompareViewModel, Task> operation,
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