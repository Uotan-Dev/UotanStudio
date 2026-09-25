namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Logging;

/// <summary>
/// Writes OPS log lines to the console standard output, filtered by a minimum level. Each accepted
/// line has the form <c>[yyyy-MM-dd HH:mm:ss.fff] [LEVEL       ] message</c>, where the local
/// machine time is used and the level name is right-padded to eleven characters.
/// <para>按最低级别过滤后，把 OPS 日志行写入控制台标准输出。每条被接受的行形如
/// <c>[yyyy-MM-dd HH:mm:ss.fff] [LEVEL       ] 消息</c>，时间取本机本地时间，级别名右侧补齐到
/// 11 个字符。</para>
/// </summary>
public class OnePlusLogger : IOnePlusLogger
{
    /// <summary>Lowest severity that reaches the console.
    /// <para>会输出到控制台的最低严重级别。</para></summary>
    /// <value>Levels ordered below this value are dropped without being formatted; the default is
    /// <see cref="LogLevel.Information"/>. Callers may raise or lower it at any time, and a
    /// value of <see cref="LogLevel.Trace"/> emits every message.
    /// <para>低于该级别的记录会被直接丢弃而不做格式化；默认值为
    /// <see cref="LogLevel.Information"/>。调用方可随时调高或调低该值，取
    /// <see cref="LogLevel.Trace"/> 时会输出全部消息。</para></value>
    public LogLevel MinLogLevel { get; set; } = LogLevel.Information;

    /// <summary>Shared instance used when a caller has no logger of its own.
    /// <para>调用方未自带日志器时使用的共享实例。</para></summary>
    /// <value>A lazily created <see cref="OnePlusLogger"/> typed as <see cref="IOnePlusLogger"/>,
    /// created on first read and reused afterwards. Assigning a different sink replaces it for later
    /// reads, and assigning <see langword="null"/> makes the next read create a fresh instance. The
    /// lazy creation is not locked, so concurrent first reads may each build an instance and one of
    /// them wins.
    /// <para>以 <see cref="IOnePlusLogger"/> 暴露的延迟创建 <see cref="OnePlusLogger"/>，首次读取时
    /// 创建并复用。赋入其它接收端后，后续读取返回新值；赋 <see langword="null"/> 会让下一次读取重新
    /// 创建实例。延迟创建未加锁，并发首次读取可能各自构建一个实例，最终保留其中之一。</para></value>
    public static IOnePlusLogger Instance { get => field ??= new OnePlusLogger(); set; }

    /// <summary>
    /// Log a trace message
    /// <para>记录追踪消息</para>
    /// </summary>
    /// <param name="message">Text written verbatim at <see cref="LogLevel.Trace"/> level,
    /// which the default <see cref="MinLogLevel"/> suppresses.
    /// <para>以 <see cref="LogLevel.Trace"/> 级别原样写入的文本；默认
    /// <see cref="MinLogLevel"/> 会将其抑制。</para></param>
    public void Trace(string message) => Log(LogLevel.Trace, message);

    /// <summary>
    /// Log a debug message
    /// <para>记录调试消息</para>
    /// </summary>
    /// <param name="message">Text written verbatim at <see cref="LogLevel.Debug"/> level,
    /// which the default <see cref="MinLogLevel"/> suppresses.
    /// <para>以 <see cref="LogLevel.Debug"/> 级别原样写入的文本；默认
    /// <see cref="MinLogLevel"/> 会将其抑制。</para></param>
    public void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>
    /// Log an information message
    /// <para>记录信息消息</para>
    /// </summary>
    /// <param name="message">Text written verbatim at <see cref="LogLevel.Information"/> level, the
    /// lowest level the default <see cref="MinLogLevel"/> accepts.
    /// <para>以 <see cref="LogLevel.Information"/> 级别原样写入的文本，是默认
    /// <see cref="MinLogLevel"/> 接受的最低级别。</para></param>
    public void Information(string message) => Log(LogLevel.Information, message);

    /// <summary>
    /// Log a warning message
    /// <para>记录警告消息</para>
    /// </summary>
    /// <param name="message">Text written verbatim at <see cref="LogLevel.Warning"/> level.
    /// <para>以 <see cref="LogLevel.Warning"/> 级别原样写入的文本。</para></param>
    public void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>
    /// Log an error message
    /// <para>记录错误消息</para>
    /// </summary>
    /// <param name="message">Text written verbatim at <see cref="LogLevel.Error"/> level.
    /// <para>以 <see cref="LogLevel.Error"/> 级别原样写入的文本。</para></param>
    public void Error(string message) => Log(LogLevel.Error, message);

    /// <summary>
    /// Log a critical error message
    /// <para>记录严重错误消息</para>
    /// </summary>
    /// <param name="message">Text written verbatim at <see cref="LogLevel.Critical"/> level, the
    /// highest severity offered.
    /// <para>以 <see cref="LogLevel.Critical"/> 级别原样写入的文本，是提供的最高严重级别。</para></param>
    public void Critical(string message) => Log(LogLevel.Critical, message);

    /// <summary>
    /// Log a message with specified log level
    /// <para>记录指定日志级别的消息</para>
    /// </summary>
    /// <param name="level">Severity compared against <see cref="MinLogLevel"/>; a level below that
    /// threshold returns without writing anything.
    /// <para>与 <see cref="MinLogLevel"/> 比较的严重级别；低于该阈值时直接返回且不写入。</para></param>
    /// <param name="message">Message text appended after the timestamp and level columns without
    /// additional escaping; <see langword="null"/> appears as an empty column.
    /// <para>追加在时间戳与级别列之后的消息文本，不做额外转义；为 <see langword="null"/> 时该列为
    /// 空。</para></param>
    public void Log(LogLevel level, string message)
    {
        if (level < MinLogLevel)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = level.ToString().PadRight(11);

        Console.WriteLine($"[{timestamp}] [{levelStr}] {message}");
    }
}