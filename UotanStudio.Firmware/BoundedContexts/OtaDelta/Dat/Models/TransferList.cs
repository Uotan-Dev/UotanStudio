using System.Globalization;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Models;

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
/// <param name="Start">Inclusive first block index. <para>闭区间首块索引。</para></param>
/// <param name="End">Exclusive past-the-end block index. <para>开区间末块索引（不含）。</para></param>
public readonly record struct BlockRange(long Start, long End)
{
    /// <summary>Gets the block count. <para>获取块数。</para></summary>
    /// <value>Number of blocks in the interval, computed with overflow checking.
    /// <para>区间内块数，计算时检查溢出。</para></value>
    public long BlockCount => checked(End - Start);
}

/// <summary>Describes one transfer command and its source mapping. <para>描述传输命令及其源映射。</para></summary>
public sealed class TransferListCommand
{
    /// <summary>Initializes a transfer command. <para>初始化传输命令。</para></summary>
    /// <param name="kind">Operation kind performed by this command. <para>本命令执行的传输操作类型。</para></param>
    /// <param name="ranges">Destination ranges, or saved ranges for a stash command.
    /// <para>目标区间；stash 命令时为被暂存的区间。</para></param>
    /// <param name="stashId">Stash identifier for stash/free commands; <see langword="null"/> otherwise.
    /// <para>stash/free 命令的暂存标识；其余命令为 <see langword="null"/>。</para></param>
    /// <param name="sourceRanges">Source disk ranges; <see langword="null"/> normalizes to an empty list.
    /// <para>源磁盘区间；传入 <see langword="null"/> 时归一为空列表。</para></param>
    public TransferListCommand(TransferListCommandKind kind, IReadOnlyList<BlockRange> ranges, string? stashId = null, IReadOnlyList<BlockRange>? sourceRanges = null)
    { Kind = kind; Ranges = ranges; StashId = stashId; SourceRanges = sourceRanges ?? []; }
    /// <summary>Gets the command kind. <para>获取命令类型。</para></summary>
    /// <value>The transfer operation this command performs. <para>本命令执行的传输操作。</para></value>
    public TransferListCommandKind Kind { get; }
    /// <summary>Gets destination ranges, or saved ranges for stash. <para>获取目标区间或暂存区间。</para></summary>
    /// <value>Half-open destination block ranges in file order; the blocks being saved for a stash command.
    /// <para>按文件顺序排列的左闭右开目标区间；stash 命令时为待保存的块。</para></value>
    public IReadOnlyList<BlockRange> Ranges { get; }
    /// <summary>Gets the stash identifier. <para>获取暂存标识。</para></summary>
    /// <value>Identifier text for stash/free commands; <see langword="null"/> for every other kind.
    /// <para>stash/free 命令的标识文本；其他类型均为 <see langword="null"/>。</para></value>
    public string? StashId { get; }
    /// <summary>Gets source disk ranges. <para>获取源磁盘区间。</para></summary>
    /// <value>Half-open source ranges on disk; empty when the command declares no source.
    /// <para>磁盘上的左闭右开源区间；无源时为空。</para></value>
    public IReadOnlyList<BlockRange> SourceRanges { get; }
    /// <summary>Gets source buffer placement ranges. <para>获取源缓冲区放置区间。</para></summary>
    /// <value>Ranges locating the assembled source inside the source buffer; empty without source blocks.
    /// <para>标识组装后源数据在源缓冲区内位置的区间；无源块时为空。</para></value>
    public IReadOnlyList<BlockRange> SourceLocations { get; internal init; } = [];
    /// <summary>Gets stash placement ranges keyed by identifier. <para>获取按标识索引的暂存放置区间。</para></summary>
    /// <value>Buffer placement ranges per referenced stash identifier; empty when no stash is referenced.
    /// <para>每个被引用暂存标识对应的缓冲区放置区间；未引用暂存时为空。</para></value>
    public IReadOnlyDictionary<string, IReadOnlyList<BlockRange>> StashReferences { get; internal init; } = new Dictionary<string, IReadOnlyList<BlockRange>>();
    /// <summary>Gets the assembled source block count. <para>获取组装后的源块数。</para></summary>
    /// <value>Declared total source blocks forming the patch input; <c>0</c> when there is no source.
    /// <para>构成补丁输入的声明源块总数；无源时为 <c>0</c>。</para></value>
    public long SourceBlockCount { get; internal init; }
    /// <summary>Gets the patch offset. <para>获取补丁偏移。</para></summary>
    /// <value>Byte offset into the patch data file for diff/imgdiff commands; <c>0</c> otherwise.
    /// <para>bsdiff/imgdiff 命令在补丁数据文件中的字节偏移；其他命令为 <c>0</c>。</para></value>
    public long PatchOffset { get; internal init; }
    /// <summary>Gets the patch length. <para>获取补丁长度。</para></summary>
    /// <value>Patch byte length for diff/imgdiff commands; <c>0</c> otherwise.
    /// <para>bsdiff/imgdiff 命令的补丁字节长度；其他命令为 <c>0</c>。</para></value>
    public long PatchLength { get; internal init; }
    /// <summary>Gets the source SHA-1. <para>获取源 SHA-1。</para></summary>
    /// <value>Forty-hex-digit source digest for version 3 and later; <see langword="null"/> for versions 1 and 2.
    /// <para>版本 3 及以上的 40 位十六进制源摘要；版本 1、2 为 <see langword="null"/>。</para></value>
    public string? SourceHash { get; internal init; }
    /// <summary>Gets the target SHA-1. <para>获取目标 SHA-1。</para></summary>
    /// <value>Forty-hex-digit target digest for version 3 and later, equal to <see cref="SourceHash"/> for move
    /// commands; <see langword="null"/> for versions 1 and 2.
    /// <para>版本 3 及以上的 40 位十六进制目标摘要；move 命令时与 <see cref="SourceHash"/> 相同；
    /// 版本 1、2 为 <see langword="null"/>。</para></value>
    public string? TargetHash { get; internal init; }
}

