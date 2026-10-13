using System.Globalization;
using System.Text.RegularExpressions;
using UotanStudio.Services.Logging;

namespace UotanStudio.Services.Devices;

/// <summary>
/// A logcat threadtime entry carrying the fields parsed from one device output line.
/// <para>logcat threadtime 条目，携带从单行设备输出解析出的各字段。</para>
/// </summary>
/// <param name="RawText">Original device output. <para>设备原始输出。</para></param>
/// <param name="Timestamp">Device date and time. <para>设备日期时间。</para></param>
/// <param name="ProcessId">Process ID. <para>进程 ID。</para></param>
/// <param name="ThreadId">Thread ID. <para>线程 ID。</para></param>
/// <param name="Tag">Log tag. <para>日志组件。</para></param>
/// <param name="ProcessName">Process name, or - when unavailable. <para>进程名称，未知时为 -。</para></param>
/// <param name="Priority">Logcat priority letter. <para>logcat 等级字母。</para></param>
/// <param name="Message">Message text. <para>消息正文。</para></param>
public sealed partial record AdbLogcatEntry(string RawText, string Timestamp, int ProcessId, int ThreadId,
    string Tag, string ProcessName, char Priority, string Message) : ILogLine
{
    /// <summary>
    /// Unified level mapped from the priority letter.
    /// <para>由等级字母映射出的统一等级。</para>
    /// </summary>
    /// <value>The mapped level; null for non-entry lines. <para>映射后的等级；非日志条目行为 null。</para></value>
    public LogLevel? Level { get; } = Priority switch
    {
        'V' => LogLevel.Trace,
        'D' => LogLevel.Debug,
        'I' => LogLevel.Info,
        'W' => LogLevel.Warn,
        'E' => LogLevel.Error,
        'F' or 'A' => LogLevel.Fatal,
        _ => null
    };

    /// <summary>
    /// Complete displayed and searchable text.
    /// <para>用于显示和搜索的完整日志行。</para>
    /// </summary>
    private string? _text;
    public string Text => _text ??= Priority == '\0' ? RawText
        : $"{Timestamp,-23} {ProcessId + "-" + ThreadId,-13} {Tag,-24} {ProcessName,-32} {Priority} {Message}";

    /// <summary>
    /// Display columns retaining complete field spans.
    /// <para>保留完整字段范围的显示列。</para>
    /// </summary>
    private IReadOnlyList<LogTextColumn>? _columns;
    public IReadOnlyList<LogTextColumn> Columns => _columns ??= Priority == '\0' ? [] :
    [
        new(Timestamp, Math.Max(Timestamp.Length, 23) + 1, 24),
        new(ProcessId + "-" + ThreadId, Math.Max((ProcessId + "-" + ThreadId).Length, 13) + 1, 14),
        new(Tag, Math.Max(Tag.Length, 24) + 1, 25, TrimMiddle: true),
        new(ProcessName, Math.Max(ProcessName.Length, 32) + 1, 33, TrimMiddle: true),
        new(Priority.ToString(), 1, 3, IsPriority: true)
    ];

    /// <summary>
    /// Parses threadtime output; preserves headers and unrecognized lines verbatim.
    /// <para>解析 threadtime 输出；原样保留缓冲区标题和未识别的行。</para>
    /// </summary>
    /// <param name="text">One raw logcat line. <para>单条原始 logcat 文本。</para></param>
    /// <param name="processNames">Process ID to name map; null when unavailable. <para>进程 ID 到名称的映射；不可用时为 null。</para></param>
    /// <returns>The parsed entry, or a verbatim entry for unparsed lines. <para>解析后的条目；未能解析的行按原样生成条目。</para></returns>
    public static AdbLogcatEntry Parse(string text, IReadOnlyDictionary<int, string>? processNames = null)
    {
        if (text.Length > AdbLogcatLineDecoder.MaximumLineLength)
            text = text[..AdbLogcatLineDecoder.MaximumLineLength];
        return ParseComplete(text, processNames);
    }

    internal static AdbLogcatEntry ParseComplete(string text, IReadOnlyDictionary<int, string>? processNames = null)
    {
        var match = ThreadtimePattern().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["pid"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(match.Groups["tid"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var tid))
            return new(text, string.Empty, 0, 0, string.Empty, "-", '\0', text);
        var name = processNames is not null && processNames.TryGetValue(pid, out var value) ? value : "-";
        return new(text, match.Groups["date"].Value + " " + match.Groups["time"].Value, pid, tid,
            match.Groups["tag"].Value.TrimEnd(), name, match.Groups["level"].Value[0], match.Groups["message"].Value);
    }

    [GeneratedRegex(@"^(?<date>(?:[0-9]{4}-)?[0-9]{2}-[0-9]{2})[ \t]+(?<time>[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+)[ \t]+(?<pid>[0-9]+)[ \t]+(?<tid>[0-9]+)[ \t]+(?<level>[VDIWEFA])[ \t]+(?<tag>.*?):[ \t](?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreadtimePattern();
}