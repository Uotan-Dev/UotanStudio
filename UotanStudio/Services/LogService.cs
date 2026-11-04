using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Composition;
using UotanStudio.Services.Logging;

namespace UotanStudio.Services;

/// <summary>
/// One structured field attached to a log entry, carrying a name and its rendered value.
/// <para>挂在日志条目上的一个结构化字段，携带名称与渲染后的取值。</para>
/// </summary>
/// <param name="Key">Field name, such as a message-template placeholder. <para>字段名，例如消息模板占位符。</para></param>
/// <param name="Value">Rendered field value; already formatted and never null.
/// <para>渲染后的字段值；已格式化且从不为 null。</para></param>
public readonly record struct LogField(string Key, string Value);

/// <summary>
/// Log entry record, containing timestamp, level, source, text and optional correlation metadata.
/// <para>日志条目记录，包含时间戳、级别、来源、文本以及可选的关联元数据。</para>
/// </summary>
/// <param name="Timestamp">The local time the entry was written. <para>写入条目的本地时间。</para></param>
/// <param name="Level">The severity level of the entry. <para>条目的严重级别。</para></param>
/// <param name="Source">The name of the log source. <para>日志来源名称。</para></param>
/// <param name="Message">The log text. <para>日志文本。</para></param>
/// <param name="Scope">Correlation scope shared by a group of entries, or null when the entry belongs to none.
/// <para>一组条目共享的关联作用域；条目不属于任何组时为 null。</para></param>
/// <param name="Fields">Structured fields attached to the entry, or null when it carries none.
/// <para>挂在条目上的结构化字段；不携带时为 null。</para></param>
public sealed record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Source,
    string Message,
    string? Scope = null,
    IReadOnlyList<LogField>? Fields = null);

/// <summary>
/// Log channel categories that group entries by their source.
/// <para>按来源对日志条目归类的日志通道分类。</para>
/// </summary>
public enum LogChannel
{
    /// <summary>General channel: logs that match no other channel keywords. <para>通用通道：未匹配到其他通道关键字的日志。</para></summary>
    General,

    /// <summary>Device channel: device discovery, connection and USB/ADB logs. <para>设备通道：设备发现、连接、USB/ADB 等设备相关日志。</para></summary>
    Device,

    /// <summary>Flash channel: flashing and partition writing logs. <para>烧录通道：刷机、烧录、分区写入等日志。</para></summary>
    Flash,

    /// <summary>Firmware channel: firmware unpack/pack, signing and image processing logs. <para>固件通道：固件解包/打包、签名与镜像处理日志。</para></summary>
    Firmware,

    /// <summary>Node channel: workflow, node execution, packing and CLI tool logs. <para>节点通道：工作流、节点执行、打包与命令行工具日志。</para></summary>
    Build
}

/// <summary>
/// Unified log service that retains entries under an entry-count and text budget, applies a minimum level, and
/// notifies subscribers.
/// <para>统一日志服务：按条目数与文本预算保留日志，执行最低级别过滤，并向订阅者推送。</para>
/// </summary>
public sealed class LogService : IUnifiedLog
{
    /// <summary>
    /// Default entry-count guard; the character budget normally evicts first.
    /// <para>默认条目数护栏；通常先触发的是字符预算。</para>
    /// </summary>
    public const int DefaultMaxQueueSize = 250_000;

    /// <summary>
    /// Default text budget in characters; the oldest entries are evicted once the retained text exceeds it.
    /// <para>默认文本字符预算；保留文本超出后淘汰最旧条目。</para>
    /// </summary>
    public const long DefaultMaxQueueCharacters = 64L * 1024 * 1024;

    private static readonly Lazy<LogService> LazyInstance = new(() => new LogService());
    private static int _containerLookup;
    private static readonly ConcurrentDictionary<string, LogChannel> ChannelCache = new();

    /// <summary>
    /// Retained entries in arrival order; the head is the oldest entry and is evicted once the entry or
    /// character budget is exceeded.
    /// <para>按到达顺序保留的条目；队首是最旧条目，超出条目数或字符预算时被淘汰。</para>
    /// </summary>
    private readonly Queue<LogEntry> _entries = new();

