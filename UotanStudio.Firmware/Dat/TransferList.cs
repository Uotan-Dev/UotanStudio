using System.Globalization;

namespace UotanStudio.Firmware.Dat;

/// <summary>Identifies an Android transfer command. <para>标识 Android 传输命令。</para></summary>
public enum TransferListCommandKind
{
    /// <summary>Writes new blocks. <para>写入新块。</para></summary>
    New,
    /// <summary>Clears blocks. <para>清零块。</para></summary>
    Zero,
    /// <summary>Discards blocks. <para>丢弃块。</para></summary>
    Erase,
    /// <summary>Releases a stash. <para>释放暂存。</para></summary>
    Free,
    /// <summary>Applies BSDIFF. <para>应用 BSDIFF。</para></summary>
    Diff,
    /// <summary>Saves source blocks. <para>暂存源块。</para></summary>
    Stash,
    /// <summary>Copies source blocks. <para>复制源块。</para></summary>
    Move,
    /// <summary>Applies IMGDIFF. <para>应用 IMGDIFF。</para></summary>
    Imgdiff
}

/// <summary>Describes a half-open block interval. <para>描述左闭右开块区间。</para></summary>
public readonly record struct BlockRange(long Start, long End)
{
    /// <summary>Gets the block count. <para>获取块数。</para></summary>
    public long BlockCount => checked(End - Start);
}

/// <summary>Describes one transfer command and its source mapping. <para>描述传输命令及其源映射。</para></summary>
public sealed class TransferListCommand
{
    /// <summary>Initializes a transfer command. <para>初始化传输命令。</para></summary>
    public TransferListCommand(TransferListCommandKind kind, IReadOnlyList<BlockRange> ranges, string? stashId = null, IReadOnlyList<BlockRange>? sourceRanges = null)
    { Kind = kind; Ranges = ranges; StashId = stashId; SourceRanges = sourceRanges ?? []; }
    /// <summary>Gets the command kind. <para>获取命令类型。</para></summary>
    public TransferListCommandKind Kind { get; }
    /// <summary>Gets destination ranges, or saved ranges for stash. <para>获取目标区间或暂存区间。</para></summary>
    public IReadOnlyList<BlockRange> Ranges { get; }
    /// <summary>Gets the stash identifier. <para>获取暂存标识。</para></summary>
    public string? StashId { get; }
    /// <summary>Gets source disk ranges. <para>获取源磁盘区间。</para></summary>
    public IReadOnlyList<BlockRange> SourceRanges { get; }
    /// <summary>Gets source buffer placement ranges. <para>获取源缓冲区放置区间。</para></summary>
    public IReadOnlyList<BlockRange> SourceLocations { get; internal init; } = [];
    /// <summary>Gets stash placement ranges keyed by identifier. <para>获取按标识索引的暂存放置区间。</para></summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BlockRange>> StashReferences { get; internal init; } = new Dictionary<string, IReadOnlyList<BlockRange>>();
    /// <summary>Gets the assembled source block count. <para>获取组装后的源块数。</para></summary>
    public long SourceBlockCount { get; internal init; }
    /// <summary>Gets the patch offset. <para>获取补丁偏移。</para></summary>
    public long PatchOffset { get; internal init; }
    /// <summary>Gets the patch length. <para>获取补丁长度。</para></summary>
    public long PatchLength { get; internal init; }
    /// <summary>Gets the source SHA-1. <para>获取源 SHA-1。</para></summary>
    public string? SourceHash { get; internal init; }
    /// <summary>Gets the target SHA-1. <para>获取目标 SHA-1。</para></summary>
    public string? TargetHash { get; internal init; }
}

/// <summary>Contains a parsed Android transfer list. <para>包含解析后的 Android 传输列表。</para></summary>
public sealed class TransferList
{
    /// <summary>Initializes a transfer list and derives its destination size. <para>初始化传输列表并计算目标大小。</para></summary>
    public TransferList(int version, long newBlocks, int stashEntries, long maxStashedBlocks, IReadOnlyList<TransferListCommand> commands)
    {
        Version = version; NewBlocks = newBlocks; StashEntries = stashEntries; MaxStashedBlocks = maxStashedBlocks; Commands = commands;
        foreach (var command in commands)
        {
            if (command.Kind is TransferListCommandKind.Stash or TransferListCommandKind.Free) continue;
            foreach (var range in command.Ranges)
            {
                TotalBlocks = Math.Max(TotalBlocks, range.End);
                if (command.Kind == TransferListCommandKind.New) NewDataBlocks = checked(NewDataBlocks + range.BlockCount);
            }
        }
    }
    /// <summary>Gets the format version. <para>获取格式版本。</para></summary>
    public int Version { get; }
    /// <summary>Gets the advertised total blocks to write, not image size. <para>获取声明的总写入块数，不是镜像大小。</para></summary>
    public long NewBlocks { get; }
    /// <summary>Gets the legacy stash entry count. <para>获取旧版暂存条目数。</para></summary>
    public int StashEntries { get; }
    /// <summary>Gets the maximum simultaneously stashed blocks. <para>获取同时暂存块数上限。</para></summary>
    public long MaxStashedBlocks { get; }
    /// <summary>Gets the ordered commands. <para>获取有序命令。</para></summary>
    public IReadOnlyList<TransferListCommand> Commands { get; }
    /// <summary>Gets the highest destination block boundary. <para>获取最高目标块边界。</para></summary>
    public long TotalBlocks { get; }
    /// <summary>Gets the number of new-data blocks consumed. <para>获取消耗的新数据块数。</para></summary>
    public long NewDataBlocks { get; }
}

