using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Logging;

internal sealed partial class LogTextParser
{
    private ILogLine? _pending;
    private StringBuilder? _text;
    private bool _longFormat;
    private bool _firstLongMessage;

    internal void Add(string text, List<ILogLine> entries)
    {
        var parsed = ParseLine(text, out var longFormat);
        if (parsed is not null)
        {
            Finish(entries);
            _pending = parsed;
            _longFormat = longFormat;
            _firstLongMessage = longFormat;
            return;
        }
        if (_pending is not null && ((_longFormat && text.Length > 0)
            || text.StartsWith(' ') || text.StartsWith('\t') || text.StartsWith("Caused by:", StringComparison.Ordinal)))
        {
            _text ??= new StringBuilder(_pending.Text);
            if (!_firstLongMessage) _text.Append('\n');
            _text.Append(text);
            _firstLongMessage = false;
            if (_text.Length > LogFileService.MaximumEntryCharacters)
                throw new InvalidDataException(LanguageService.Instance.GetString("Log_FileTooLarge"));
            return;
        }
        Finish(entries);
        entries.Add(new LogFileRecord(text, null, []));
    }

    internal void Finish(List<ILogLine> entries)
    {
        if (_pending is not null)
            entries.Add(_text is null ? _pending : new LogFileRecord(_text.ToString(), _pending.Level, _pending.Columns.ToArray()));
        _pending = null;
        _text = null;
        _longFormat = false;
    }

    internal static bool Recognizes(string text) => ParseLine(text, out _) is not null;

    private static ILogLine? ParseLine(string text, out bool longFormat)
    {
        longFormat = false;
        var project = ProjectPattern().Match(text);
        if (project.Success && Enum.TryParse<LogLevel>(project.Groups["level"].Value, true, out var level))
        {
            var timestamp = project.Groups["timestamp"];
            var source = project.Groups["source"];
            var message = project.Groups["message"];
            var severityStart = text.IndexOf('[', timestamp.Index + timestamp.Length);
            return new LogFileRecord(text, level,
            [
                new(timestamp.Value, severityStart, 24),
                new(level == LogLevel.Trace ? "V" : level.ToString()[..1], source.Index - severityStart, 3, IsPriority: true),
                new(source.Value, message.Index - source.Index, 33, TrimMiddle: true)
            ]);
        }
        var threadtime = AdbLogcatEntry.ParseComplete(text);
        if (threadtime.Level is not null) return threadtime;

        var match = LongPattern().Match(text);
        if (match.Success) longFormat = true;
        else
        {
            match = TimePattern().Match(text);
            if (!match.Success) match = BriefPattern().Match(text);
        }
        if (!match.Success) return null;
        var priority = match.Groups["priority"].Value[0];
        var timestampText = match.Groups["timestamp"].Success ? match.Groups["timestamp"].Value : "-";
        var pid = match.Groups["pid"].Value;
        var tid = match.Groups["tid"].Success ? match.Groups["tid"].Value : "-";
        var tag = match.Groups["tag"].Value.Trim();
        var messageText = match.Groups["message"].Value;
        var ids = pid + "-" + tid;
        var complete = $"{timestampText,-23} {ids,-13} {tag,-24} {"-",-32} {priority} {messageText}";
        var severity = priority switch
        {
            'V' => LogLevel.Trace, 'D' => LogLevel.Debug, 'I' => LogLevel.Info,
            'W' => LogLevel.Warn, 'E' => LogLevel.Error, _ => LogLevel.Fatal
        };
        return new LogFileRecord(complete, severity,
        [
            new(timestampText, Math.Max(timestampText.Length, 23) + 1, 24),
            new(ids, Math.Max(ids.Length, 13) + 1, 14),
            new(tag, Math.Max(tag.Length, 24) + 1, 25, TrimMiddle: true),
            new("-", 33, 33, TrimMiddle: true),
            new(priority.ToString(), 1, 3, IsPriority: true)
        ]);
    }

    [GeneratedRegex(@"^(?<timestamp>(?:[0-9]{4}-[0-9]{2}-[0-9]{2} )?[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?)\s+\[(?<level>Trace|Debug|Info|Warn|Error|Fatal)\s*\]\s+(?<source>.*?) - (?<message>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPattern();

    [GeneratedRegex(@"^(?<priority>[VDIWEFA])/(?<tag>.*?)\(\s*(?<pid>[0-9]+)\s*\):\s?(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BriefPattern();

    [GeneratedRegex(@"^(?<timestamp>(?:[0-9]{4}-)?[0-9]{2}-[0-9]{2}\s+[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+)\s+(?<priority>[VDIWEFA])/(?<tag>.*?)\(\s*(?<pid>[0-9]+)\s*\):\s?(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^\[\s*(?<timestamp>(?:[0-9]{4}-)?[0-9]{2}-[0-9]{2}\s+[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+)\s+(?<pid>[0-9]+):\s*(?<tid>[0-9]+)\s+(?<priority>[VDIWEFA])/(?<tag>.*?)\s*\]$", RegexOptions.CultureInvariant)]
    private static partial Regex LongPattern();
}
