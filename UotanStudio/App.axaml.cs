using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UotanStudio.Build;
using UotanStudio.Composition;
using UotanStudio.Fonts;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Launch;
using UotanStudio.Services.Logging;
using UotanStudio.Services.Mcp;
using UotanStudio.Services.NativeBin;
using UotanStudio.Services.Plugins;
using UotanStudio.Services.Ui.Launch;
using UotanStudio.Services.Update;
using UotanStudio.Themes;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views;
using UotanStudio.Views.Shell;

namespace UotanStudio;

public partial class App : Application
{
    private static readonly Lazy<DeviceMonitorService> _defaultDeviceMonitor =
        new(() => DeviceMonitorService.Create());
    private DesktopApplicationServices? _desktopServices;
    private MacOSApplicationMenu? _macApplicationMenu;
    private readonly List<DesktopLaunchRequest> _pendingLaunches = [];
    private DesktopInstanceRelay? _instanceRelay;
    private MainViewModel? _preparedMainViewModel;
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
    /// The single-instance relay created by the desktop entry point; null for a forwarded secondary launch
    /// that exits before the GUI starts.
    /// <para>由桌面入口创建的单实例中继；被转发的次级启动会在 GUI 启动前退出，因此为 null。</para>
    /// </summary>
    /// <value>The primary relay owned by this process, or null.<para>本进程持有的主实例中继；无则为 null。</para></value>
    public static DesktopInstanceRelay? PrimaryInstanceRelay { get; set; }

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
    /// 容器注册存在时优先取容器实例，否则使用惰性创建的组合根回退实例。</para>
    /// </summary>
    /// <value>The shared desktop device monitor.<para>共享的桌面设备监视实例。</para></value>
    internal static DeviceMonitorService DeviceMonitor =>
        ServiceHub.TryGet<DeviceMonitorService>() ?? DefaultDeviceMonitor;

    /// <summary>
    /// Gets the composition-root device monitor without asking the container, so a provider factory can
    /// create it lazily during the first scan instead of during provider construction.
    /// <para>获取不查询容器的组合根设备监视实例，使提供程序工厂可以在首次扫描时惰性创建它，而不是在构建提供程序时创建。</para>
    /// </summary>
    /// <value>The lazily created fallback monitor.<para>惰性创建的回退监视实例。</para></value>
    internal static DeviceMonitorService DefaultDeviceMonitor => _defaultDeviceMonitor.Value;

