using System.Globalization;

namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Command kinds in an Android OTA transfer list.
/// <para>Android OTA transfer list 中的命令类型。</para>
/// </summary>
public enum TransferListCommandKind
{
    /// <summary><para>从 <c>.new.dat</c> 顺序读取数据块。</para>Read data blocks sequentially from the <c>.new.dat</c> file.</summary>
    New,

    /// <summary><para>将目标块区间写为零。</para>Write zeros to the target block ranges.</summary>
    Zero,

    /// <summary><para>擦除目标块区间（等效于写零）。</para>Erase the target block ranges (equivalent to writing zeros).</summary>
    Erase,

    /// <summary><para>释放目标块区间（不写入数据）。</para>Free the target block ranges (no data written).</summary>
    Free,

    /// <summary><para>用 <c>.patch.dat</c> 增量修补源镜像块区间（需源镜像）。</para>Patch source image ranges using <c>.patch.dat</c> (requires a source image).</summary>
    Diff,

    /// <summary><para>暂存块区间供后续命令使用（增量 OTA）。</para>Stash block ranges for later commands (incremental OTA).</summary>
    Stash
}

/// <summary>
/// A half-open block range <c>[Start, End)</c>.
/// <para>半开块区间 <c>[Start, End)</c>。</para>
/// </summary>
/// <param name="Start">Inclusive first block index. <para>包含的首块索引。</para></param>
/// <param name="End">Exclusive last block index. <para>不包含的末块索引。</para></param>
public readonly record struct BlockRange(long Start, long End)
{
    /// <summary>
    /// Number of blocks in the range.
    /// <para>区间内的块数。</para>
    /// </summary>
    public long BlockCount => End - Start;
}

/// <summary>
/// A single transfer-list command with its block ranges.
/// <para>一条 transfer list 命令及其块范围。</para>
/// </summary>
public sealed class TransferListCommand
{
    /// <summary>
    /// Creates a transfer-list command.
    /// <para>创建一条 transfer list 命令。</para>
    /// </summary>
    /// <param name="kind">The command kind. <para>命令类型。</para></param>
    /// <param name="ranges">The block ranges. <para>块范围列表。</para></param>
    /// <param name="stashId">The stash id for <see cref="TransferListCommandKind.Stash"/> commands; otherwise <c>null</c>. <para>Stash 命令的暂存 id；其他命令为 <c>null</c>。</para></param>
    public TransferListCommand(TransferListCommandKind kind, IReadOnlyList<BlockRange> ranges, string? stashId = null)
    {
        Kind = kind;
        Ranges = ranges;
        StashId = stashId;
    }

    /// <summary>
    /// The command kind.
    /// <para>命令类型。</para>
    /// </summary>
    public TransferListCommandKind Kind { get; }

    /// <summary>
    /// The block ranges targeted by this command.
    /// <para>该命令作用的块范围。</para>
    /// </summary>
    public IReadOnlyList<BlockRange> Ranges { get; }

    /// <summary>
    /// The stash id for <see cref="TransferListCommandKind.Stash"/> commands; otherwise <c>null</c>.
    /// <para>Stash 命令的暂存 id；其他命令为 <c>null</c>。</para>
    /// </summary>
    public string? StashId { get; }
}

/// <summary>
/// A parsed Android OTA transfer list.
/// <para>解析后的 Android OTA transfer list。</para>
/// </summary>
public sealed class TransferList
{
    /// <summary>
    /// Creates a parsed transfer list, computing derived totals.
    /// <para>创建解析后的 transfer list，并计算派生总量。</para>
    /// </summary>
    /// <param name="version">Format version. <para>格式版本。</para></param>
    /// <param name="newBlocks">Total number of blocks to write. <para>待写入的总块数。</para></param>
    /// <param name="stashEntries">Number of stash entries needed simultaneously. <para>同时需要的暂存条目数。</para></param>
    /// <param name="maxStashedBlocks">Maximum number of simultaneously stashed blocks. <para>同时暂存的最大块数。</para></param>
    /// <param name="commands">The parsed commands. <para>解析出的命令列表。</para></param>
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

        TotalBlocks = total;
        NewDataBlocks = newData;
    }

    /// <summary>
    /// Format version (4 for Android 8+).
    /// <para>格式版本（Android 8+ 为 4）。</para>
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// Total number of blocks to write.
    /// <para>待写入的总块数。</para>
    /// </summary>
    public long NewBlocks { get; }

    /// <summary>
    /// Number of stash entries needed simultaneously.
    /// <para>同时需要的暂存条目数。</para>
    /// </summary>
    public int StashEntries { get; }

    /// <summary>
    /// Maximum number of simultaneously stashed blocks.
    /// <para>同时暂存的最大块数。</para>
    /// </summary>
    public long MaxStashedBlocks { get; }

    /// <summary>
    /// The parsed commands.
    /// <para>解析出的命令列表。</para>
    /// </summary>
    public IReadOnlyList<TransferListCommand> Commands { get; }

    /// <summary>
    /// Total logical blocks covered by all commands (the highest block index).
    /// <para>全部命令覆盖的逻辑块总数（最大块索引）。</para>
    /// </summary>
    public long TotalBlocks { get; }

    /// <summary>
    /// Total number of data blocks that must be read from the <c>.new.dat</c> file.
    /// <para>必须从 <c>.new.dat</c> 读取的数据块总数。</para>
    /// </summary>
    public long NewDataBlocks { get; }
}

/// <summary>
/// Parser for Android OTA transfer list files (v4+).
/// <para>Android OTA transfer list（v4+）解析器。</para>
/// </summary>
public static class TransferListParser
{
    /// <summary>
    /// Parses a transfer list from a file path.
    /// <para>从文件路径解析 transfer list。</para>
    /// </summary>
    /// <param name="path">The transfer list file path. <para>transfer list 文件路径。</para></param>
    /// <returns>The parsed transfer list. <para>解析后的 transfer list。</para></returns>
    /// <exception cref="InvalidDataException">Thrown when the file is malformed. <para>文件格式非法时抛出。</para></exception>
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
    /// <param name="reader">The text reader. <para>文本读取器。</para></param>
    /// <returns>The parsed transfer list. <para>解析后的 transfer list。</para></returns>
    /// <exception cref="InvalidDataException">Thrown when the input is malformed. <para>输入格式非法时抛出。</para></exception>
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
            _ => throw new InvalidDataException($"Unknown command '{tokens[0]}' on line {lineNumber}.  第 {lineNumber} 行存在未知命令 '{tokens[0]}'。")
        };

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