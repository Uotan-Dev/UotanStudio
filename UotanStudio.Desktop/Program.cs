using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using Dock.Settings;
using UotanStudio.Services;

namespace UotanStudio.Desktop;

/// <summary>Desktop application entry point: process bootstrap and startup-mode dispatch.<para>桌面应用入口：进程引导与启动模式分发。</para></summary>
sealed class Program
{
    /// <summary>
    /// Process entry point that dispatches the startup mode.
    /// <para>进程入口，分发启动模式。</para>
    /// </summary>
    /// <param name="args">
    /// Launch arguments; an empty array starts the desktop application, other switches select headless modes.
    /// <para>启动参数；空数组启动桌面应用，其他开关选择无头模式。</para>
    /// </param>
    /// <returns>
    /// 0 when the selected mode finishes or is cancelled, and 1 when a managed device-shell request fails.
    /// <para>所选模式正常结束或被取消时返回 0；托管设备 shell 请求失败时返回 1。</para>
    /// </returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var settings = Services.SettingsPersistenceService.Instance.LoadSettings();
        Comm.DeviceCommunicationConfiguration.Current = settings.DeviceCommunication;

        if (args is ["--managed-device-shell", var protocolName, var serial])
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            var output = Console.OpenStandardOutput();
            void Report(string text)
            {
                try
                {
                    var line = System.Text.Encoding.UTF8.GetBytes(text + "\r\n");
                    output.Write(line, 0, line.Length);
                    output.Flush();
                }
                catch (IOException) { }
            }

            try
            {
                Report($"[UotanStudio] Opening the {protocolName} shell for {serial}.");
                if (!Enum.TryParse<Services.Devices.DeviceProtocolKind>(protocolName, out var protocol) ||
                    protocol is not (Services.Devices.DeviceProtocolKind.Adb or
                        Services.Devices.DeviceProtocolKind.Hdc))
                    throw new ArgumentException("An ADB or HDC protocol is required.");
                using var manager = Services.Devices.DeviceMonitorService.Create();
                Report("[UotanStudio] Resolving the target; a full device scan runs in this process.");
                var target = manager.Scan().SingleOrDefault(candidate => candidate.Protocol == protocol &&
                    string.Equals(candidate.Serial, serial, StringComparison.Ordinal))
                    ?? throw new IOException("The selected server target is no longer available.");
                Report("[UotanStudio] Target resolved; opening the interactive shell.");
                manager.RunInteractiveShellAsync(target, Console.OpenStandardInput(), output, cts.Token)
                    .GetAwaiter().GetResult();
                return 0;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return 0; }
            catch (Exception error)
            {
                Report("[UotanStudio] The device shell could not be opened: " + error.Message);
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (Mcp.McpLauncher.IsHeadlessRequested(args))
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            try
            {
                Mcp.McpLauncher.RunHeadlessAsync(args, cts.Token, Services.Mcp.McpToolCallLog.Instance).GetAwaiter().GetResult();
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        if (Mcp.McpLauncher.IsCompanionRequested(args))
        {
            _ = Task.Run(() => Mcp.McpLauncher.RunCompanionAsync(
                args, new Services.Mcp.DesktopDeviceManagerGateway(),
                () => new Services.Delivery.DeliveryKeyProvider(),
                audit: Services.Mcp.McpToolCallLog.Instance));
        }

        var mirrorFlag = Array.IndexOf(args, "--open-screen-mirror");
        if (mirrorFlag >= 0 && mirrorFlag + 1 < args.Length)
        {
            App.StartupScreenMirrorSerial = args[mirrorFlag + 1];
        }

        App.StartupFiles = [.. args.Where(File.Exists)];
        BuildAvaloniaApp(settings).StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Configures and returns the Avalonia application builder using the persisted rendering preferences.
    /// <para>使用持久化的渲染偏好配置并返回 Avalonia 应用构建器。</para>
    /// </summary>
    /// <returns>The configured Avalonia <see cref="AppBuilder"/>. <para>配置完成的 Avalonia <see cref="AppBuilder"/>。</para></returns>
    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(Services.SettingsPersistenceService.Instance.LoadSettings());

    /// <summary>
    /// Configures and returns the Avalonia application builder from the supplied settings.
    /// <para>根据给定的设置配置并返回 Avalonia 应用构建器。</para>
    /// </summary>
    /// <param name="settings">
    /// Persisted settings supplying the rendering preferences.
    /// <para>提供渲染偏好的持久化设置。</para>
    /// </param>
    /// <returns>The configured Avalonia <see cref="AppBuilder"/>. <para>配置完成的 Avalonia <see cref="AppBuilder"/>。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="settings"/> is null.
    /// <para>当 <paramref name="settings"/> 为 null 时抛出。</para>
    /// </exception>
    public static AppBuilder BuildAvaloniaApp(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        builder = ApplyWindowingBackend(builder);
        builder = ApplyRenderingPreferences(builder, settings);
        builder = builder
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:UotanStudio/MiSans#MiSans"
            })
            .WithDockSettings(new DockSettingsOptions
            {
                CommandBarMergingEnabled = true,
                CommandBarMergingScope = DockCommandBarMergingScope.ActiveDocument
            });

        Logger.Sink = new FilteredTraceLogSink();
        return builder;
    }

