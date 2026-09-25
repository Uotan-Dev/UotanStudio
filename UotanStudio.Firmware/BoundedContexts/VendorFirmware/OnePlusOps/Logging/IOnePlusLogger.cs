namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Logging;

/// <summary>
/// Severity levels used by the OnePlus OPS tooling, ordered from most verbose to most severe.
/// <para>OnePlus OPS 工具使用的日志严重级别，按从最详细到最严重排序。</para>
/// </summary>
public enum LogLevel
{
    /// <summary>Finest-grained diagnostic output. <para>最细粒度的诊断输出。</para></summary>
    Trace,

    /// <summary>Developer debugging detail. <para>开发者调试细节。</para></summary>
    Debug,

    /// <summary>Regular operational progress information. <para>常规运行进度信息。</para></summary>
    Information,

    /// <summary>A recoverable anomaly that does not stop the operation. <para>不中断操作的可恢复异常。</para></summary>
    Warning,

    /// <summary>A failure that aborts the current operation. <para>导致当前操作失败的错误。</para></summary>
    Error,

    /// <summary>A failure that makes the whole session unusable. <para>导致整个会话不可用的严重失败。</para></summary>
    Critical
}

/// <summary>
/// Logging sink for OnePlus OPS extraction and decryption steps. Implementations route the messages
/// to whichever output the host chooses.
/// <para>OnePlus OPS 提取与解密步骤的日志接收端。实现可将消息路由到宿主选择的任意输出。</para>
/// </summary>
public interface IOnePlusLogger
{
    /// <summary>
    /// Log a trace message
    /// <para>记录追踪消息</para>
    /// </summary>
    /// <param name="message">Message text emitted at <see cref="LogLevel.Trace"/> level.
    /// <para>以 <see cref="LogLevel.Trace"/> 级别输出的消息文本。</para></param>
    void Trace(string message);

    /// <summary>
    /// Log a debug message
    /// <para>记录调试消息</para>
    /// </summary>
    /// <param name="message">Message text emitted at <see cref="LogLevel.Debug"/> level.
    /// <para>以 <see cref="LogLevel.Debug"/> 级别输出的消息文本。</para></param>
    void Debug(string message);

    /// <summary>
    /// Log an information message
    /// <para>记录信息消息</para>
    /// </summary>
    /// <param name="message">Message text emitted at <see cref="LogLevel.Information"/> level.
    /// <para>以 <see cref="LogLevel.Information"/> 级别输出的消息文本。</para></param>
    void Information(string message);

    /// <summary>
    /// Log a warning message
    /// <para>记录警告消息</para>
    /// </summary>
    /// <param name="message">Message text emitted at <see cref="LogLevel.Warning"/> level.
    /// <para>以 <see cref="LogLevel.Warning"/> 级别输出的消息文本。</para></param>
    void Warning(string message);

    /// <summary>
    /// Log an error message
    /// <para>记录错误消息</para>
    /// </summary>
    /// <param name="message">Message text emitted at <see cref="LogLevel.Error"/> level.
    /// <para>以 <see cref="LogLevel.Error"/> 级别输出的消息文本。</para></param>
    void Error(string message);

    /// <summary>
    /// Log a critical error message
    /// <para>记录严重错误消息</para>
    /// </summary>
    /// <param name="message">Message text emitted at <see cref="LogLevel.Critical"/> level.
    /// <para>以 <see cref="LogLevel.Critical"/> 级别输出的消息文本。</para></param>
    void Critical(string message);

    /// <summary>
    /// Log a message with specified log level
    /// <para>记录指定日志级别的消息</para>
    /// </summary>
    /// <param name="level">Severity to attach to <paramref name="message"/>.
    /// <para>附加到 <paramref name="message"/> 的严重级别。</para></param>
    /// <param name="message">Message text to emit. <para>要输出的消息文本。</para></param>
    void Log(LogLevel level, string message);
}