using System.Globalization;

namespace UotanStudio.Firmware.Dat;

public enum TransferListCommandKind
{
    New,

    Zero,

    Erase,

    Free,

    Diff,

    Stash,

    Move
}

public readonly record struct BlockRange(long Start, long End)
{
    public long BlockCount => End - Start;
}

public sealed class TransferListCommand
{
    /// <summary>
    /// Creates a transfer-list command.
    /// <para>创建一条 transfer list 命令。</para>
    /// </summary>
    public TransferListCommand(TransferListCommandKind kind, IReadOnlyList<BlockRange> ranges, string? stashId = null, IReadOnlyList<BlockRange>? sourceRanges = null)
    {
        Kind = kind;
        Ranges = ranges;
        StashId = stashId;
        SourceRanges = sourceRanges ?? (kind == TransferListCommandKind.Diff ? ranges : []);
    }

    public TransferListCommandKind Kind { get; }

    public IReadOnlyList<BlockRange> Ranges { get; }

    public string? StashId { get; }

    public IReadOnlyList<BlockRange> SourceRanges { get; }
}

public sealed class TransferList
{
    /// <summary>
    /// Creates a parsed transfer list, computing derived totals.
    /// <para>创建解析后的 transfer list，并计算派生总量。</para>
    /// </summary>
    public TransferList(int version, long newBlocks, int stashEntries, long maxStashedBlocks, IReadOnlyList<TransferListCommand> commands)
    {
        Version = version;
        NewBlocks = newBlocks;
        StashEntries = stashEntries;
        MaxStashedBlocks = maxStashedBlocks;
        Commands = commands;

        long total = 0;
        long newData = 0;
        foreach (var command in commands)
        {
            foreach (var range in command.Ranges)
            {
                total = Math.Max(total, range.End);
                if (command.Kind == TransferListCommandKind.New)
                {
                    newData += range.BlockCount;
                }
            }
        }


        TotalBlocks = Math.Max(newBlocks, total);
        NewDataBlocks = newData;
    }

    public int Version { get; }

    public long NewBlocks { get; }

    public int StashEntries { get; }

    public long MaxStashedBlocks { get; }

    public IReadOnlyList<TransferListCommand> Commands { get; }

    public long TotalBlocks { get; }

    public long NewDataBlocks { get; }
}