    private readonly Lock _subscribersLock = new();
    private readonly Lock _bufferLock = new();
    private readonly Lock _fileLogLock = new();
    private readonly List<EventHandler<LogEntry>> _subscribers = [];
    private EventHandler<LogEntry>[] _subscriberSnapshot = [];
    private volatile LogLevel _minimumLevel = LogLevel.Trace;
    private volatile int _maxQueueSize = DefaultMaxQueueSize;
    private long _maxQueueCharacters = DefaultMaxQueueCharacters;
    private long _queuedCharacters;
    private volatile bool _debugMode;
    private string? _fileLogDirectory;
    private LogFileSink? _fileSink;

    /// <summary>
    /// Gets the singleton instance: the container instance when <see cref="ServiceHub"/> is
    /// initialized, otherwise the lazy default used by tests and by code that runs before the
    /// composition root exists.
    /// <para>获取单例实例：<see cref="ServiceHub"/> 已初始化时返回容器实例，否则返回测试与组合根建立前
    /// 代码使用的惰性默认实例。</para>
    /// </summary>
    /// <value>The shared log service instance. <para>共享的日志服务实例。</para></value>
    public static LogService Instance
    {
        get
        {
            if (Interlocked.CompareExchange(ref _containerLookup, 1, 0) == 0)
            {
                try
                {
                    if (ServiceHub.TryGet<LogService>() is { } registered)
                    {
                        return registered;
                    }
                }
                finally
                {
                    Volatile.Write(ref _containerLookup, 0);
                }
            }

            return LazyInstance.Value;
        }
    }

    internal LogService()
    {
    }

    /// <summary>
    /// Raised when a log entry is enqueued.
    /// <para>日志条目入队时触发。</para>
    /// </summary>
    public event EventHandler<LogEntry>? LogEnqueued;

    /// <summary>
    /// All log entries currently buffered (read-only snapshot, oldest first).
    /// <para>当前缓冲区中所有日志条目（只读快照，最旧在前）。</para>
    /// </summary>
    /// <value>A snapshot copy of the buffered entries. <para>缓冲条目的快照副本。</para></value>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_bufferLock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Entry-count guard; entries are dropped from the head once either this count or
    /// <see cref="MaxQueueCharacters"/> is exceeded.
    /// <para>条目数护栏；条目数或 <see cref="MaxQueueCharacters"/> 任一超出时从队首丢弃条目。</para>
    /// </summary>
    /// <value>The entry ceiling; assigned values below 100 are raised to 100.
    /// <para>条目数上限；赋值小于 100 时提升为 100。</para></value>
    public int MaxQueueSize
    {
        get => _maxQueueSize;
        set
        {
            var capacity = Math.Max(100, value);
            lock (_bufferLock)
            {
                if (capacity == _maxQueueSize)
                {
                    return;
                }

                _maxQueueSize = capacity;
                TrimLocked();
            }
        }
    }

    /// <summary>
    /// Text budget in characters; the oldest entries are evicted once the retained source and message text
    /// exceeds it, so long sessions keep the newest output instead of growing without bound.
    /// <para>文本字符预算；保留的来源与消息文本超出后淘汰最旧条目，使长会话保留最新输出而不无限增长。</para>
    /// </summary>
    /// <value>The character budget; assigned values below 65536 are raised to 65536.
    /// <para>字符预算；赋值小于 65536 时提升为 65536。</para></value>
    public long MaxQueueCharacters
    {
        get => Volatile.Read(ref _maxQueueCharacters);
        set
        {
            var budget = Math.Max(64 * 1024, value);
            lock (_bufferLock)
            {
                if (budget == Volatile.Read(ref _maxQueueCharacters))
                {
                    return;
                }

                Volatile.Write(ref _maxQueueCharacters, budget);
                TrimLocked();
            }
        }
    }

    /// <summary>
    /// Writes a log entry to the configured file trail; when its level meets the minimum it is also buffered under
    /// the retention budget and subscribers are notified.
    /// <para>把日志条目写入已配置的文件接收端；级别不低于最低值时再按保留预算入队缓冲并通知订阅者。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">The log text. <para>日志文本。</para></param>
    public void Write(LogLevel level, string source, string message)
        => Write(level, source, message, null, null);

