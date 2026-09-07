using System.Globalization;
using System.Text.RegularExpressions;

namespace UotanStudio.Services.Devices;

/// <summary>
/// A logcat entry with the fields displayed by Android Studio.
/// <para>携带 Android Studio 日志行所显示字段的 logcat 条目。</para>
/// </summary>
/// <param name="RawText">Original device output. 设备原始输出。</param>
/// <param name="Timestamp">Device date and time. 设备日期时间。</param>
/// <param name="ProcessId">Process ID. 进程 ID。</param>
/// <param name="ThreadId">Thread ID. 线程 ID。</param>
/// <param name="Tag">Log tag. 日志组件。</param>
/// <param name="ProcessName">Process name, or - when unavailable. 进程名称，未知时为 -。</param>
/// <param name="Priority">Logcat priority letter. logcat 等级字母。</param>
/// <param name="Message">Message text. 消息正文。</param>
public sealed partial record AdbLogcatEntry(string RawText, string Timestamp, int ProcessId, int ThreadId,
    string Tag, string ProcessName, char Priority, string Message)
{
    /// <summary>Unified level; null for non-entry lines. 统一等级，非日志条目行为空。</summary>
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

    /// <summary>Complete displayed and searchable text. 用于显示和搜索的完整日志行。</summary>
    public string Text { get; } = Priority == '\0' ? RawText
        : $"{Timestamp,-23} {ProcessId + "-" + ThreadId,-13} {Tag,-24} {ProcessName,-32} {Priority} {Message}";

    /// <summary>
    /// Parses threadtime output; preserves headers and unrecognized lines verbatim.
    /// <para>解析 threadtime 输出；原样保留缓冲区标题和未识别的行。</para>
    /// </summary>
    public static AdbLogcatEntry Parse(string text, IReadOnlyDictionary<int, string>? processNames = null)
    {
        if (text.Length > AdbLogcatLineDecoder.MaximumLineLength)
            text = text[..AdbLogcatLineDecoder.MaximumLineLength];
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