    /// <summary>
    /// Selects the native Wayland windowing backend on Linux when a compositor is reachable, keeping the X11
    /// backend otherwise.
    /// <para>Linux 上在可连接合成器时选择原生 Wayland 窗口后端，否则保持 X11 后端。</para>
    /// </summary>
    /// <param name="builder">The application builder being configured.<para>正在配置的应用构建器。</para></param>
    /// <returns>The builder carrying the windowing backend.<para>携带窗口后端的构建器。</para></returns>
    private static AppBuilder ApplyWindowingBackend(AppBuilder builder) =>
        OperatingSystem.IsLinux() ? UseLinuxWayland(builder) : builder;

    /// <summary>
    /// Enables Wayland with an X11 fallback. The platform types stay in this non-inlined method so the NativeAOT
    /// trimmer removes the Wayland assemblies from Windows and macOS publishes.
    /// <para>启用带 X11 回退的 Wayland。平台类型留在这个非内联方法中，使 NativeAOT 裁剪器能从 Windows 与 macOS
    /// 发布中移除 Wayland 程序集。</para>
    /// </summary>
    /// <param name="builder">The application builder being configured.<para>正在配置的应用构建器。</para></param>
    /// <returns>The builder with the Linux windowing backend configured.<para>配置了 Linux 窗口后端的构建器。</para></returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AppBuilder UseLinuxWayland(AppBuilder builder) => builder.UseWaylandWithFallback();

    /// <summary>
    /// Applies the persisted rendering backend and composition mode to the platform options, falling back to
    /// the automatic choice when the running platform does not accept them.
    /// <para>把持久化的渲染后端与合成模式应用到平台选项；当前平台不接受时回退到自动。</para>
    /// </summary>
    /// <param name="builder">The application builder being configured.<para>正在配置的应用构建器。</para></param>
    /// <param name="settings">The persisted settings.<para>持久化设置。</para></param>
    /// <returns>The builder carrying the platform options.<para>携带平台选项的构建器。</para></returns>
    private static AppBuilder ApplyRenderingPreferences(AppBuilder builder, AppSettings settings)
    {
        var platform = RenderingPreferenceCatalog.CurrentPlatform;
        var backend = RenderingPreferenceCatalog.Normalize(platform, settings.RenderingBackend);
        var composition = RenderingPreferenceCatalog.NormalizeComposition(
            platform, settings.RenderingCompositionMode);
        return platform switch
        {
            RenderingPlatform.Windows => ApplyWindowsRendering(builder, backend, composition),
            RenderingPlatform.LinuxX11 => ApplyX11Rendering(builder, backend),
            RenderingPlatform.MacOS => ApplyNativeRendering(builder, backend),
            _ => builder,
        };
    }

