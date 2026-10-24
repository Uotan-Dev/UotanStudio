using System.Collections.Concurrent;
using UotanStudio.Composition;

namespace UotanStudio.Services;

/// <summary>
/// Log entry record, containing timestamp, level, source and text.
/// <para>日志条目记录，包含时间戳、级别、来源和文本。</para>
/// </summary>
/// <param name="Timestamp">The local time the entry was written. <para>写入条目的本地时间。</para></param>
/// <param name="Level">The severity level of the entry. <para>条目的严重级别。</para></param>
/// <param name="Source">The name of the log source. <para>日志来源名称。</para></param>
/// <param name="Message">The log text. <para>日志文本。</para></param>
public sealed record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Source,
    string Message);

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
/// Unified log service that buffers entries, applies a minimum level, and notifies subscribers.
/// <para>统一日志服务：缓冲日志条目，执行最低级别过滤，并向订阅者推送。</para>
/// </summary>
public sealed class LogService : IUnifiedLog
{
    private static readonly Lazy<LogService> LazyInstance = new(() => new LogService());
    private static int _containerLookup;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly Lock _subscribersLock = new();
    private readonly List<EventHandler<LogEntry>> _subscribers = [];
    private EventHandler<LogEntry>[] _subscriberSnapshot = [];
    private volatile LogLevel _minimumLevel = LogLevel.Trace;
    private int _maxQueueSize = 5000;

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

    private LogService() { }

    /// <summary>
    /// Raised when a log entry is enqueued.
    /// <para>日志条目入队时触发。</para>
    /// </summary>
    public event EventHandler<LogEntry>? LogEnqueued;

    /// <summary>
    /// All log entries currently buffered (read-only snapshot).
    /// <para>当前缓冲区中所有日志条目（只读快照）。</para>
    /// </summary>
    /// <value>A snapshot copy of the buffered entries. <para>缓冲条目的快照副本。</para></value>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            List<LogEntry> snapshot = [];
            foreach (var e in _entries) snapshot.Add(e);
            return snapshot;
        }
    }

    /// <summary>
    /// Maximum number of entries in the buffer; the oldest entries are dropped when exceeded.
    /// <para>缓冲区最大条目数，超出后丢弃最旧条目。</para>
    /// </summary>
    /// <value>The entry ceiling; assigned values below 100 are raised to 100.
    /// <para>条目数上限；赋值小于 100 时提升为 100。</para></value>
    public int MaxQueueSize
    {
        get => _maxQueueSize;
        set => _maxQueueSize = Math.Max(100, value);
    }

    /// <summary>
    /// Writes a log entry when its level meets the minimum, buffers it, trims the queue, and notifies subscribers.
    /// <para>级别不低于最低值时写入日志条目：入队缓冲、超限裁剪并通知订阅者。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">The log text. <para>日志文本。</para></param>
    public void Write(LogLevel level, string source, string message)
    {
        if (level < _minimumLevel) return;

        var entry = new LogEntry(DateTime.Now, level, source ?? string.Empty, message ?? string.Empty);
        _entries.Enqueue(entry);

        while (_entries.Count > _maxQueueSize && _entries.TryDequeue(out _)) { }

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
    /// Writes a log entry whose text is formatted from the arguments when the level meets the minimum.
    /// <para>级别不低于最低值时写入按参数格式化日志文本的条目。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="format">The composite format string. <para>复合格式字符串。</para></param>
    /// <param name="args">The format arguments. <para>格式化使用的参数数组。</para></param>
    public void Write(LogLevel level, string source, string format, params object[] args)
    {
        if (level < _minimumLevel) return;
        string message;
        try
        {
            message = args == null || args.Length == 0 ? format : string.Format(format, args);
        }
        catch
        {
            message = format;
        }
        Write(level, source, message);
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
    /// Applies the debug mode setting: when enabled the minimum level is <see cref="LogLevel.Trace"/>;
    /// when disabled it is <see cref="LogLevel.Info"/>.
    /// <para>应用调试模式设置：开启时最低级别为 <see cref="LogLevel.Trace"/>，关闭时为 <see cref="LogLevel.Info"/>。</para>
    /// </summary>
    /// <param name="debug">Whether debug mode is enabled. <para>是否启用调试模式。</para></param>
    public void ApplyDebugMode(bool debug)
    {
        SetMinimumLevel(debug ? LogLevel.Trace : LogLevel.Info);
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
        while (_entries.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Classifies a log source into a <see cref="LogChannel"/> by case-insensitive keyword match, first hit wins.
    /// <para>按不区分大小写的关键字将日志来源归入某个 <see cref="LogChannel"/>，首个命中生效。</para>
    /// </summary>
    /// <param name="source">Log source string. <para>日志来源字符串。</para></param>
    /// <returns>The corresponding log channel. <para>对应的日志通道。</para></returns>
    public static LogChannel ChannelOf(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return LogChannel.General;
        }

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