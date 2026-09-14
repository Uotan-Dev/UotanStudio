using Avalonia;
using Avalonia.Logging;
using Dock.Settings;

namespace UotanStudio.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // File association activation only; firmware and workflow command dispatch is archived.
        App.StartupFiles = args.Where(File.Exists).ToArray();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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