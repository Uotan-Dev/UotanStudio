using System.Runtime.CompilerServices;

namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Severity of one Comm diagnostics message; lower values are always emitted once enabled.
/// <para>Comm 诊断消息的严重级别；启用后较低级别总是输出。</para>
/// </summary>
public enum CommLogLevel
{
    /// <summary>Disables every level. <para>关闭所有级别。</para></summary>
    Off = 0,
    /// <summary>Failures that must reach the log. <para>必须进入日志的故障。</para></summary>
    Error = 1,
    /// <summary>Link state changes, transfer starts and completions.
    /// <para>链路状态变化、传输开始与完成。</para></summary>
    Info = 2,
    /// <summary>Command pool entries and protocol steps. <para>指令池进出与协议步骤。</para></summary>
    Debug = 3,
    /// <summary>Per-chunk detail, optionally including bounded raw payloads.
    /// <para>逐块细节，可选包含有界原始负载。</para></summary>
    Trace = 4,
}

/// <summary>
/// Receives Comm diagnostics with their level and category.
/// <para>接收带级别与分类的 Comm 诊断消息。</para>
/// </summary>
public interface ICommLogger
{
    /// <summary>Writes one diagnostic message. <para>写入一条诊断消息。</para></summary>
    /// <param name="level">Message severity. <para>消息严重级别。</para></param>
    /// <param name="category">Stable category such as <c>adb-sync</c>. <para>稳定分类，例如 <c>adb-sync</c>。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    void Log(CommLogLevel level, string category, string message);
}

/// <summary>
/// Comm-wide diagnostics for the device link: command pool traffic, protocol steps, file transfers, raw payload
/// previews and link state. Levels come from <c>UOTAN_COMM_LOG</c> (<c>off|error|info|debug|trace</c>); raw payload
/// capture is a second, explicit switch (<c>UOTAN_COMM_RAW=1</c>) so verbose logs never include file content by
/// accident.
/// <para>设备链路的 Comm 级诊断：指令池流量、协议步骤、文件传输、原始负载预览与链路状态。级别取自
/// <c>UOTAN_COMM_LOG</c>（<c>off|error|info|debug|trace</c>）；原始负载捕获是第二个显式开关
/// （<c>UOTAN_COMM_RAW=1</c>），因此详细日志不会意外带上文件内容。</para>
/// </summary>
public static class CommTrace
{
    private static CommLogLevel _level = ParseLevel(Environment.GetEnvironmentVariable("UOTAN_COMM_LOG"));

    /// <summary>Gets or sets the highest level that is emitted; <see cref="CommLogLevel.Off"/> silences everything.
    /// <para>获取或设置允许输出的最高级别；<see cref="CommLogLevel.Off"/> 关闭全部输出。</para>
    /// </summary>
    /// <value>The active level; initialized from <c>UOTAN_COMM_LOG</c>.
    /// <para>当前级别；初值取自 <c>UOTAN_COMM_LOG</c>。</para></value>
    public static CommLogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>Gets or sets whether payload previews are attached to trace messages. Errors and informational
    /// messages never include payload bytes, so this switch only widens trace output.
    /// <para>获取或设置是否在 trace 消息上附带负载预览。错误与信息级消息从不包含负载字节，因此该开关只扩大
    /// trace 输出。</para>
    /// </summary>
    /// <value><see langword="true"/> when trace messages include bounded payload previews; initialized from
    /// <c>UOTAN_COMM_RAW=1</c>.<para>trace 消息附带负载预览时为 <see langword="true"/>；初值取自
    /// <c>UOTAN_COMM_RAW=1</c>。</para></value>
    public static bool CaptureRawPayloads { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("UOTAN_COMM_RAW"), "1", StringComparison.Ordinal);

    /// <summary>Hard upper bound applied to every captured payload preview.
    /// <para>每条负载预览的硬性上限。</para>
    /// </summary>
    public const int MaxRawPayloadBytes = 256;

    /// <summary>Gets or sets the sink receiving diagnostics; the default writes to the error stream.
    /// <para>获取或设置接收诊断的汇；默认实现写入错误输出流。</para>
    /// </summary>
    /// <value>The installed <see cref="ICommLogger"/>; the host replaces it with a log-service bridge.
    /// <para>已安装的 <see cref="ICommLogger"/>；宿主会替换为日志服务桥。</para></value>
    public static ICommLogger Logger { get; set; } = new ConsoleCommLogger();

