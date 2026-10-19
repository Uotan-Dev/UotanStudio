using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using Dock.Settings;

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
                if (!Enum.TryParse<Services.Devices.DeviceProtocolKind>(protocolName, out var protocol) ||
                    protocol is not (Services.Devices.DeviceProtocolKind.Adb or
                        Services.Devices.DeviceProtocolKind.Hdc))
                    throw new ArgumentException("An ADB or HDC protocol is required.");
                using var manager = Services.Devices.DeviceMonitorService.Create();
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