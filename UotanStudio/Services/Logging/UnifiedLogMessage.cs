using System.Runtime.CompilerServices;

namespace UotanStudio.Services.Logging;

/// <summary>
/// Formats a unified-log message only when the target accepts its level, so a filtered entry neither formats nor
/// allocates.
/// <para>仅在目标接受该级别时才格式化统一日志消息，使被过滤的条目既不格式化也不分配内存。</para>
/// </summary>
/// <remarks>
/// The receiver and the level reach the constructor through
/// <see cref="InterpolatedStringHandlerArgumentAttribute"/>, so the handler can consult
/// <see cref="IUnifiedLog.IsEnabled(LogLevel)"/> before appending anything. This mirrors the Comm diagnostics
/// handler, which suppresses formatting for silenced trace output.
/// <para>接收者与级别经 <see cref="InterpolatedStringHandlerArgumentAttribute"/> 传入构造函数，使处理器能在追加任何
/// 内容之前查询 <see cref="IUnifiedLog.IsEnabled(LogLevel)"/>。这与抑制已关闭 trace 输出的 Comm 诊断处理器一致。</para>
/// </remarks>
[InterpolatedStringHandler]
public ref struct UnifiedLogMessage
{
    private DefaultInterpolatedStringHandler _builder;
    private readonly bool _enabled;

    /// <summary>
    /// Creates a handler that formats only when <paramref name="log"/> accepts <paramref name="level"/>.
    /// <para>创建仅在 <paramref name="log"/> 接受 <paramref name="level"/> 时才格式化的处理器。</para>
    /// </summary>
    /// <param name="literalLength">Literal character count supplied by the compiler.
    /// <para>编译器提供的字面量字符数。</para></param>
    /// <param name="formattedCount">Placeholder count supplied by the compiler.
    /// <para>编译器提供的占位符数量。</para></param>
    /// <param name="log">The target deciding whether the level survives. <para>决定该级别是否保留的目标。</para></param>
    /// <param name="level">Level the message will be written at. <para>消息将要写入的级别。</para></param>
    /// <param name="isEnabled">Receives whether appends run. <para>接收是否执行追加的标志。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.
    /// <para><paramref name="log"/> 为 null。</para></exception>
    public UnifiedLogMessage(int literalLength, int formattedCount, IUnifiedLog log, LogLevel level, out bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(log);
        isEnabled = log.IsEnabled(level);
        _enabled = isEnabled;
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>
    /// Gets whether appends produced text.
    /// <para>获取追加是否产生了文本。</para>
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