    /// <summary>Parses a level name from configuration. <para>从配置解析级别名称。</para></summary>
    /// <param name="text">Level text: <c>off</c>, <c>error</c>, <c>info</c>, <c>debug</c>, <c>trace</c>, or the
    /// shorthand <c>1</c>/<c>true</c> (debug) and <c>2</c>/<c>all</c> (trace).
    /// <para>级别文本：<c>off</c>、<c>error</c>、<c>info</c>、<c>debug</c>、<c>trace</c>，以及简写
    /// <c>1</c>/<c>true</c>（debug）与 <c>2</c>/<c>all</c>（trace）。</para></param>
    /// <returns>The parsed level; unset or unrecognized text yields <see cref="CommLogLevel.Off"/>.
    /// <para>解析出的级别；未设置或无法识别的文本为 <see cref="CommLogLevel.Off"/>。</para></returns>
    public static CommLogLevel ParseLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CommLogLevel.Off;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "none" => CommLogLevel.Off,
            "error" => CommLogLevel.Error,
            "info" => CommLogLevel.Info,
            "debug" or "1" or "true" => CommLogLevel.Debug,
            "trace" or "2" or "all" or "verbose" => CommLogLevel.Trace,
            _ => CommLogLevel.Off,
        };
    }

    /// <summary>Gets whether a level is currently emitted. <para>获取某个级别当前是否输出。</para></summary>
    /// <param name="level">Level to test. <para>要测试的级别。</para></param>
    /// <returns><see langword="true"/> when the level is enabled.
    /// <para>该级别启用时为 <see langword="true"/>。</para></returns>
    public static bool IsEnabled(CommLogLevel level) => level != CommLogLevel.Off && level <= _level;

    /// <summary>Writes one message, formatting it only when the level is enabled.
    /// <para>写入一条消息，仅在级别启用时才执行格式化。</para>
    /// </summary>
    /// <param name="level">Message severity. <para>消息严重级别。</para></param>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Interpolated message; no text is built when the level is off.
    /// <para>插值消息；级别关闭时不构建任何文本。</para></param>
    public static void Write(CommLogLevel level, string category,
        [InterpolatedStringHandlerArgument("level")] ref CommTraceMessage message)
    {
        if (!message.IsEnabled)
        {
            return;
        }

        Logger.Log(level, category, message.ToStringAndClear());
    }

    /// <summary>Writes one already-rendered message, skipped unless the level is enabled.
    /// <para>写入一条已渲染的消息；级别未启用时跳过。</para>
    /// </summary>
    /// <param name="level">Message severity. <para>消息严重级别。</para></param>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public static void Write(CommLogLevel level, string category, string message)
    {
        if (!IsEnabled(level) || message is null)
        {
            return;
        }

        Logger.Log(level, category, message);
    }

    /// <summary>Writes an already-rendered error that no level switch can silence.
    /// <para>写入一条已渲染的错误，任何级别开关都无法屏蔽。</para>
    /// </summary>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public static void Error(string category, string message)
    {
        if (message is null)
        {
            return;
        }

        Logger.Log(CommLogLevel.Error, category, message);
    }

    /// <summary>Writes an already-rendered debug message. <para>写入一条已渲染的 debug 消息。</para></summary>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public static void Debug(string category, string message) => Write(CommLogLevel.Debug, category, message);

    /// <summary>Writes per-chunk detail at trace level. <para>在 trace 级别写入逐块细节。</para></summary>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public static void Trace(string category, string message) => Write(CommLogLevel.Trace, category, message);

    /// <summary>Writes a link state change. <para>写入链路状态变化。</para></summary>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public static void State(string category, string message) => Write(CommLogLevel.Info, category, message);

    /// <summary>Writes a bounded payload preview at trace level; nothing is emitted unless raw capture is on.
    /// <para>在 trace 级别写入有界负载预览；未开启原始捕获时不输出。</para>
    /// </summary>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="label">What the bytes are, such as <c>stdout</c>. <para>字节的含义，例如 <c>stdout</c>。</para></param>
    /// <param name="payload">Payload bytes; the preview is capped at <see cref="MaxRawPayloadBytes"/>.
    /// <para>负载字节；预览上限为 <see cref="MaxRawPayloadBytes"/>。</para></param>
    public static void Payload(string category, string label, ReadOnlySpan<byte> payload)
    {
        if (!CaptureRawPayloads || !IsEnabled(CommLogLevel.Trace))
        {
            return;
        }

        Logger.Log(CommLogLevel.Trace, category,
            $"{label}: {PayloadPreview.Format(payload, MaxRawPayloadBytes)}");
    }
}

