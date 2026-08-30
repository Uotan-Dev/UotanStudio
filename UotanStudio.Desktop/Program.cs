using Avalonia;
using Avalonia.Logging;
using Dock.Settings;
using UotanStudio.Services;
using UotanStudio.Services.Cli;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace UotanStudio.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // 固件 CLI 子命令：不启动 Avalonia，直接走命令行解包/打包并返回退出码。
        // Firmware CLI subcommand: do not start Avalonia; run the CLI and return its exit code.
        if (args.Length > 0 && args[0] == "firmware")
        {
            return FirmwareCli.Run(args[1..]);
        }

        // 工作流 CLI 子命令：无头执行节点工作流并返回退出码。
        // Workflow CLI subcommand: run a node workflow headlessly and return its exit code.
        if (args.Length > 0 && args[0] == "workflow")
        {
            return WorkflowCli.Run(args[1..]);
        }

        // 解析命令行参数（打开解决方案 / 导入文件 / 帮助 / 版本），其余参数仍传给 Avalonia。
        // Parse command-line options (open solution / import files / help / version); the rest are
        // still forwarded to Avalonia.
        var options = CommandLineParser.Parse(args);
        if (options.HelpRequested)
        {
            PrintHelp();
            return 0;
        }

        if (options.VersionRequested)
        {
            PrintVersion();
            return 0;
        }

        if (options.RegisterAssociations)
        {
            Console.WriteLine(FileAssociationService.Register()
                ? "File associations registered.  文件关联已注册。"
                : "Failed to register file associations.  文件关联注册失败。");
            return 0;
        }

        if (options.UnregisterAssociations)
        {
            Console.WriteLine(FileAssociationService.Unregister()
                ? "File associations unregistered.  文件关联已注销。"
                : "Failed to unregister file associations.  文件关联注销失败。");
            return 0;
        }

        App.StartupOptions = options;
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithDockSettings(new DockSettingsOptions
            {
                CommandBarMergingEnabled = true,
                CommandBarMergingScope = DockCommandBarMergingScope.ActiveDocument
            });

        // Dock.Avalonia 主题模板在空态（Layout/ActiveDockable 为 null）下会触发大量
        // "Value is null" 绑定警告（库已知噪音，功能无影响），用过滤 sink 静音。
        // Dock.Avalonia theme templates emit many "Value is null" binding warnings in the empty
        // state (null Layout/ActiveDockable) — known library noise with no functional impact,
        // silenced via a filtered sink.
        Logger.Sink = new FilteredTraceLogSink();
#if DEBUG
        // 启动命名管道 MCP 诊断服务器，供 AI 助手/CLI 在线检查与交互当前运行的 Avalonia 应用。
        builder = builder.UseMcpDiagnostics();
#endif
        return builder;
    }

    /// <summary>
    /// <para>把 Avalonia 日志输出到 <see cref="System.Diagnostics.Trace"/>，但过滤
    /// <see cref="LogArea.Binding"/> 类别的 Warning 及以下级别（Dock 模板空态绑定噪音）。
    /// <c>Log</c> 方法内再次执行同样的过滤：AvaloniaMcp 诊断的 <c>BindingErrorLogSink</c>
    /// 会绕过 <c>IsEnabled</c> 直接转发消息，若不加防御则噪音会漏出并在格式化时崩溃。</para>
    /// Writes Avalonia logs to <see cref="System.Diagnostics.Trace"/> while filtering out
    /// <see cref="LogArea.Binding"/> messages at Warning level and below (Dock template
    /// empty-state binding noise). The same filter is re-applied inside <c>Log</c>: the
    /// AvaloniaMcp diagnostic <c>BindingErrorLogSink</c> forwards messages without consulting
    /// <c>IsEnabled</c>, so without this guard the noise would leak through and crash while
    /// formatting.
    /// </summary>
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

    private static void PrintHelp()
    {
        Console.WriteLine("UotanStudio - Android firmware tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  UotanStudio [options] [<solution.uotan>] [<file>...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --open <path>     Open a .uotan solution on startup");
        Console.WriteLine("  -i, --import <file>   Import a file into the opened solution (repeatable)");
        Console.WriteLine("  -h, --help            Show this help and exit");
        Console.WriteLine("  -v, --version         Show version and exit");
        Console.WriteLine("  --register-file-associations    Register .uotan/.uotanfw/.uotannode associations and exit");
        Console.WriteLine("  --unregister-file-associations  Remove the associations and exit");
        Console.WriteLine();
        Console.WriteLine("A positional <path>.uotan argument opens a solution; other positional files are imported.");
    }

    private static void PrintVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine($"UotanStudio {version?.ToString(3) ?? "1.0.0"}");
    }
}