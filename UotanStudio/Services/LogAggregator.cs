using System;
using System.Threading;

namespace UotanStudio.Services;

/// <summary>
/// <para>工具箱日志分类枚举，对应不同后端来源的日志通道。</para>
/// Toolbox log category enum, corresponding to different backend source log channels.
/// </summary>
public enum ToolboxLogCategory
{
    /// <summary>
    /// <para>工具箱自身操作日志（节点添加、连接、删除、运行状态等）。</para>
    /// Toolbox operation logs (node add, connect, delete, run state, etc.).
    /// </summary>
    Toolbox,
    /// <summary>
    /// <para>ADB 命令输出与设备日志。</para>
    /// ADB command output and device logs.
    /// </summary>
    ADB,
    /// <summary>
    /// <para>Fastboot 命令输出与烧录日志。</para>
    /// Fastboot command output and flashing logs.
    /// </summary>
    Fastboot,
    /// <summary>
    /// <para>固件处理日志（CPIO/Payload/AVB/Oppo 等 FirmwareKit 后端输出）。</para>
    /// Firmware processing logs (CPIO/Payload/AVB/Oppo FirmwareKit backend output).
    /// </summary>
    Firmware,
    /// <summary>
    /// <para>节点流程执行日志（各 Node ExecuteAsync 的步骤与结果摘要）。</para>
    /// Node flow execution logs (each Node ExecuteAsync step and result summary).
    /// </summary>
    Node
}

/// <summary>
/// <para>工具箱日志条目事件参数。</para>
/// Toolbox log entry event arguments.
/// </summary>
public sealed class ToolboxLogEventArgs : EventArgs
{
    /// <summary>日志分类。</summary>
    public ToolboxLogCategory Category { get; init; }
    /// <summary>日志级别（INFO/WARN/ERROR 等）。</summary>
    public string Level { get; init; } = "INFO";
    /// <summary>日志来源标签。</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>日志消息文本。</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>日志产生时间（本地时间）。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

/// <summary>
/// <para>静态日志事件总线，集中收集工具箱各后端来源的日志条目，供订阅者分发。</para>
/// Static log event bus, centrally collecting log entries from various toolbox backend sources for subscribers to dispatch.
/// <para>线程安全：所有 Append 重载可在任意线程调用；事件回调在调用线程同步触发。</para>
/// Thread-safe: all Append overloads can be called on any thread; event callbacks fire synchronously on the calling thread.
/// </summary>
public static class LogAggregator
{
    private static int _suspendCounter;

    /// <summary>
    /// <para>当有新日志条目推送时触发的事件。订阅者负责线程调度（UI 订阅需自行 Post）。</para>
    /// Event raised when a new log entry is appended. Subscribers are responsible for thread marshalling (UI subscribers must Post themselves).
    /// </summary>
    public static event EventHandler<ToolboxLogEventArgs>? LogAppended;

    /// <summary>
    /// <para>推送一条日志到事件总线。</para>
    /// Appends a log entry to the event bus.
    /// </summary>
    /// <param name="category">日志分类。</param>
    /// <param name="level">日志级别（INFO/WARN/ERROR 等）。</param>
    /// <param name="source">日志来源标签。</param>
    /// <param name="message">日志消息文本。</param>
    public static void Append(ToolboxLogCategory category, string level, string source, string message)
    {
        if (Volatile.Read(ref _suspendCounter) > 0) return;
        var args = new ToolboxLogEventArgs
        {
            Category = category,
            Level = string.IsNullOrEmpty(level) ? "INFO" : level,
            Source = source ?? string.Empty,
            Message = message ?? string.Empty
        };
        LogAppended?.Invoke(null, args);
    }

    /// <summary>
    /// <para>推送一条 INFO 级日志。</para>
    /// Appends an INFO-level log entry.
    /// </summary>
    /// <param name="category">日志分类。</param>
    /// <param name="source">日志来源标签。</param>
    /// <param name="message">日志消息文本。</param>
    public static void Info(ToolboxLogCategory category, string source, string message)
        => Append(category, "INFO", source, message);

    /// <summary>
    /// <para>推送一条 WARN 级日志。</para>
    /// Appends a WARN-level log entry.
    /// </summary>
    /// <param name="category">日志分类。</param>
    /// <param name="source">日志来源标签。</param>
    /// <param name="message">日志消息文本。</param>
    public static void Warn(ToolboxLogCategory category, string source, string message)
        => Append(category, "WARN", source, message);

    /// <summary>
    /// <para>推送一条 ERROR 级日志。</para>
    /// Appends an ERROR-level log entry.
    /// </summary>
    /// <param name="category">日志分类。</param>
    /// <param name="source">日志来源标签。</param>
    /// <param name="message">日志消息文本。</param>
    public static void Error(ToolboxLogCategory category, string source, string message)
        => Append(category, "ERROR", source, message);

    /// <summary>
    /// <para>暂停推送日志（计数+1，恢复时计数-1，计数大于 0 时丢弃所有 Append）。</para>
    /// Suspends log dispatch (increments counter; Append is dropped while counter > 0; Resume decrements).
    /// </summary>
    public static void Suspend() => Interlocked.Increment(ref _suspendCounter);

    /// <summary>
    /// <para>恢复推送日志（计数-1，不低于 0）。</para>
    /// Resumes log dispatch (decrements counter, floored at 0).
    /// </summary>
    public static void Resume() => Interlocked.Decrement(ref _suspendCounter);
}
