using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace UotanStudio.Services.Logging;

/// <summary>
/// Routes <see cref="Microsoft.Extensions.Logging"/> producers into the unified log store, so components that
/// only speak <see cref="ILogger"/> — the Model Context Protocol SDK — land in the same window instead of a
/// null logger that discards everything.
/// <para>将 <see cref="Microsoft.Extensions.Logging"/> 生产者路由到统一日志存储，使只使用
/// <see cref="ILogger"/> 的组件（Model Context Protocol SDK）进入同一个日志窗口，而不是丢弃全部内容的空 logger。</para>
/// </summary>
/// <remarks>
/// The bridge is one-way on purpose: the application-facing API stays <see cref="IUnifiedLog"/>, filtering
/// reuses <see cref="LogService"/>'s single minimum level and therefore follows the debug-mode switch, and
/// MEL scopes are not rendered because the store has no scope concept. No <see cref="ILoggerProvider"/>
/// pipeline is consulted, so <see cref="AddProvider"/> is a no-op and neither <c>Microsoft.Extensions.Logging</c>
/// nor the options package is pulled into the host.
/// <para>桥接刻意只做成单向：面向应用的 API 仍是 <see cref="IUnifiedLog"/>，过滤沿用 <see cref="LogService"/>
/// 的唯一最低级别（因此跟随调试模式开关），且因为存储没有作用域概念，MEL 的作用域不会被渲染。这里不查询任何
/// <see cref="ILoggerProvider"/> 管道，所以 <see cref="AddProvider"/> 是空操作，宿主也不会因此引入
/// <c>Microsoft.Extensions.Logging</c> 或选项包。</para>
/// </remarks>
internal sealed class UnifiedLogLoggerFactory : ILoggerFactory
{
    /// <summary>Factory writing into the process-wide <see cref="LogService.Instance"/> singleton.
    /// <para>写入进程级 <see cref="LogService.Instance"/> 单例的工厂。</para></summary>
    internal static ILoggerFactory Shared { get; } = new UnifiedLogLoggerFactory(LogService.Instance);

    /// <summary>Source recorded when a producer supplies a blank category.
    /// <para>生产者给出空分类名时记录的来源。</para></summary>
    internal const string DefaultSource = "Microsoft.Extensions.Logging";

    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    /// <summary>Creates a factory writing through <paramref name="log"/>.
    /// <para>创建通过 <paramref name="log"/> 写入的工厂。</para></summary>
    /// <param name="log">The store receiving every routed entry. <para>接收所有被路由条目的存储。</para></param>
    internal UnifiedLogLoggerFactory(LogService log)
    {
        Log = log;
    }

    /// <summary>Gets the store every logger created here writes through.
    /// <para>获取此处创建的每个 logger 写入的目标存储。</para></summary>
    internal LogService Log { get; }

    /// <summary>Gets the logger for one MEL category; the category becomes the log entry's source.
    /// <para>获取某个 MEL 分类的 logger；分类名即日志条目的来源。</para></summary>
    /// <param name="categoryName">The producer's category name; blank yields <see cref="DefaultSource"/>.
    /// <para>生产者的分类名；空白时使用 <see cref="DefaultSource"/>。</para></param>
    /// <returns>A cached logger bound to <paramref name="categoryName"/>.
    /// <para>绑定到 <paramref name="categoryName"/> 的缓存 logger。</para></returns>
    public ILogger CreateLogger(string categoryName)
    {
        var source = string.IsNullOrWhiteSpace(categoryName) ? DefaultSource : categoryName;
        return _loggers.GetOrAdd(source, static (key, factory) => new UnifiedLogLogger(factory.Log, key), this);
    }

    /// <summary>Ignores the registration; this factory never dispatches through providers.
    /// <para>忽略该注册；本工厂从不通过 provider 分发。</para></summary>
    /// <param name="provider">The provider to ignore. <para>被忽略的 provider。</para></param>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <summary>Drops the cached loggers; later <see cref="CreateLogger"/> calls still work.
    /// <para>丢弃缓存的 logger；后续 <see cref="CreateLogger"/> 调用仍然可用。</para></summary>
    public void Dispose() => _loggers.Clear();

    private sealed class UnifiedLogLogger(LogService log, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(MelLogLevel logLevel)
        {
            var mapped = Map(logLevel);
            return mapped is not null && log.IsEnabled(mapped.Value);
        }

        /// <remarks>
        /// MEL hands the exception to the sink separately: the formatter's text never carries it, so a bridge
        /// that ignores this argument silently drops every stack trace the producer reported.
        /// <para>MEL 会把异常单独交给 sink：格式化文本从不携带异常，忽略该参数的桥接器会静默丢弃
        /// 生产方报告的全部堆栈。</para>
        /// </remarks>
        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var mapped = Map(logLevel);
            if (mapped is null || !log.IsEnabled(mapped.Value))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            if (eventId.Id != 0 || eventId.Name is not null)
            {
                message = eventId.Name is null
                    ? $"{message} [event {eventId.Id}]"
                    : $"{message} [event {eventId.Id} {eventId.Name}]";
            }

            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            log.Write(mapped.Value, source, message);
        }

        /// <summary>Maps a MEL level onto the unified level, or returns null for nothing to record.
        /// <para>将 MEL 级别映射到统一级别；无需记录时返回 null。</para></summary>
        /// <param name="logLevel">The MEL level. <para>MEL 级别。</para></param>
        /// <returns>The unified level, or <see langword="null"/> for <see cref="LogLevel.None"/>.
        /// <para>统一级别；<see cref="LogLevel.None"/> 时为 <see langword="null"/>。</para></returns>
        private static LogLevel? Map(MelLogLevel logLevel) => logLevel switch
        {
            MelLogLevel.Trace => Services.LogLevel.Trace,
            MelLogLevel.Debug => Services.LogLevel.Debug,
            MelLogLevel.Information => Services.LogLevel.Info,
            MelLogLevel.Warning => Services.LogLevel.Warn,
            MelLogLevel.Error => Services.LogLevel.Error,
            MelLogLevel.Critical => Services.LogLevel.Fatal,
            _ => null,
        };

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}