    /// <summary>
    /// Writes a log entry carrying optional correlation metadata to the file trail, the retention buffer and the
    /// subscribers.
    /// <para>把携带可选关联元数据的日志条目写入文件接收端、保留缓冲与订阅者。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">The log text. <para>日志文本。</para></param>
    /// <param name="scope">Correlation scope shared by a group of entries; blank values are recorded as null.
    /// <para>一组条目共享的关联作用域；空白值按 null 记录。</para></param>
    /// <param name="fields">Structured fields attached to the entry; the list is copied and empty lists are
    /// recorded as null.
    /// <para>挂在条目上的结构化字段；列表会被复制，空列表按 null 记录。</para></param>
    public void Write(LogLevel level, string source, string message, string? scope, IReadOnlyList<LogField>? fields)
    {
        var entry = new LogEntry(DateTime.Now, level, source ?? string.Empty, message ?? string.Empty,
            string.IsNullOrWhiteSpace(scope) ? null : scope,
            fields is { Count: > 0 } ? [.. fields] : null);
        Volatile.Read(ref _fileSink)?.Append(entry);

        if (level < _minimumLevel)
        {
            return;
        }

        lock (_bufferLock)
        {
            _entries.Enqueue(entry);
            _queuedCharacters += CharacterCost(entry);
            TrimLocked();
        }

        var handlers = Volatile.Read(ref _subscriberSnapshot);
        if (handlers.Length != 0)
        {
            foreach (var h in handlers)
            {
                try { h(this, entry); } catch { }
            }
        }

        LogEnqueued?.Invoke(this, entry);
    }

    /// <summary>
    /// Writes an interpolated entry, building no text when the entry would not be recorded anywhere.
    /// <para>写入一条插值条目；条目不会在任何位置被记录时不构建文本。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">Interpolated message; skipped when <see cref="IsEnabled"/> rejects the level.
    /// <para>插值消息；<see cref="IsEnabled"/> 拒绝该级别时跳过。</para></param>
    public void Write(LogLevel level, string source,
        [InterpolatedStringHandlerArgument("", "level")] ref UnifiedLogMessage message)
    {
        if (!message.IsEnabled)
        {
            return;
        }

        Write(level, source, message.ToStringAndClear());
    }

    /// <summary>
    /// Writes a log entry whose text is formatted from the arguments; the formatted entry reaches the file trail
    /// even when its level does not meet the store minimum.
    /// <para>写入按参数格式化日志文本的条目；即使级别低于存储最低级别，格式化后的条目仍会进入文件接收端。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="format">The composite format string. <para>复合格式字符串。</para></param>
    /// <param name="args">The format arguments. <para>格式化使用的参数数组。</para></param>
    public void Write(LogLevel level, string source, string format, params object[] args)
        => Write(level, source, FormatMessage(format, args));

