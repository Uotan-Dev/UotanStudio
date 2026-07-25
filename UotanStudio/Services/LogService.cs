using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services
{
    /// <summary>
    /// <para>日志条目记录，包含时间戳、级别、来源和文本。</para>
    /// Log entry record, containing timestamp, level, source and text.
    /// </summary>
    public sealed record LogEntry(
        DateTime Timestamp,
        LogLevel Level,
        string Source,
        string Message);

    /// <summary>
    /// <para>统一日志服务实现。所有方法（节点执行、设备协议、UI 命令等）均通过此推送文本，由主日志窗口订阅显示。</para>
    /// Unified log service implementation. All methods (node execution, device protocols, UI commands, etc.) push text through this; the main log window subscribes to display.
    /// </summary>
    /// <remarks>
    /// <para>AOT 友好：无反射，使用 <see cref="ConcurrentQueue{T}"/> 与事件订阅。</para>
    /// AOT-friendly: no reflection, uses <see cref="ConcurrentQueue{T}"/> and event subscription.
    /// </remarks>
    public sealed class LogService : IUnifiedLog
    {
        private static readonly Lazy<LogService> LazyInstance = new(() => new LogService());
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly object _subscribersLock = new();
        private readonly List<EventHandler<LogEntry>> _subscribers = new();
        private LogLevel _minimumLevel = LogLevel.Trace;
        private int _maxQueueSize = 5000;

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
                foreach (var e in _entries) snapshot.Add(e);
                return snapshot;
            }
        }

        /// <summary>
        /// <para>缓冲区最大条目数，超出后丢弃最旧条目。</para>
        /// Maximum number of entries in the buffer; the oldest entries are dropped when exceeded.
        /// </summary>
        public int MaxQueueSize
        {
            get => _maxQueueSize;
            set => _maxQueueSize = Math.Max(100, value);
        }

        /// <inheritdoc />
        public void Write(LogLevel level, string source, string message)
        {
            if (level < _minimumLevel) return;

            var entry = new LogEntry(DateTime.Now, level, source ?? string.Empty, message ?? string.Empty);
            _entries.Enqueue(entry);

            // 丢弃最旧条目以限制内存占用
            while (_entries.Count > _maxQueueSize && _entries.TryDequeue(out _)) { }

            // 触发订阅者回调（线程安全）
            List<EventHandler<LogEntry>>? handlers = null;
            lock (_subscribersLock)
            {
                if (_subscribers.Count > 0)
                {
                    handlers = new List<EventHandler<LogEntry>>(_subscribers);
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

        /// <inheritdoc />
        public void SetMinimumLevel(LogLevel level)
        {
            _minimumLevel = level;
        }

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

        /// <inheritdoc />
        void IUnifiedLog.Write(LogLevel level, string source, string message) => Write(level, source, message);
    }
}