/// <summary>
/// Formats an interpolated diagnostic message only when its level is enabled, so a silenced trace performs no
/// formatting and allocates nothing.
/// <para>仅在级别启用时才格式化插值诊断消息，使被关闭的 trace 不执行格式化也不产生分配。</para>
/// </summary>
[InterpolatedStringHandler]
public ref struct CommTraceMessage
{
    private DefaultInterpolatedStringHandler _builder;
    private readonly bool _enabled;

    /// <summary>
    /// Creates an always-enabled handler for messages that no level switch can silence.
    /// <para>创建始终启用的处理器，用于任何级别开关都无法屏蔽的消息。</para>
    /// </summary>
    /// <param name="literalLength">Literal character count supplied by the compiler.
    /// <para>编译器提供的字面量字符数。</para></param>
    /// <param name="formattedCount">Placeholder count supplied by the compiler.
    /// <para>编译器提供的占位符数量。</para></param>
    public CommTraceMessage(int literalLength, int formattedCount)
    {
        _enabled = true;
        _builder = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
    }

    /// <summary>
    /// Creates a handler that formats only when <paramref name="level"/> is currently emitted.
    /// <para>创建仅在 <paramref name="level"/> 当前会输出时才格式化的处理器。</para>
    /// </summary>
    /// <param name="literalLength">Literal character count supplied by the compiler.
    /// <para>编译器提供的字面量字符数。</para></param>
    /// <param name="formattedCount">Placeholder count supplied by the compiler.
    /// <para>编译器提供的占位符数量。</para></param>
    /// <param name="level">Level the message will be written at. <para>消息将要写入的级别。</para></param>
    /// <param name="isEnabled">Receives whether appends run. <para>接收是否执行追加的标志。</para></param>
    public CommTraceMessage(int literalLength, int formattedCount, CommLogLevel level, out bool isEnabled)
    {
        isEnabled = CommTrace.IsEnabled(level);
        _enabled = isEnabled;
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>
    /// Gets whether the handler produced text.
    /// <para>获取处理器是否产生了文本。</para>
    /// </summary>
    /// <value><see langword="true"/> when appends were enabled. <para>追加启用时为 <see langword="true"/>。</para></value>
    internal readonly bool IsEnabled => _enabled;

    /// <summary>
    /// Appends a literal run.
    /// <para>追加一段字面量。</para>
    /// </summary>
    /// <param name="value">Literal text. <para>字面量文本。</para></param>
    public void AppendLiteral(string value)
    {
        if (_enabled)
        {
            _builder.AppendLiteral(value);
        }
    }

    /// <summary>
    /// Appends one placeholder value.
    /// <para>追加一个占位符取值。</para>
    /// </summary>
    /// <typeparam name="T">Value type. <para>取值类型。</para></typeparam>
    /// <param name="value">Value to format. <para>待格式化的取值。</para></param>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled)
        {
            _builder.AppendFormatted(value);
        }
    }

    /// <summary>
    /// Appends one placeholder value with an alignment.
    /// <para>带对齐地追加一个占位符取值。</para>
    /// </summary>
    /// <typeparam name="T">Value type. <para>取值类型。</para></typeparam>
    /// <param name="value">Value to format. <para>待格式化的取值。</para></param>
    /// <param name="alignment">Field width; negative means left-aligned. <para>字段宽度；负值表示左对齐。</para></param>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled)
        {
            _builder.AppendFormatted(value, alignment);
        }
    }

    /// <summary>
    /// Appends one placeholder value with a format specifier.
    /// <para>带格式说明符地追加一个占位符取值。</para>
    /// </summary>
    /// <typeparam name="T">Value type. <para>取值类型。</para></typeparam>
    /// <param name="value">Value to format. <para>待格式化的取值。</para></param>
    /// <param name="format">Format specifier, or null for the default. <para>格式说明符；null 表示默认格式。</para></param>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled)
        {
            _builder.AppendFormatted(value, format);
        }
    }

    /// <summary>
    /// Appends one placeholder value with an alignment and a format specifier.
    /// <para>带对齐与格式说明符地追加一个占位符取值。</para>
    /// </summary>
    /// <typeparam name="T">Value type. <para>取值类型。</para></typeparam>
    /// <param name="value">Value to format. <para>待格式化的取值。</para></param>
    /// <param name="alignment">Field width; negative means left-aligned. <para>字段宽度；负值表示左对齐。</para></param>
    /// <param name="format">Format specifier, or null for the default. <para>格式说明符；null 表示默认格式。</para></param>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled)
        {
            _builder.AppendFormatted(value, alignment, format);
        }
    }

    /// <summary>
    /// Appends one string value.
    /// <para>追加一个字符串取值。</para>
    /// </summary>
    /// <param name="value">String to append, or null for no text. <para>要追加的字符串；null 表示不追加文本。</para></param>
    public void AppendFormatted(string? value)
    {
        if (_enabled)
        {
            _builder.AppendFormatted(value);
        }
    }

    /// <summary>
    /// Appends one pre-boxed value, matching the compiler fallback for object-typed holes.
    /// <para>追加一个已装箱取值，对应 object 类型占位符的编译器回退路径。</para>
    /// </summary>
    /// <param name="value">Value to format. <para>待格式化的取值。</para></param>
    /// <param name="alignment">Field width; negative means left-aligned. <para>字段宽度；负值表示左对齐。</para></param>
    /// <param name="format">Format specifier, or null for the default. <para>格式说明符；null 表示默认格式。</para></param>
    public void AppendFormatted(object? value, int alignment = 0, string? format = null)
    {
        if (_enabled)
        {
            _builder.AppendFormatted(value, alignment, format);
        }
    }

    /// <summary>
    /// Returns the rendered text and releases the builder buffer.
    /// <para>返回渲染后的文本并释放构建缓冲。</para>
    /// </summary>
    /// <returns>The formatted message, or an empty string when formatting was suppressed.
    /// <para>格式化后的消息；格式化被抑制时为空字符串。</para></returns>
    internal string ToStringAndClear() => _enabled ? _builder.ToStringAndClear() : string.Empty;
}