public static class TransferListParser
{
    /// <summary>
    /// Parses a transfer list from a file path.
    /// <para>从文件路径解析 transfer list。</para>
    /// </summary>
    public static TransferList Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = File.OpenText(path);
        return Parse(reader);
    }

    /// <summary>
    /// Parses a transfer list from a text reader.
    /// <para>从文本读取器解析 transfer list。</para>
    /// </summary>
    public static TransferList Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line.Trim());
            }
        }

        if (lines.Count < 2)
        {
            throw new InvalidDataException("Transfer list is too short.  transfer list 内容过短。");
        }

        if (!int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidDataException($"Invalid transfer list version: '{lines[0]}'.  版本号非法：'{lines[0]}'。");
        }

        if (!long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var newBlocks) || newBlocks < 0)
        {
            throw new InvalidDataException($"Invalid new-blocks count: '{lines[1]}'.  块数非法：'{lines[1]}'。");
        }

        var commandStart = 2;
        int stashEntries = 0;
        long maxStashedBlocks = 0;
        if (version >= 2)
        {
            if (lines.Count < 4)
            {
                throw new InvalidDataException("Transfer list v2+ must include stash counts.  v2+ 必须包含暂存计数。");
            }

            if (!int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var stash) || stash < 0)
            {
                throw new InvalidDataException($"Invalid stash-entries count: '{lines[2]}'.  暂存条目数非法：'{lines[2]}'。");
            }

            if (!long.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var maxStashed) || maxStashed < 0)
            {
                throw new InvalidDataException($"Invalid max-stashed-blocks count: '{lines[3]}'.  最大暂存块数非法：'{lines[3]}'。");
            }

            stashEntries = stash;
            maxStashedBlocks = maxStashed;
            commandStart = 4;
        }

        var commands = new List<TransferListCommand>(lines.Count - commandStart);
        for (var i = commandStart; i < lines.Count; i++)
        {
            commands.Add(ParseCommand(lines[i], i + 1));
        }

        return new TransferList(version, newBlocks, stashEntries, maxStashedBlocks, commands);
    }

    private static TransferListCommand ParseCommand(string line, int lineNumber)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw new InvalidDataException($"Invalid command on line {lineNumber}: '{line}'.  第 {lineNumber} 行命令非法：'{line}'。");
        }

        var kind = tokens[0] switch
        {
            "new" => TransferListCommandKind.New,
            "zero" => TransferListCommandKind.Zero,
            "erase" => TransferListCommandKind.Erase,
            "free" => TransferListCommandKind.Free,
            "diff" => TransferListCommandKind.Diff,
            "stash" => TransferListCommandKind.Stash,
            "move" => TransferListCommandKind.Move,
            _ => throw new InvalidDataException($"Unknown command '{tokens[0]}' on line {lineNumber}.  第 {lineNumber} 行存在未知命令 '{tokens[0]}'。")
        };

        if (kind is TransferListCommandKind.Move or TransferListCommandKind.Diff)
        {


            if (tokens.Length == 3)
            {
                return new TransferListCommand(
                    kind,
                    ParseRangeSet(tokens[2], lineNumber),
                    null,
                    ParseRangeSet(tokens[1], lineNumber));
            }


            if (kind == TransferListCommandKind.Diff && tokens.Length == 2)
            {
                var ranges = ParseRangeSet(tokens[1], lineNumber);
                return new TransferListCommand(kind, ranges, null, ranges);
            }

            throw new InvalidDataException($"Invalid '{tokens[0]}' command on line {lineNumber}: '{line}'.  第 {lineNumber} 行 '{tokens[0]}' 命令非法：'{line}'。");
        }

        var hasStashId = kind == TransferListCommandKind.Stash && tokens.Length == 3;
        var rangeSetToken = hasStashId ? tokens[2] : tokens[1];
        var stashId = hasStashId ? tokens[1] : null;

        return new TransferListCommand(kind, ParseRangeSet(rangeSetToken, lineNumber), stashId);
    }

    private static List<BlockRange> ParseRangeSet(string token, int lineNumber)
    {
        var parts = token.Split(',');
        if (parts.Length < 3)
        {
            throw new InvalidDataException($"Invalid range set on line {lineNumber}: '{token}'.  第 {lineNumber} 行范围集非法：'{token}'。");
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            throw new InvalidDataException($"Invalid range count on line {lineNumber}: '{parts[0]}'.  第 {lineNumber} 行范围数量非法：'{parts[0]}'。");
        }

        if (parts.Length != 1 + (count * 2))
        {
            throw new InvalidDataException($"Range set on line {lineNumber} declares {count} ranges but has {parts.Length - 1} numbers.  第 {lineNumber} 行范围集声明 {count} 个范围但包含 {parts.Length - 1} 个数字。");
        }

        var ranges = new List<BlockRange>(count);
        for (var i = 0; i < count; i++)
        {
            if (!long.TryParse(parts[1 + (i * 2)], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                !long.TryParse(parts[2 + (i * 2)], NumberStyles.None, CultureInfo.InvariantCulture, out var end))
            {
                throw new InvalidDataException($"Invalid range bounds on line {lineNumber}.  第 {lineNumber} 行范围边界非法。");
            }

            if (start < 0 || end <= start)
            {
                throw new InvalidDataException($"Invalid range [{start},{end}) on line {lineNumber}.  第 {lineNumber} 行范围 [{start},{end}) 非法。");
            }

            ranges.Add(new BlockRange(start, end));
        }

        return ranges;
    }
}