    /// <summary>
    /// Applies the Win32 rendering and composition options; composition is only requested for the automatic and
    /// ANGLE/EGL backends, which are the only paths that register a composition host.
    /// <para>应用 Win32 渲染与合成选项；仅自动与 ANGLE/EGL 后端请求合成模式，因为只有这些路径会注册合成宿主。</para>
    /// </summary>
    /// <param name="builder">The application builder being configured.<para>正在配置的应用构建器。</para></param>
    /// <param name="backend">The normalized backend.<para>规范化后的后端。</para></param>
    /// <param name="composition">The normalized composition mode.<para>规范化后的合成模式。</para></param>
    /// <returns>The configured builder.<para>配置完成的构建器。</para></returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AppBuilder ApplyWindowsRendering(
        AppBuilder builder, RenderingBackend backend, RenderingCompositionMode composition)
    {
        if (backend == RenderingBackend.Auto && composition == RenderingCompositionMode.Auto)
        {
            return builder;
        }

        var options = new Win32PlatformOptions();
        if (WindowsRenderingModes(backend) is { } renderingModes)
        {
            options.RenderingMode = renderingModes;
        }

        if (backend is RenderingBackend.Auto or RenderingBackend.AngleEgl &&
            WindowsCompositionModes(composition) is { } compositionModes)
        {
            options.CompositionMode = compositionModes;
        }

        return builder.With(options);
    }

    /// <summary>
    /// Applies the X11 rendering option, which is the Linux platform selected by <c>UsePlatformDetect</c>.
    /// <para>应用 X11 渲染选项，即 <c>UsePlatformDetect</c> 选定的 Linux 平台。</para>
    /// </summary>
    /// <param name="builder">The application builder being configured.<para>正在配置的应用构建器。</para></param>
    /// <param name="backend">The normalized backend.<para>规范化后的后端。</para></param>
    /// <returns>The configured builder.<para>配置完成的构建器。</para></returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AppBuilder ApplyX11Rendering(AppBuilder builder, RenderingBackend backend) =>
        X11RenderingModes(backend) is { } renderingModes
            ? builder.With(new X11PlatformOptions { RenderingMode = renderingModes })
            : builder;

    /// <summary>
    /// Applies the Avalonia.Native rendering option used by macOS.
    /// <para>应用 macOS 使用的 Avalonia.Native 渲染选项。</para>
    /// </summary>
    /// <param name="builder">The application builder being configured.<para>正在配置的应用构建器。</para></param>
    /// <param name="backend">The normalized backend.<para>规范化后的后端。</para></param>
    /// <returns>The configured builder.<para>配置完成的构建器。</para></returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AppBuilder ApplyNativeRendering(AppBuilder builder, RenderingBackend backend) =>
        NativeRenderingModes(backend) is { } renderingModes
            ? builder.With(new AvaloniaNativePlatformOptions { RenderingMode = renderingModes })
            : builder;

    /// <summary>
    /// Builds the Win32 fallback list for the requested backend, always ending with software rendering.
    /// <para>构造请求后端对应的 Win32 回退列表，始终以软件渲染兜底。</para>
    /// </summary>
    /// <param name="backend">The normalized backend.<para>规范化后的后端。</para></param>
    /// <returns>The ordered modes, or null to keep the platform default.<para>有序模式；null 表示保持平台默认。</para></returns>
    private static IReadOnlyList<Win32RenderingMode>? WindowsRenderingModes(RenderingBackend backend) => backend switch
    {
        RenderingBackend.AngleEgl => [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
        RenderingBackend.Software => [Win32RenderingMode.Software],
        RenderingBackend.Wgl => [Win32RenderingMode.Wgl, Win32RenderingMode.Software],
        RenderingBackend.Vulkan => [Win32RenderingMode.Vulkan, Win32RenderingMode.Software],
        _ => null,
    };

    /// <summary>
    /// Builds the Win32 composition fallback list for the requested mode, always ending with the redirection surface.
    /// <para>构造请求合成模式对应的 Win32 回退列表，始终以重定向表面兜底。</para>
    /// </summary>
    /// <param name="composition">The normalized composition mode.<para>规范化后的合成模式。</para></param>
    /// <returns>The ordered modes, or null to keep the platform default.<para>有序模式；null 表示保持平台默认。</para></returns>
    private static IReadOnlyList<Win32CompositionMode>? WindowsCompositionModes(
        RenderingCompositionMode composition) => composition switch
        {
            RenderingCompositionMode.WinUIComposition =>
                [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface],
            RenderingCompositionMode.DirectComposition =>
                [Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface],
            RenderingCompositionMode.LowLatencyDxgiSwapChain =>
                [Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.RedirectionSurface],
            RenderingCompositionMode.RedirectionSurface => [Win32CompositionMode.RedirectionSurface],
            _ => null,
        };

    /// <summary>
    /// Builds the X11 fallback list for the requested backend, always ending with software rendering.
    /// <para>构造请求后端对应的 X11 回退列表，始终以软件渲染兜底。</para>
    /// </summary>
    /// <param name="backend">The normalized backend.<para>规范化后的后端。</para></param>
    /// <returns>The ordered modes, or null to keep the platform default.<para>有序模式；null 表示保持平台默认。</para></returns>
    private static IReadOnlyList<X11RenderingMode>? X11RenderingModes(RenderingBackend backend) => backend switch
    {
        RenderingBackend.Egl => [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
        RenderingBackend.Glx => [X11RenderingMode.Glx, X11RenderingMode.Software],
        RenderingBackend.Software => [X11RenderingMode.Software],
        RenderingBackend.Vulkan => [X11RenderingMode.Vulkan, X11RenderingMode.Software],
        _ => null,
    };

    /// <summary>
    /// Builds the Avalonia.Native fallback list for the requested backend, always ending with software rendering.
    /// <para>构造 Avalonia.Native 后端对应的回退列表，始终以软件渲染兜底。</para>
    /// </summary>
    /// <param name="backend">The normalized backend.<para>规范化后的后端。</para></param>
    /// <returns>The ordered modes, or null to keep the platform default.<para>有序模式；null 表示保持平台默认。</para></returns>
    private static IReadOnlyList<AvaloniaNativeRenderingMode>? NativeRenderingModes(
        RenderingBackend backend) => backend switch
        {
            RenderingBackend.Metal =>
                [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl,
                    AvaloniaNativeRenderingMode.Software],
            RenderingBackend.OpenGl =>
                [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
            RenderingBackend.Software => [AvaloniaNativeRenderingMode.Software],
            _ => null,
        };

    /// <summary>Writes warnings to Trace while filtering known empty-Dock binding noise.<para>把警告写入 Trace，并过滤已知的空 Dock 绑定噪声。</para></summary>
    private sealed class FilteredTraceLogSink : ILogSink
    {
        /// <summary>Reports whether an event in this area should be recorded.<para>报告该区域的事件是否应被记录。</para></summary>
        /// <param name="level">Severity level of the event. <para>事件的严重级别。</para></param>
        /// <param name="area">Logging area of the event. <para>事件所属的日志区域。</para></param>
        /// <returns>True when the event should be written. <para>需要写入该事件时为 true。</para></returns>
        public bool IsEnabled(LogEventLevel level, string area)
        {
            if (level <= LogEventLevel.Warning && string.Equals(area, LogArea.Binding, StringComparison.Ordinal))
            {
                return false;
            }

            return level >= LogEventLevel.Warning;
        }

        /// <summary>Writes one log event to the trace listener when it passes the filter.<para>事件通过筛选时把它写入跟踪监听器。</para></summary>
        /// <param name="level">Severity level of the event. <para>事件的严重级别。</para></param>
        /// <param name="area">Logging area of the event. <para>事件所属的日志区域。</para></param>
        /// <param name="source">Source object of the event, unused here. <para>事件的来源对象，此处不使用。</para></param>
        /// <param name="messageTemplate">Message template of the event. <para>事件的消息模板。</para></param>
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (!IsEnabled(level, area))
            {
                return;
            }

            System.Diagnostics.Trace.WriteLine($"{area}: {messageTemplate}");
        }

        /// <summary>Writes one parameterised log event to the trace listener when it passes the filter.<para>带参数的事件通过筛选时把它写入跟踪监听器。</para></summary>
        /// <param name="level">Severity level of the event. <para>事件的严重级别。</para></param>
        /// <param name="area">Logging area of the event. <para>事件所属的日志区域。</para></param>
        /// <param name="source">Source object of the event, unused here. <para>事件的来源对象，此处不使用。</para></param>
        /// <param name="messageTemplate">Message template of the event. <para>事件的消息模板。</para></param>
        /// <param name="propertyValues">Values substituted into the template placeholders. <para>代入模板占位符的参数值。</para></param>
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, object?[] propertyValues)
        {
            if (!IsEnabled(level, area))
            {
                return;
            }

            var message = propertyValues is { Length: > 0 }
                ? FormatTemplateSafely(messageTemplate, propertyValues)
                : messageTemplate;
            System.Diagnostics.Trace.WriteLine($"{area}: {message}");
        }

        /// <summary>
        /// Formats a log template, falling back to the raw template with appended values when named placeholders make indexed formatting fail.
        /// <para>格式化日志模板；当命名占位符使索引格式化失败时，回退为模板原文并追加参数值。</para>
        /// </summary>
        /// <param name="template">Message template, possibly with named placeholders. <para>可能带命名占位符的消息模板。</para></param>
        /// <param name="values">Property values referenced by the template. <para>模板引用的属性值。</para></param>
        /// <returns>The formatted message, or the template plus values on failure. <para>格式化后的消息，失败时为模板与参数值。</para></returns>
        private static string FormatTemplateSafely(string template, object?[] values)
        {
            try
            {
                return string.Format(template, values);
            }
            catch (FormatException)
            {
                return $"{template} [{string.Join(", ", values)}]";
            }
        }
    }

}