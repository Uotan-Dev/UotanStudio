using System.Collections.Concurrent;

namespace UotanStudio.Services;

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
/// <para>日志通道分类，用于在日志面板中按来源（Source）对日志条目归类展示。</para>
/// Log channel categories, used to group log entries by their source in the log panel.
/// </summary>
/// <remarks>
/// <para>通道信息不写入 <see cref="LogEntry"/>，而是由 <see cref="LogService.ChannelOf"/> 在展示时按来源关键字映射。</para>
/// Channel information is not stored in <see cref="LogEntry"/>; it is mapped at display time by
/// <see cref="LogService.ChannelOf"/> based on source keywords.
/// </remarks>
public enum LogChannel
{
    /// <summary><para>通用通道：未匹配到其他通道关键字的日志。</para>General channel: logs that match no other channel keywords.</summary>
    General,

    /// <summary><para>设备通道：设备发现、连接、USB/ADB 等设备相关日志。</para>Device channel: device discovery, connection and USB/ADB logs.</summary>
    Device,

    /// <summary><para>烧录通道：刷机、烧录、分区写入等日志。</para>Flash channel: flashing and partition writing logs.</summary>
    Flash,

    /// <summary><para>固件通道：固件解包/打包、签名与镜像处理日志。</para>Firmware channel: firmware unpack/pack, signing and image processing logs.</summary>
    Firmware,

    /// <summary><para>构建通道：工作流、节点执行、打包与命令行工具日志。</para>Build channel: workflow, node execution, packing and CLI tool logs.</summary>
    Build
}

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
    private readonly List<EventHandler<LogEntry>> _subscribers = [];
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

    /// <summary>
    /// <para>根据日志来源（Source）归类日志通道，供 <see cref="UotanStudio.ViewModels.Shell.LogViewModel"/> 按通道过滤展示。</para>
    /// Classifies a log source into a <see cref="LogChannel"/>, used by the log panel to filter by channel.
    /// </summary>
    /// <remarks>
    /// <para>按关键字归类（不区分大小写，顺序优先）：包含 Flash/Fastboot/EDL/Firehose/Sideload → <see cref="LogChannel.Flash"/>；
    /// ADB/Device/USB/Usb/RKDL → <see cref="LogChannel.Device"/>；
    /// Firmware/Payload/AVB/Ext4/Erofs/F2fs/DEX/Format → <see cref="LogChannel.Firmware"/>；
    /// Build/Pack/Node/Workflow/Cli → <see cref="LogChannel.Build"/>；其余归 <see cref="LogChannel.General"/>。
    /// 此方法不改动任何写入调用点，仅作展示层分类。</para>
    /// Classified by keyword (case-insensitive, first match wins): Flash/Fastboot/EDL/Firehose/Sideload → <see cref="LogChannel.Flash"/>;
    /// ADB/Device/USB/Usb/RKDL → <see cref="LogChannel.Device"/>;
    /// Firmware/Payload/AVB/Ext4/Erofs/F2fs/DEX/Format → <see cref="LogChannel.Firmware"/>;
    /// Build/Pack/Node/Workflow/Cli → <see cref="LogChannel.Build"/>; otherwise <see cref="LogChannel.General"/>.
    /// This method does not modify any write call sites; it only classifies at the presentation layer.
    /// </remarks>
    /// <param name="source"><para>日志来源字符串。</para>Log source string.</param>
    /// <returns><para>对应的日志通道。</para>The corresponding log channel.</returns>
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

        if (ContainsAny(source, "Firmware", "Payload", "AVB", "Ext4", "Erofs", "F2fs", "DEX", "Format"))
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
    /// <para>判断来源是否包含任一关键字（不区分大小写）。</para>
    /// Determines whether the source contains any of the given keywords (case-insensitive).
    /// </summary>
    private static bool ContainsAny(string source, params string[] keywords)
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

    /// <inheritdoc />
    void IUnifiedLog.Write(LogLevel level, string source, string message) => Write(level, source, message);
}