/// <summary>Contains a parsed Android transfer list. <para>包含解析后的 Android 传输列表。</para></summary>
public sealed class TransferList
{
    /// <summary>Initializes a transfer list and derives its destination size. <para>初始化传输列表并计算目标大小。</para></summary>
    /// <param name="version">Transfer-list format version. <para>传输列表格式版本。</para></param>
    /// <param name="newBlocks">Advertised total blocks to write from the header. <para>头部声明的总写入块数。</para></param>
    /// <param name="stashEntries">Legacy stash entry count from the header. <para>头部声明的传统暂存条目数。</para></param>
    /// <param name="maxStashedBlocks">Maximum simultaneously stashed blocks from the header.
    /// <para>头部声明的同时暂存块数上限。</para></param>
    /// <param name="commands">Ordered transfer commands. <para>有序的传输命令列表。</para></param>
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
    /// <value>The version line of the parsed header; the parser accepts <c>1</c> through <c>4</c>.
    /// <para>解析头部的版本号行；解析器接受 <c>1</c> 至 <c>4</c>。</para></value>
    public int Version { get; }
    /// <summary>Gets the advertised total blocks to write, not image size. <para>获取声明的总写入块数，不是镜像大小。</para></summary>
    /// <value>Header-declared block count; it can exceed or under-run the actual destination extent.
    /// <para>头部声明的块数；可大于或小于实际目标范围。</para></value>
    public long NewBlocks { get; }
    /// <summary>Gets the legacy stash entry count. <para>获取传统暂存条目数。</para></summary>
    /// <value>Header-declared stash slots; <c>0</c> for version 1, which has no stash header line.
    /// <para>头部声明的暂存槽位数；版本 1 无该头部行，为 <c>0</c>。</para></value>
    public int StashEntries { get; }
    /// <summary>Gets the maximum simultaneously stashed blocks. <para>获取同时暂存块数上限。</para></summary>
    /// <value>Header-declared stash block cap; <c>0</c> for version 1. <para>头部声明的暂存块数上限；版本 1 为 <c>0</c>。</para></value>
    public long MaxStashedBlocks { get; }
    /// <summary>Gets the ordered commands. <para>获取有序命令。</para></summary>
    /// <value>Transfer commands in file order, the sequence a block updater must replay.
    /// <para>按文件顺序排列的传输命令，即块更新器需重放的序列。</para></value>
    public IReadOnlyList<TransferListCommand> Commands { get; }
    /// <summary>Gets the highest destination block boundary. <para>获取最高目标块边界。</para></summary>
    /// <value>Largest exclusive end across all destination ranges of stash/free-excluded commands,
    /// derived at construction; <c>0</c> when no command writes a range.
    /// <para>构造时推导的所有目标区间（不含 stash/free 命令）的最大开区间端点；无写入区间时为 <c>0</c>。</para></value>
    public long TotalBlocks { get; }
    /// <summary>Gets the number of new-data blocks consumed. <para>获取消耗的新数据块数。</para></summary>
    /// <value>Checked sum of block counts across all new commands; zero-run blocks from other kinds are excluded.
    /// <para>所有 new 命令块数的检查和（检查溢出）；不含其他命令类型消耗的块。</para></value>
    public long NewDataBlocks { get; }
}

/// <summary>Parses recovery transfer-list versions 1 through 4. <para>解析 recovery 传输列表版本 1 至 4。</para></summary>
public static class TransferListParser
{
    /// <summary>Parses a transfer-list file. <para>解析传输列表文件。</para></summary>
    /// <param name="path">Path to the transfer-list text file, opened for reading.
    /// <para>待只读打开的传输列表文本文件路径。</para></param>
    /// <returns>The parsed transfer list. <para>解析后的传输列表。</para></returns>
    /// <exception cref="InvalidDataException">The file content is truncated or malformed.
    /// <para>文件内容被截断或格式非法。</para></exception>
    public static TransferList Parse(string path) { using var reader = File.OpenText(path); return Parse(reader); }
    /// <summary>Parses a transfer list from text. <para>从文本解析传输列表。</para></summary>
    /// <param name="reader">Text reader positioned at the version line; consumed to the end and left open.
    /// <para>定位在版本行的文本读取器；会被读取到末尾且保持打开。</para></param>
    /// <returns>The parsed transfer list with commands in file order.
    /// <para>按文件顺序包含命令的解析结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.
    /// <para><paramref name="reader"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">A header line is missing, the version is outside 1 through 4,
    /// or a command line violates the version's field grammar, ranges, hashes or stash coverage.
    /// <para>头部行缺失、版本超出 1 至 4，或命令行违反该版本的字段语法、区间、摘要或暂存覆盖规则。</para></exception>
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