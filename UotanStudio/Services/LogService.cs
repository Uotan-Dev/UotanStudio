using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace UotanStudio.Services;

/// <summary>
/// <para>日志级别枚举。</para>
/// Log level enum.
/// </summary>
public enum LogLevel
{
    /// <summary><para>跟踪。</para>Trace.</summary>
    Trace,

    /// <summary><para>调试。</para>Debug.</summary>
    Debug,

    /// <summary><para>信息。</para>Info.</summary>
    Info,

    /// <summary><para>警告。</para>Warn.</summary>
    Warn,

    /// <summary><para>错误。</para>Error.</summary>
    Error,

    /// <summary><para>致命错误。</para>Fatal.</summary>
    Fatal
}

/// <summary>
/// <para>统一日志写入接口。所有服务、节点执行、设备协议交互均通过此接口推送文本，
/// 由主日志窗口收集显示。</para>
/// Unified log writer interface. All services, node executions and device-protocol
/// interactions push text through this interface, collected and displayed by the main
/// log window.
/// </summary>
public interface IUnifiedLog
{
    /// <summary>
    /// <para>写入一条日志条目。</para>
    /// Writes a log entry.
    /// </summary>
    /// <param name="level"><para>日志级别。</para>Log level.</param>
    /// <param name="source"><para>日志来源（模块名、节点名、设备 ID 等）。</para>Log source (module name, node name, device id, etc.).</param>
    /// <param name="message"><para>日志文本。</para>Log text.</param>
    void Write(LogLevel level, string source, string message);

    /// <summary>
    /// <para>写入一条格式化日志条目。</para>
    /// Writes a formatted log entry.
    /// </summary>
    void Write(LogLevel level, string source, string format, params object[] args);

    /// <summary>
    /// <para>设置最小输出级别，低于此级别的日志将被丢弃。</para>
    /// Sets the minimum output level; logs below this level are discarded.
    /// </summary>
    void SetMinimumLevel(LogLevel level);
}

/// <summary>
/// <para>日志条目记录，包含时间戳、级别、来源和文本。</para>
/// Log entry record, containing timestamp, level, source and text.
/// </summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message);

/// <summary>
/// <para>统一日志服务实现。所有模块（节点执行、设备协议、UI 命令等）均通过此推送文本，
/// 由主日志窗口订阅显示。</para>
/// Unified log service implementation. All modules (node execution, device protocols, UI
/// commands, etc.) push text through this; the main log window subscribes to display.
/// </summary>
/// <remarks>
/// <para>AOT 友好：无反射，使用 <see cref="ConcurrentQueue{T}"/> 与事件订阅。</para>
/// AOT-friendly: no reflection, uses <see cref="ConcurrentQueue{T}"/> and event subscription.
/// </remarks>
public sealed class LogService : IUnifiedLog
{
    private static readonly Lazy<LogService> LazyInstance = new(() => new LogService());
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly Lock _subscribersLock = new();
    private readonly List<EventHandler<LogEntry>> _subscribers = [];
    private LogLevel _minimumLevel = LogLevel.Trace;

    /// <summary>
    /// <para>单例实例。</para>
    /// Singleton instance.
    /// </summary>
    public static LogService Instance => LazyInstance.Value;

    private LogService() { }

    /// <summary>
    /// <para>日志条目入队事件。UI 日志窗口订阅此事件以增量接收日志。</para>
    /// Log entry enqueued event. The UI log window subscribes to incrementally receive logs.
    /// </summary>
    public event EventHandler<LogEntry>? LogEnqueued;

    /// <summary>
    /// <para>当前缓冲区中所有日志条目（只读快照）。</para>
    /// All log entries currently buffered (read-only snapshot).
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            var snapshot = new List<LogEntry>();
            foreach (var e in _entries)
            {
                snapshot.Add(e);
            }

            return snapshot;
        }
    }

    /// <summary>
    /// <para>缓冲区最大条目数，超出后丢弃最旧条目。</para>
    /// Maximum number of entries in the buffer; the oldest entries are dropped when exceeded.
    /// </summary>
    public int MaxQueueSize
    {
        get;
        set => field = Math.Max(100, value);
    } = 5000;

    /// <inheritdoc />
    public void Write(LogLevel level, string source, string message)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTime.Now, level, source ?? string.Empty, message ?? string.Empty);
        _entries.Enqueue(entry);

        // 丢弃最旧条目以限制内存占用
        while (_entries.Count > MaxQueueSize && _entries.TryDequeue(out _)) { }

        List<EventHandler<LogEntry>>? handlers = null;
        lock (_subscribersLock)
        {
            if (_subscribers.Count > 0)
            {
                handlers = [.. _subscribers];
            }
        }
        if (handlers != null)
        {
            foreach (var h in handlers)
            {
                try { h(this, entry); } catch { /* 忽略订阅者异常 */ }
            }
        }

        LogEnqueued?.Invoke(this, entry);
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string source, string format, params object[] args)
    {
        if (level < _minimumLevel)
        {
            return;
        }

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

    /// <inheritdoc />
    public void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    /// <summary>
    /// <para>订阅日志条目推送（线程安全）。返回取消订阅的委托。</para>
    /// Subscribes to log entry push (thread-safe). Returns a delegate to unsubscribe.
    /// </summary>
    public Action Subscribe(EventHandler<LogEntry> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subscribersLock)
        {
            _subscribers.Add(handler);
        }
        return () =>
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(handler);
            }
        };
    }

    /// <summary>
    /// <para>清空缓冲区。</para>
    /// Clears the buffer.
    /// </summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }
}