/// <summary>Parses recovery transfer-list versions 1 through 4. <para>解析 recovery 传输列表版本 1 至 4。</para></summary>
public static class TransferListParser
{
    /// <summary>Parses a transfer-list file. <para>解析传输列表文件。</para></summary>
    public static TransferList Parse(string path) { using var reader = File.OpenText(path); return Parse(reader); }
    /// <summary>Parses a transfer list from text. <para>从文本解析传输列表。</para></summary>
    public static TransferList Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string RequiredLine() => reader.ReadLine()?.Trim() ?? throw new InvalidDataException("Truncated transfer-list header.");
        var version = checked((int)Number(RequiredLine()));
        if (version is < 1 or > 4) throw new InvalidDataException("Unsupported transfer-list version.");
        var blocks = Number(RequiredLine());
        var stashEntries = version >= 2 ? checked((int)Number(RequiredLine())) : 0;
        var stashBlocks = version >= 2 ? Number(RequiredLine()) : 0;
        var commands = new List<TransferListCommand>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            commands.Add(ParseCommand(line, version));
        }
        return new TransferList(version, blocks, stashEntries, stashBlocks, commands);
    }

    private static TransferListCommand ParseCommand(string line, int version)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var index = 1;
        string Next() => index < tokens.Length ? tokens[index++] : throw new InvalidDataException($"Missing transfer command argument: {line}");
        void End() { if (index != tokens.Length) throw new InvalidDataException($"Unexpected transfer command arguments: {line}"); }
        var kind = tokens[0] switch
        {
            "new" => TransferListCommandKind.New,
            "zero" => TransferListCommandKind.Zero,
            "erase" => TransferListCommandKind.Erase,
            "free" => TransferListCommandKind.Free,
            "stash" => TransferListCommandKind.Stash,
            "move" => TransferListCommandKind.Move,
            "bsdiff" => TransferListCommandKind.Diff,
            "imgdiff" => TransferListCommandKind.Imgdiff,
            _ => throw new InvalidDataException($"Unknown transfer command: {tokens[0]}")
        };
        if (kind == TransferListCommandKind.Free) { var id = Next(); End(); return new(kind, [], id); }
        if (kind == TransferListCommandKind.Stash) { var id = Next(); var ranges = Ranges(Next()); End(); return new(kind, ranges, id); }
        if (kind is TransferListCommandKind.New or TransferListCommandKind.Zero or TransferListCommandKind.Erase)
        { var ranges = Ranges(Next()); End(); return new(kind, ranges); }
        var patchOffset = kind is TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff ? Number(Next()) : 0;
        var patchLength = kind is TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff ? Number(Next()) : 0;
        string? sourceHash = null, targetHash = null;
        if (version >= 3)
        {
            sourceHash = Hash(Next());
            targetHash = kind == TransferListCommandKind.Move ? sourceHash : Hash(Next());
        }
        IReadOnlyList<BlockRange> source;
        IReadOnlyList<BlockRange> target;
        long sourceCount;
        if (version == 1)
        {
            source = Ranges(Next()); target = Ranges(Next()); sourceCount = Count(source);
        }
        else
        {
            target = Ranges(Next()); sourceCount = Number(Next()); var sourceToken = Next();
            source = sourceToken == "-" ? [] : Ranges(sourceToken);
        }
        IReadOnlyList<BlockRange> locations = source.Count == 0 ? [] : [new(0, Count(source))];
        if (index < tokens.Length && source.Count > 0) locations = Ranges(Next());
        var stashes = new Dictionary<string, IReadOnlyList<BlockRange>>(StringComparer.Ordinal);
        while (index < tokens.Length)
        {
            var reference = Next().Split(':', 2);
            if (reference.Length != 2 || !stashes.TryAdd(reference[0], Ranges(reference[1])))
                throw new InvalidDataException("Invalid or duplicate stash reference.");
        }
        if (Count(locations) != Count(source)) throw new InvalidDataException("Source locations do not match source blocks.");
        var coverage = locations.Concat(stashes.Values.SelectMany(value => value)).OrderBy(range => range.Start).ToArray();
        long covered = 0;
        foreach (var range in coverage)
        { if (range.Start != covered) throw new InvalidDataException("Source buffer has overlapping or missing ranges."); covered = range.End; }
        if (covered != sourceCount) throw new InvalidDataException("Source buffer does not match its declared block count.");
        return new(kind, target, sourceRanges: source)
        {
            SourceBlockCount = sourceCount,
            SourceLocations = locations,
            StashReferences = stashes,
            SourceHash = sourceHash,
            TargetHash = targetHash,
            PatchOffset = patchOffset,
            PatchLength = patchLength
        };
    }

    private static string Hash(string value)
    {
        if (value.Length != 40 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid transfer-list SHA-1.");
        return value;
    }
    private static long Number(string token) => long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException($"Invalid transfer-list number: {token}");
    private static long Count(IEnumerable<BlockRange> ranges) => ranges.Aggregate(0L, (count, range) => checked(count + range.BlockCount));
    private static IReadOnlyList<BlockRange> Ranges(string token)
    {
        var fields = token.Split(',');
        var endpoints = Number(fields[0]);
        if (endpoints == 0 || (endpoints & 1) != 0 || endpoints != fields.Length - 1)
            throw new InvalidDataException("Range set must declare its even endpoint count.");
        var ranges = new List<BlockRange>(fields.Length / 2);
        long previousEnd = -1;
        for (var i = 1; i < fields.Length; i += 2)
        {
            var start = Number(fields[i]); var end = Number(fields[i + 1]);
            if (end <= start || start < previousEnd) throw new InvalidDataException("Invalid or overlapping transfer range.");
            ranges.Add(new(start, end)); previousEnd = end;
        }
        return ranges;
    }
}