    public override void Initialize()
    {
        LogStartupMark("App.Initialize entry");
        AvaloniaXamlLoader.Load(this);
        LogStartupMark("App.Initialize XAML loaded");
        var collection = ServiceRegistration.Register(
            new ServiceCollection(), null, Views.UiWindowService.Shared, static () => DefaultDeviceMonitor);
        LogStartupMark("App.Initialize services registered");
        var services = ServiceRegistration.BuildProvider(collection);
        LogStartupMark("App.Initialize container built");
        ServiceHub.Initialize(services);
        AccentPalette.Install(this);
        InstallWindowRounding();
        LogStartupMark("App.Initialize completed");
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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        UsbLogBridge.Install();
        FirstChanceExceptionLogger.Install();
        UnhandledExceptionLogger.Install();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        LogService.Instance.Write(LogLevel.Info, "App", "UotanStudio framework initialization completed.");
        LogService.Instance.Write(LogLevel.Info, "Simd", UotanStudio.Hash.Simd.SimdCapabilities.Describe());

        _ = UotanStudio.Services.Scrcpy.ScrcpyServerService.Shared.RunBackgroundCheckAsync(TimeSpan.FromSeconds(8));
        _ = RunAutomaticUpdateCheckAsync();

        LanguageService.Instance.Initialize();
        LanguageService.Instance.InitializeFromSystemLanguage();

        global::UotanStudio.Services.Flash.IFlashElevationPromptProvider.Set(
            new global::UotanStudio.Services.Ui.FlashElevationPrompt(
                () => Views.UiWindowService.Shared.OwnerWindow as Avalonia.Controls.Window));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AttachInstanceRelay();
            if (OperatingSystem.IsMacOS())
                _macApplicationMenu = new MacOSApplicationMenu(this,
                    () => GetDesktopMainViewModel()?.MenuVM.AboutCommand.Execute(null));
            desktop.Exit += OnDesktopExit;
            // The global text style resolves the bundled MiSans collection, so every platform must register
            // it before the first window; a missing family fails the splash layout outright.
            RegisterBundledFonts();
            var splash = new MainSplashWindow
            {
                DataContext = new SplashViewModel(LanguageService.Instance.GetString("Splash_Initializing"))
            };
            desktop.MainWindow = splash;
            LogStartupMark("Splash showing");
            splash.Show();
            LogStartupMark("Splash shown");
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
    /// Writes one cold-start milestone with the elapsed time since process creation; diagnostics only.
    /// <para>写入一个冷启动里程碑并附带进程创建以来的耗时；仅用于诊断。</para>
    /// </summary>
    /// <param name="stage">The milestone name.<para>里程碑名称。</para></param>
    private static void LogStartupMark(string stage)
    {
        try
        {
            var elapsed = (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            LogService.Instance.Write(LogLevel.Debug, "Startup", $"[cold-start] {stage} at +{elapsed} ms.");
            LogService.Instance.FlushFileLog();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Decodes the bundled MiSans and JetBrains Mono faces and registers their font collections under
    /// <see cref="BundledFont.CollectionKey"/> and <see cref="BundledFont.MonospaceCollectionKey"/>. Runs before
    /// the first window is created because the global text style resolves MiSans; a missing family fails the splash
    /// layout outright. A failure is logged only; the caller still shows the splash, whose text then falls back to
    /// whatever the platform can resolve.
    /// <para>解码随包 MiSans 与 JetBrains Mono 字形，并在 <see cref="BundledFont.CollectionKey"/> 与
    /// <see cref="BundledFont.MonospaceCollectionKey"/> 下注册其字体集合。必须在第一个窗口创建前运行：
    /// 全局文本样式会解析 MiSans 字体族，缺失会让启动窗布局直接失败。失败只记录日志，调用方仍会显示
    /// 启动窗，此时其文本退回到平台可解析的字体。</para>
    /// </summary>
    private static void RegisterBundledFonts()
    {
        try
        {
            var faces = ZstdEmbeddedFontCollection.DecompressFaces();
            FontManager.Current.AddFontCollection(new ZstdEmbeddedFontCollection(faces));
            FontManager.Current.AddFontCollection(ZstdEmbeddedFontCollection.CreateMonospaceCollection());
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Startup", $"Bundled font registration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <summary>
    /// Kicks off the libusb runtime provisioning in the background. Device scanning may start
    /// before registration completes: the libusb backend reports itself unavailable first and
    /// re-enables once <c>LibUsbRuntime.SetLibraryPath</c> succeeds, which the loader documents
    /// as a supported path. Failures are logged only and never block startup.
    /// <para>在后台启动 libusb 运行库准备。设备扫描可能先于注册完成：libusb 后端先报告不可用，
    /// 待 <c>LibUsbRuntime.SetLibraryPath</c> 成功后重新启用，加载器文档明确支持该路径。
    /// 失败只记日志，绝不阻塞启动。</para>
    /// </summary>
    private static void ProvisionNativeRuntimeInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (ServiceHub.TryGet<NativeBinProvisioner>() is not { } provisioner)
                {
                    return;
                }

                var path = await provisioner.EnsureForCurrentProcessAsync().ConfigureAwait(false);
                if (path is not null)
                {
                    LogService.Instance.Write(LogLevel.Info, "Startup", $"libusb runtime provisioned at '{path}'.");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Warn, "Startup", $"libusb runtime provisioning failed: {ex.Message}");
            }
        });
    }

    /// Startup sequence: applies persisted settings and pre-builds the main window without waiting for device
    /// enumeration; the device panel starts the USB/debug-bridge scan in the background after the shell exists.
    /// <para>启动序列：应用持久化设置并在不等待设备枚举的情况下预构建主窗口；设备面板在 Shell 建好后于后台
    /// 启动 USB/调试桥扫描。</para>
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
        await ReportAsync(SplashPhase.Settings);
        var settings = await Task.Run(() => SettingsPersistenceService.Instance.LoadSettings()).ConfigureAwait(true);

        // Deferred one-time initialization: these steps used to run before the splash was created, which
        // delayed the first window. The settings load above already yielded to the dispatcher, so they only
        // affect the following phases. The bundled fonts stay ahead of the first window.
        Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        _ = PluginHost.Current.Capability;

        Comm.DeviceCommunicationConfiguration.Current = settings.DeviceCommunication;
        ProvisionNativeRuntimeInBackground();
        _ = RefreshFileAssociationsAsync([.. settings.AssociatedFileExtensions]);
        Firmware.Core.FirmwareParallelism.Workers = settings.FirmwareParallelism;
        _desktopServices = new DesktopApplicationServices();

        await ReportAsync(SplashPhase.ApplyingSettings);
        ApplyTheme(settings.ThemeMode);
        ApplyLanguage(settings.CultureName);
        ServiceHub.GetRequired<McpExposureService>().Apply(settings.McpExposeEnabled, settings.McpExposePort);
        ServiceHub.GetRequired<McpClientConnectionService>().Apply();

        // Device enumeration (the USB bus inventory plus the adb/HDC availability probes) is deliberately not
        // awaited here. The device panel starts it in the background once the shell exists, so the splash and
        // the main window never wait for the bus. ShellComposition reports Services/Nodes/Workspace next.
        MainViewModel? preparedModel = null;
        try
        {
            preparedModel = new MainViewModel(await ShellComposition.CreateAsync(
                phase => ReportAsync(phase), settings.CultureName));
            _preparedMainViewModel = preparedModel;
            await ReportAsync(SplashPhase.Window);
            var mainWindow = new MainWindow(preparedModel);
            mainWindow.InstallPrebuiltTopLevelPages();
            splashWindow.PreparedMainWindow = mainWindow;
            DrainPendingLaunches();
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

        await ReportAsync(SplashPhase.Ready);
        if (splashWindow.PreparedMainWindow is not null)
        {
            await splashWindow.PresentCompletionAsync();
        }

        DrainPendingLaunches();

        // The device panel starts the monitor on first access. Create it after the shell is ready so the first
        // enumeration runs after startup even when no binding evaluated it during window construction.
        _ = ViewModels.Shell.Devices.DeviceViewModel.EnsureInstance();
    }

    /// <summary>
    /// Repairs stale file associations in the background so a moved or rebuilt executable keeps opening its
    /// associated files; a failure only reaches the log.
    /// <para>在后台修复过期的文件关联，使移动或重建后的可执行文件仍能打开关联文件；失败只进日志。</para>
    /// </summary>
    /// <param name="extensions">The persisted file-association selection.<para>持久化的文件关联选择。</para></param>
    /// <returns>A task that completes when the repair attempt finished.<para>修复尝试结束后完成的任务。</para></returns>
    private static Task RefreshFileAssociationsAsync(IReadOnlyList<string> extensions) => Task.Run(() =>
    {
        try
        {
            FileAssociationService.EnsureRegistration(extensions);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "FileAssociation",
                $"Startup file-association refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    });

    /// <summary>
    /// Runs the startup update check when the setting enables it and opens the check window only when a newer
    /// release exists; every failure stays in the log so startup is never blocked.
    /// <para>设置启用时执行启动更新检查，只有存在更新的版本才打开检查窗口；所有失败只进日志，绝不阻断启动。</para>
    /// </summary>
    /// <returns>A task that completes when the automatic check finished. <para>自动检查结束时完成的任务。</para></returns>
    private static async Task RunAutomaticUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (ServiceHub.TryGet<AppUpdateService>() is not { } service)
            {
                return;
            }

            var settings = SettingsPersistenceService.Instance.LoadSettings();
            if (!settings.AutoCheckUpdates)
            {
                LogService.Instance.Write(LogLevel.Info, "Update", "The startup update check is disabled by settings.");
                return;
            }

            var result = await service.CheckAsync(settings.SkippedUpdateVersion).ConfigureAwait(false);
            if (!result.IsNewer || result.Package is null)
            {
                LogService.Instance.Write(LogLevel.Info, "Update",
                    $"The startup update check found no applicable update: newest {result.Release.Version}, " +
                    $"running {BuildInfo.Version}.");
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Update",
                $"The startup update check found {result.Release.Version} for {result.Package.Name}.");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ServiceHub.TryGet<CheckUpdateViewModel>()?.ApplyResult(result);
                Views.UiWindowService.Shared.ShowHostWindow(HostWindowKind.CheckUpdate);
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Update",
                $"The startup update check failed: {ex.GetType().Name}: {ex.Message}");
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

        LogService.Instance.FlushFileLog();
    }

    /// <summary>Releases application-wide monitoring only after desktop shutdown has been accepted.<para>仅在桌面关闭已被接受后释放应用级监视资源。</para></summary>
    /// <param name="sender">The desktop lifetime raising the exit event.<para>引发退出事件的桌面生命周期实例。</para></param>
    /// <param name="e">The exit arguments describing the accepted shutdown.<para>描述已接受关闭操作的退出参数。</para></param>
    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (sender is IClassicDesktopStyleApplicationLifetime desktop) desktop.Exit -= OnDesktopExit;
        ServiceHub.GetRequired<McpExposureService>().Stop();
        try { GetDesktopMainViewModel()?.Dispose(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Lifecycle", $"Closing views during shutdown failed: {ex.GetType().Name}: {ex.Message}"); }
        try { _desktopServices?.Dispose(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Lifecycle", ex.Message); }
        finally
        {
            _macApplicationMenu?.Dispose();
            _macApplicationMenu = null;
            _desktopServices = null;
            _instanceRelay?.Dispose();
            _instanceRelay = null;
            PrimaryInstanceRelay = null;
            WeakReferenceMessenger.Default.UnregisterAll(this);
            LogService.Instance.FlushFileLog();
        }
    }

    /// <summary>
    /// Applies startup file-association inputs: opens solutions and documents through the launch applier;
    /// failures only log and never block startup.
    /// <para>应用文件关联启动输入：经启动应用器打开解决方案与文档；失败仅记录日志且不阻断启动。</para>
    /// </summary>
    /// <param name="splashWindow">The splash window whose pre-built main window receives the inputs.<para>其预构建主窗口接收这些输入的启动窗。</para></param>
    /// <param name="files">The startup paths to open as a project or document.<para>作为工程或文档打开的启动路径。</para></param>
    private async Task ApplyStartupFilesAsync(MainSplashWindow splashWindow, string[] files)
    {
        try
        {
            if (splashWindow.PreparedMainWindow?.DataContext is not MainViewModel mainVm)
            {
                return;
            }

            await LaunchRequestApplier.ApplyAsync(mainVm, files).ConfigureAwait(true);
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
            if (splashWindow.PreparedMainWindow?.DataContext is MainViewModel mainVm)
            {
                ApplyScreenMirror(mainVm, serial);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "App", $"Startup screen mirror failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the screen-mirror document for the given device serial on the supplied main model.
    /// <para>在给定主模型上为设备序列号打开投屏文档。</para>
    /// </summary>
    /// <param name="mainVm">The desktop main model hosting the document.<para>承载该文档的桌面主模型。</para></param>
    /// <param name="serial">The device serial to mirror.<para>需要投屏的设备序列号。</para></param>
    private static void ApplyScreenMirror(MainViewModel mainVm, string serial)
    {
        if (mainVm.WorkspaceVM.OpenScreenMirrorDocument(serial) is { } mirror)
        {
            mirror.StartForDevice(serial);
            LogService.Instance.Write(LogLevel.Info, "App", $"Screen mirror requested for {serial}.");
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
            compare =>
            {
                if (message.Device is { } device)
                {
                    return compare.AddDevicePathAsync(device, message.Path);
                }

                return message.Path is { } path ? compare.AddComparisonPathAsync(path) : Task.CompletedTask;
            },
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
                if (!string.IsNullOrWhiteSpace(message.Serial))
                {
                    mirror.StartForDevice(message.Serial);
                }
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
    /// Binds the single-instance relay created by the desktop entry point to this application.
    /// <para>把桌面入口创建的单实例中继绑定到本应用。</para>
    /// </summary>
    private void AttachInstanceRelay()
    {
        if (PrimaryInstanceRelay is not { } relay)
        {
            return;
        }

        _instanceRelay = relay;
        relay.SetLaunchHandler(OnLaunchRequested);
    }

    /// <summary>
    /// Handles one forwarded launch on the UI thread; requests that arrive before the main model exists wait
    /// in <see cref="_pendingLaunches"/> and are applied once startup has prepared it.
    /// <para>在 UI 线程处理一条转发的启动请求；主模型尚未建立时先进入 <see cref="_pendingLaunches"/>，
    /// 待启动序列准备好后统一应用。</para>
    /// </summary>
    /// <param name="request">The forwarded launch.<para>转发的启动请求。</para></param>
    private void OnLaunchRequested(DesktopLaunchRequest request)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            _ = ApplyLaunchRequestAsync(request);
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ApplyLaunchRequestAsync(request));
    }

    private async Task ApplyLaunchRequestAsync(DesktopLaunchRequest request)
    {
        if (ResolveMainViewModel() is not { } main)
        {
            _pendingLaunches.Add(request);
            return;
        }

        try
        {
            ActivateMainWindow();
            await LaunchRequestApplier.ApplyAsync(main, request.Files).ConfigureAwait(true);
            if (request.ScreenMirrorSerial is { Length: > 0 } serial)
            {
                ApplyScreenMirror(main, serial);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Launch",
                $"Opening the forwarded launch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies launches that were queued before the main model existed.
    /// <para>应用主模型建立之前排队的启动请求。</para>
    /// </summary>
    private void DrainPendingLaunches()
    {
        if (_pendingLaunches.Count == 0)
        {
            return;
        }

        var pending = _pendingLaunches.ToArray();
        _pendingLaunches.Clear();
        _ = DrainPendingLaunchesAsync(pending);
    }

    private async Task DrainPendingLaunchesAsync(DesktopLaunchRequest[] pending)
    {
        foreach (var request in pending)
        {
            await ApplyLaunchRequestAsync(request).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Brings the desktop main window back to the foreground for a forwarded launch.
    /// <para>为转发的启动请求把桌面主窗口带回前台。</para>
    /// </summary>
    private void ActivateMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Resolves the main model before the desktop lifetime has swapped the main window to the shell, so a
    /// launch that arrives during startup is not dropped.
    /// <para>在桌面生命周期尚未把主窗口切换为外壳之前解析主模型，使启动期间到达的请求不会丢失。</para>
    /// </summary>
    /// <returns>The bound or pre-built main model, or null.<para>已绑定或预构建的主模型；无则为 null。</para></returns>
    private MainViewModel? ResolveMainViewModel() => GetDesktopMainViewModel() ?? _preparedMainViewModel;

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

    private Task RunCompareRequestAsync(string error, Func<ViewModels.Editors.MultiColumnBrowser.MultiColumnBrowserViewModel, Task> operation)
        => RunCompareRequestAsync(error, operation, openInNewTab: false);

    private Task RunCompareRequestAsync(
        string error,
        Func<ViewModels.Editors.MultiColumnBrowser.MultiColumnBrowserViewModel, Task> operation,
        bool openInNewTab)
        => RunMainRequestAsync(error, main =>
        {
            var compare = openInNewTab
                ? main.WorkspaceVM.OpenNewCompareDocument()
                : main.WorkspaceVM.OpenCompareDocument();
            return compare is { } document ? operation(document) : Task.CompletedTask;
        });

    /// <summary>
    /// Applies rounded window corners on Linux, where drawn decorations replace the window manager frame.
    /// <para>在 Linux 上为窗口启用圆角：自绘装饰取代窗口管理器边框后，原生圆角随之消失。</para>
    /// </summary>
    /// <remarks>
    /// Only Linux is affected: Windows and macOS keep their native window frames, which already round the
    /// corners themselves. The transparency hint is applied via an application-level style so that native
    /// X11/Wayland surfaces request an alpha channel before window creation; when the compositor does not
    /// grant transparency, <see cref="Views.Behaviors.WindowRoundingBehavior"/> falls back to square corners.
    /// <para>仅 Linux 需要处理：Windows 与 macOS 保留原生窗口边框，圆角由系统自行绘制。透明提示通过应用级样式
    /// 在原生窗口创建前声明，使 X11/Wayland 表面请求 alpha 通道；若合成器未授予透明表面，
    /// <see cref="Views.Behaviors.WindowRoundingBehavior"/> 会自动回退直角。</para>
    /// </remarks>
    private void InstallWindowRounding()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var transparencyStyle = new Style(x => x.OfType<Window>());
        transparencyStyle.Setters.Add(new Setter(
            TopLevel.TransparencyLevelHintProperty,
            new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None }));
        Styles.Add(transparencyStyle);

        var borderRoundingStyle = new Style(x => x
            .OfType<Window>()
            .Class(Views.Behaviors.WindowRoundingBehavior.RoundedClassName)
            .Descendant()
            .OfType<WindowDrawnDecorations>()
            .Template()
            .OfType<Border>()
            .Name("PART_WindowBorder"));
        borderRoundingStyle.Setters.Add(new Setter(
            Border.CornerRadiusProperty,
            new CornerRadius(Views.Behaviors.WindowRoundingBehavior.CornerRadius)));
        Styles.Add(borderRoundingStyle);

        Window.WindowOpenedEvent.AddClassHandler<Window>(
            static (window, _) => Views.Behaviors.WindowRoundingBehavior.Attach(window));
    }

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