    /// <summary>
    /// Formats a composite message, falling back to the raw format text when the arguments do not match.
    /// <para>格式化复合消息；参数与模板不匹配时回退为原始格式文本。</para>
    /// </summary>
    /// <param name="format">The composite format string. <para>复合格式字符串。</para></param>
    /// <param name="args">The format arguments; null or empty leaves the text unchanged.
    /// <para>格式化使用的参数数组；null 或空数组时不改变文本。</para></param>
    /// <returns>The formatted text, or <paramref name="format"/> when formatting fails.
    /// <para>格式化后的文本；格式化失败时为 <paramref name="format"/>。</para></returns>
    internal static string FormatMessage(string format, object[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return format;
        }

        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return format;
        }
    }

    /// <summary>
    /// Sets the minimum output level; entries below it are discarded.
    /// <para>设置最低输出级别，低于该级别的条目将被丢弃。</para>
    /// </summary>
    /// <param name="level">The new minimum level. <para>新的最低级别。</para></param>
    public void SetMinimumLevel(LogLevel level)
    {
        _minimumLevel = level;
    }

    /// <summary>
    /// Gets whether an entry at <paramref name="level"/> reaches any destination, so a caller can skip work the
    /// interpolated handler cannot make lazy.
    /// <para>获取某个级别的条目是否会被任何一个目标接收，使调用方可以跳过插值处理器无法延迟的工作。</para>
    /// </summary>
    /// <param name="level">The level the entry would be written at. <para>条目将要写入的级别。</para></param>
    /// <returns><see langword="true"/> when the entry would be buffered or written to the session file.
    /// <para>条目会进入缓冲或写入会话文件时为 <see langword="true"/>。</para></returns>
    /// <remarks>An entry below the minimum level still reaches the session file while the file trail is active, so
    /// this reports <see langword="true"/> for it; formatting may be skipped only when both destinations drop it.
    /// <para>文件接收端启用期间，低于最低级别的条目仍会写入会话文件，因此此处返回
    /// <see langword="true"/>；只有两个目标都会丢弃时才能跳过格式化。</para></remarks>
    public bool IsEnabled(LogLevel level)
        => level >= _minimumLevel || Volatile.Read(ref _fileSink) is not null;

    /// <summary>
    /// Gets whether debug mode is currently enabled; consumers such as the global log window use it to skip
    /// their own retention budgets while verbose diagnostics are being collected.
    /// <para>获取当前是否启用调试模式；全局日志窗口等使用方在收集详细诊断期间据此跳过自身的保留预算。</para>
    /// </summary>
    /// <value><see langword="true"/> while debug mode is enabled. <para>调试模式开启时为 <see langword="true"/>。</para></value>
    public bool IsDebugModeEnabled => _debugMode;

    /// <summary>
    /// Applies the debug mode setting: when enabled the minimum level is <see cref="LogLevel.Trace"/>;
    /// when disabled it is <see cref="LogLevel.Info"/>. The file trail configured through
    /// <see cref="ConfigureFileLogging"/> is independent of this switch.
    /// <para>应用调试模式设置：开启时最低级别为 <see cref="LogLevel.Trace"/>，关闭时为 <see cref="LogLevel.Info"/>。
    /// 由 <see cref="ConfigureFileLogging"/> 配置文件接收端与开关无关。</para>
    /// </summary>
    /// <param name="debug">Whether debug mode is enabled. <para>是否启用调试模式。</para></param>
    public void ApplyDebugMode(bool debug)
    {
        SetMinimumLevel(debug ? LogLevel.Trace : LogLevel.Info);
        ApplyCommDebugMode(debug);
        _debugMode = debug;
    }

    /// <summary>
    /// Starts writing every produced entry to a session file in <paramref name="directory"/>; <see langword="null"/>
    /// stops the trail. The file receives entries below the store minimum as well, so it is not limited by debug mode
    /// or by the window's level filter.
    /// <para>开始在 <paramref name="directory"/> 中把每条产生的条目写入会话文件；<see langword="null"/> 停止写入。
    /// 文件同样接收低于存储最低级别的条目，因此不受调试模式或窗口级别筛选限制。</para>
    /// </summary>
    /// <param name="directory">Target directory, or null to disable. <para>目标目录；null 表示关闭。</para></param>
    public void ConfigureFileLogging(string? directory)
    {
        lock (_fileLogLock)
        {
            _fileLogDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
            CloseFileSinkLocked();
            OpenFileSinkLocked();
        }
    }

    /// <summary>
    /// Flushes buffered file-log records; a no-op when file logging is inactive.
    /// <para>冲刷文件日志缓冲；文件日志未启用时为空操作。</para>
    /// </summary>
    public void FlushFileLog()
    {
        lock (_fileLogLock)
        {
            _fileSink?.Flush();
        }
    }

    private void OpenFileSinkLocked()
    {
        if (_fileSink is not null || _fileLogDirectory is not { } directory)
        {
            return;
        }

        try
        {
            _fileSink = new LogFileSink(directory);
        }
        catch
        {
            _fileSink = null;
        }
    }

    private void CloseFileSinkLocked()
    {
        var sink = _fileSink;
        _fileSink = null;
        sink?.Dispose();
    }

    /// <summary>
    /// Points the Comm diagnostics at the debug setting: debug mode collects protocol state, per-step debug output and
    /// bounded raw payload previews in the global log, and the off state keeps the console-only defaults.
    /// <para>把 Comm 诊断接到调试设置上：调试模式在全局日志中收集协议状态、逐步调试输出与有界原始负载预览，关闭时
    /// 保留默认的仅控制台输出。</para>
    /// </summary>
    /// <param name="debug">Whether debug mode is enabled. <para>是否启用调试模式。</para></param>
    private static void ApplyCommDebugMode(bool debug)
    {
        CommTrace.Level = debug ? CommLogLevel.Trace : CommLogLevel.Off;
        CommTrace.CaptureRawPayloads = debug;
        UsbTrace.IsEnabled = debug;
        UsbTrace.CaptureFrames = debug;
    }

    /// <summary>
    /// Subscribes to log entry push (thread-safe). Returns a delegate to unsubscribe.
    /// <para>订阅日志条目推送（线程安全）。返回取消订阅的委托。</para>
    /// </summary>
    /// <param name="handler">The handler invoked for each new entry. <para>每条新日志触发的处理器。</para></param>
    /// <returns>A delegate that removes the subscription. <para>用于取消订阅的委托。</para></returns>
    public Action Subscribe(EventHandler<LogEntry> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subscribersLock)
        {
            _subscribers.Add(handler);
            Volatile.Write(ref _subscriberSnapshot, [.. _subscribers]);
        }
        return () =>
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(handler);
                Volatile.Write(ref _subscriberSnapshot, [.. _subscribers]);
            }
        };
    }

    /// <summary>
    /// Clears the buffer.
    /// <para>清空缓冲区。</para>
    /// </summary>
    public void Clear()
    {
        lock (_bufferLock)
        {
            _entries.Clear();
            _queuedCharacters = 0;
        }
    }

    private void TrimLocked()
    {
        while (_entries.Count > 0 && (_entries.Count > _maxQueueSize || _queuedCharacters > _maxQueueCharacters))
        {
            var removed = _entries.Dequeue();
            _queuedCharacters -= CharacterCost(removed);
        }
    }

    /// <summary>
    /// Counts the characters an entry charges against the retention budget: source, message and any metadata.
    /// <para>统计条目占用的保留预算字符数：来源、消息以及任何元数据。</para>
    /// </summary>
    /// <param name="entry">The entry being charged or released. <para>计入或释放的条目。</para></param>
    /// <returns>The character cost of the entry. <para>该条目的字符开销。</para></returns>
    private static long CharacterCost(LogEntry entry)
    {
        long cost = (long)entry.Source.Length + entry.Message.Length;
        if (entry.Scope is { Length: > 0 } scope)
        {
            cost += scope.Length;
        }

        if (entry.Fields is { Count: > 0 } fields)
        {
            foreach (var field in fields)
            {
                cost += (long)field.Key.Length + field.Value.Length;
            }
        }

        return cost;
    }

    /// <summary>
    /// Classifies a log source into a <see cref="LogChannel"/> by case-insensitive keyword match, first hit wins.
    /// Sources repeat from a small closed set, so every classified string is memoized and later lookups skip the
    /// keyword scans entirely.
    /// <para>按不区分大小写的关键字将日志来源归入某个 <see cref="LogChannel"/>，首个命中生效。来源来自一个很小的
    /// 封闭集合，因此每个已分类的字符串都会被记忆，后续查找完全跳过关键字扫描。</para>
    /// </summary>
    /// <param name="source">Log source string. <para>日志来源字符串。</para></param>
    /// <returns>The corresponding log channel. <para>对应的日志通道。</para></returns>
    public static LogChannel ChannelOf(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return LogChannel.General;
        }

        return ChannelCache.GetOrAdd(source, static value => Classify(value));
    }

    private static LogChannel Classify(string source)
    {
        if (ContainsAny(source, "Flash", "Fastboot", "EDL", "Firehose", "Sideload"))
        {
            return LogChannel.Flash;
        }

        if (ContainsAny(source, "ADB", "Device", "USB", "Usb", "RKDL"))
        {
            return LogChannel.Device;
        }

        if (string.Equals(source, "Images", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(source, "Firmware", "Payload", "AVB", "Ext4", "Erofs", "F2fs", "DEX", "Format"))
        {
            return LogChannel.Firmware;
        }

        if (ContainsAny(source, "Build", "Pack", "Node", "Workflow", "Cli"))
        {
            return LogChannel.Build;
        }

        return LogChannel.General;
    }

    /// <summary>
    /// Determines whether the source contains any of the given keywords (case-insensitive).
    /// <para>判断来源是否包含任一关键字（不区分大小写）。</para>
    /// </summary>
    /// <param name="source">The string to search. <para>待搜索的字符串。</para></param>
    /// <param name="keywords">The keywords to look for. <para>要查找的关键字。</para></param>
    /// <returns>True when any keyword occurs in the source. <para>来源中包含任一关键字时为 true。</para></returns>
    private static bool ContainsAny(string source, params ReadOnlySpan<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Interface implementation that writes a buffered, subscriber-notified log entry.
    /// <para>接口实现：写入经过缓冲并通知订阅者的日志条目。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">The log text. <para>日志文本。</para></param>
    void IUnifiedLog.Write(LogLevel level, string source, string message) => Write(level, source, message);
}