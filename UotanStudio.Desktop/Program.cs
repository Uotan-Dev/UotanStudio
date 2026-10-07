using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using Dock.Settings;

namespace UotanStudio.Desktop;

/// <summary>
/// Desktop application entry point: host process bootstrap and startup-mode dispatch.
/// <para>桌面应用入口：宿主进程引导与启动模式分发。</para>
/// </summary>
sealed class Program
{
    /// <summary>
    /// Process entry point that dispatches startup mode (headless MCP, CLI, companion MCP, GUI).
    /// <para>进程入口，分发启动模式（无头 MCP、CLI、伴随 MCP、GUI）。</para>
    /// </summary>
    /// <param name="args">
    /// Launch arguments; an empty array starts the GUI. Recognized forms are
    /// <c>--managed-device-shell &lt;protocol&gt; &lt;serial&gt;</c>, <c>--mcp-headless</c>,
    /// <c>--mcp</c>, <c>--open-screen-mirror &lt;serial&gt;</c>, and existing file paths, which the GUI
    /// opens on startup.
    /// <para>启动参数；空数组直接启动图形界面。可识别的形式包括
    /// <c>--managed-device-shell &lt;协议&gt; &lt;序列号&gt;</c>、<c>--mcp-headless</c>、<c>--mcp</c>、
    /// <c>--open-screen-mirror &lt;序列号&gt;</c>，以及由图形界面在启动时打开的既有文件路径。</para>
    /// </param>
    /// <returns>
    /// 0 when the selected mode finishes or is cancelled, and 1 when a managed device-shell request fails.
    /// <para>所选模式正常结束或被取消时返回 0；托管设备 shell 请求失败时返回 1。</para>
    /// </returns>
    [STAThread]
    public static int Main(string[] args)
    {

        if (args is ["--managed-device-shell", var protocolName, var serial])
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            try
            {
                if (!Enum.TryParse<UotanStudio.Services.Devices.DeviceProtocolKind>(protocolName, out var protocol) ||
                    protocol is not (UotanStudio.Services.Devices.DeviceProtocolKind.Adb or
                        UotanStudio.Services.Devices.DeviceProtocolKind.Hdc))
                    throw new ArgumentException("An ADB or HDC protocol is required.");
                using var manager = UotanStudio.Services.Devices.DeviceMonitorService.Create();
                var target = manager.Scan().SingleOrDefault(candidate => candidate.Protocol == protocol &&
                    string.Equals(candidate.Serial, serial, StringComparison.Ordinal))
                    ?? throw new IOException("The selected server target is no longer available.");
                manager.RunInteractiveShellAsync(target, Console.OpenStandardInput(),
                    Console.OpenStandardOutput(), cts.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return 0; }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (UotanStudio.Mcp.McpLauncher.IsHeadlessRequested(args))
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            try
            {
                UotanStudio.Mcp.McpLauncher.RunHeadlessAsync(args, cts.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        if (UotanStudio.Mcp.McpLauncher.IsCompanionRequested(args))
        {
            _ = Task.Run(() => UotanStudio.Mcp.McpLauncher.RunCompanionAsync(
                args, new UotanStudio.Services.Mcp.DesktopDeviceManagerGateway(),
                () => new UotanStudio.Services.Delivery.DeliveryKeyProvider()));
        }

        int mirrorFlag = Array.IndexOf(args, "--open-screen-mirror");
        if (mirrorFlag >= 0 && mirrorFlag + 1 < args.Length)
        {
            App.StartupScreenMirrorSerial = args[mirrorFlag + 1];
        }

        App.StartupFiles = [.. args.Where(File.Exists)];
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Configures and returns the Avalonia application builder.
    /// <para>配置并返回 Avalonia 应用构建器。</para>
    /// </summary>
    /// <returns>The configured Avalonia <see cref="AppBuilder"/>. <para>配置完成的 Avalonia <see cref="AppBuilder"/>。</para></returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://UotanStudio/Assets/Fonts#MiSans"
            })
            .WithDockSettings(new DockSettingsOptions
            {
                CommandBarMergingEnabled = true,
                CommandBarMergingScope = DockCommandBarMergingScope.ActiveDocument
            });

        Logger.Sink = new FilteredTraceLogSink();
        return builder;
    }

    /// <summary>Writes warnings to Trace while filtering known empty-Dock binding noise.</summary>
    private sealed class FilteredTraceLogSink : ILogSink
    {
        /// <inheritdoc />
        public bool IsEnabled(LogEventLevel level, string area)
        {
            if (level <= LogEventLevel.Warning && string.Equals(area, LogArea.Binding, StringComparison.Ordinal))
            {
                return false;
            }

            return level >= LogEventLevel.Warning;
        }

        /// <inheritdoc />
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (!IsEnabled(level, area))
            {
                return;
            }

            System.Diagnostics.Trace.WriteLine($"{area}: {messageTemplate}");
        }

        /// <inheritdoc />
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
        /// <para>安全格式化日志模板：Avalonia 模板可能使用命名占位符（如 <c>{Binding}</c>），
        /// 与 <see cref="string.Format(string, object?[])"/> 的索引占位符不兼容，解析会抛
        /// <see cref="FormatException"/>；失败时回退为模板原文拼接参数值，保证不崩溃。</para>
        /// Formats a log template safely: Avalonia templates may use named placeholders
        /// (e.g. <c>{Binding}</c>) that are incompatible with <see cref="string.Format(string, object?[])"/>
        /// index placeholders and would throw <see cref="FormatException"/>; on failure the raw
        /// template plus its property values are emitted instead so logging never crashes.
        /// </summary>
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