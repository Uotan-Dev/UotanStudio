using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Shared planning logic for the new-project my-partition merge option: derives partition names from
/// selected entries, decides whether the option can be offered, and locates the extracted system and
/// my_* trees plus their image files inside a sub-project layout.
/// <para>新建项目 my 分区合并选项的共用规划逻辑：从勾选条目推导分区名、判断选项是否可提供，
/// 并在子项目布局中定位解包出的 system 与 my_* 树及其镜像文件。</para>
/// </summary>
internal static class MyPartitionMergePlanner
{
    private const string SystemPartitionName = "system";
    private const string MyPartitionPrefix = "my_";

    /// <summary>
    /// Derives a partition name from a new-project entry name. Container partition entries
    /// (super logical partitions and payload partitions) use the name verbatim; file entries strip the
    /// archive path and extension. Names with characters outside <c>[A-Za-z0-9_-]</c> yield <see langword="null"/>.
    /// <para>从新建项目条目名推导分区名。容器分区条目（super 逻辑分区与 payload 分区）直接使用名称；
    /// 文件条目去除归档路径与扩展名。含 <c>[A-Za-z0-9_-]</c> 之外字符的名称返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="entryName"><para>条目标识：分区名、ZIP 内部路径或文件名。</para>Entry identity: partition name, ZIP inner path, or file name.</param>
    /// <param name="containerPartitionEntry">
    /// <para><see langword="true"/> 表示该条目本身是容器分区名。</para>
    /// <see langword="true"/> when the entry itself is a container partition name.</param>
    /// <returns>
    /// The derived partition name, or <see langword="null"/> when the name is unusable.
    /// <para>推导出的分区名；名称不可用时返回 <see langword="null"/>。</para>
    /// </returns>
    public static string? DerivePartitionName(string entryName, bool containerPartitionEntry)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        var name = containerPartitionEntry
            ? entryName
            : Path.GetFileNameWithoutExtension(entryName.Replace('\\', '/'));
        name = name.Trim();
        if (name.Length is 0 or > 64) return null;
        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-') return null;
        }
        return name;
    }

    /// <summary>
    /// Indicates whether the merge option can be offered: both unpack options are checked and the selected
    /// partition names pair one <c>system</c> partition with at least one <c>my_</c> prefixed partition of the
    /// same slot (case-insensitive). A slot suffix is a partition-table artifact, so <c>system_a</c> names the
    /// system partition and merges only with <c>my_*_a</c> or an unsuffixed <c>my_*</c>; a name carrying a
    /// suffix the system partition does not have stays out of the merge.
    /// <para>指示是否可以提供合并选项：两个解包选项都勾选，且勾选的分区名能把 <c>system</c> 分区与至少一个
    /// 同槽位的 <c>my_</c> 前缀分配对（不区分大小写）。槽位后缀只是分区表的产物，因此 <c>system_a</c> 仍是
    /// system 分区，且只与 <c>my_*_a</c> 或无后缀的 <c>my_*</c> 配对合并；带其它槽位后缀的名称不参与合并。</para>
    /// </summary>
    /// <param name="unpackImages"><para>是否解包为镜像。</para>Whether images are unpacked.</param>
    /// <param name="unpackFully"><para>是否完全解包为目录树。</para>Whether images are unpacked into trees.</param>
    /// <param name="partitionNames"><para>勾选条目推导出的分区名。</para>Partition names derived from selected entries.</param>
    /// <returns><see langword="true"/> when the option may be shown. <para>可以显示选项时为 <see langword="true"/>。</para></returns>
    public static bool ShouldOffer(bool unpackImages, bool unpackFully, IEnumerable<string> partitionNames)
    {
        if (!unpackImages || !unpackFully) return false;
        List<char?> systemSlots = [];
        List<char?> mySlots = [];
        foreach (var name in partitionNames)
        {
            if (string.Equals(RepackMetadata.StripSlotSuffix(name), SystemPartitionName, StringComparison.OrdinalIgnoreCase))
                systemSlots.Add(SlotSuffix(name));
            else if (name.StartsWith(MyPartitionPrefix, StringComparison.OrdinalIgnoreCase))
                mySlots.Add(SlotSuffix(name));
        }

        return mySlots.Any(mySlot => systemSlots.Any(systemSlot => SlotsMerge(mySlot, systemSlot)));
    }

    /// <summary>
    /// Discovers the extracted system tree and the extracted <c>my_*</c> trees among the direct child
    /// directories of a sub-project root. The trees live beside <c>images/</c> because files under
    /// <c>images/</c> unpack to the sub-project root. A slot-suffixed <c>system_a</c> directory is the system
    /// tree, and only <c>my_*</c> trees sharing that slot — or carrying no slot at all — are returned; trees of
    /// another slot belong to another OS instance and stay untouched. With several system trees unpacked,
    /// the one sorting first by name is the merge target.
    /// <para>在子项目根的直接子目录中发现解包出的 system 树与 <c>my_*</c> 树。由于 <c>images/</c> 下的
    /// 文件解包到子项目根目录，这些树与 <c>images/</c> 同级。带槽位后缀的 <c>system_a</c> 目录就是 system 树，
    /// 返回的 <c>my_*</c> 树只限同槽位或本身不带槽位后缀者；其它槽位的树属于另一套系统实例，保持原样。
    /// 解包出多个 system 树时，按名称排序最先者作为合并目标。</para>
    /// </summary>
    /// <param name="projectRoot"><para>子项目根目录。</para>Sub-project root directory.</param>
    /// <returns>
    /// The system tree path (<see langword="null"/> when absent) and the my-partition trees sorted by name.
    /// <para>system 树路径（缺失时为 <see langword="null"/>）与按名称排序的 my 分区树列表。</para>
    /// </returns>
    public static (string? SystemTree, IReadOnlyList<string> MyTrees) DiscoverTrees(string projectRoot)
    {
        List<(string Path, char? Slot)> systemCandidates = [];
        List<(string Path, char? Slot)> myCandidates = [];
        foreach (var directory in Directory.EnumerateDirectories(projectRoot))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (string.Equals(RepackMetadata.StripSlotSuffix(name), SystemPartitionName, StringComparison.OrdinalIgnoreCase))
                systemCandidates.Add((directory, SlotSuffix(name)));
            else if (name.StartsWith(MyPartitionPrefix, StringComparison.OrdinalIgnoreCase))
                myCandidates.Add((directory, SlotSuffix(name)));
        }

        var orderedSystems = systemCandidates
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string? chosenPath = orderedSystems.Length > 0 ? orderedSystems[0].Path : null;
        char? chosenSlot = orderedSystems.Length > 0 ? orderedSystems[0].Slot : null;
        List<string> myTrees = [.. myCandidates
            .Where(candidate => SlotsMerge(candidate.Slot, chosenSlot))
            .Select(candidate => candidate.Path)];
        myTrees.Sort(StringComparer.OrdinalIgnoreCase);
        return (chosenPath, myTrees);
    }

    /// <summary>
    /// Decides whether a my-partition slot merges into a system slot; an absent slot on either side matches
    /// every slot, because a single-slot layout and a shared my_* partition both carry no suffix.
    /// <para>判定 my 分区槽位能否合并进 system 槽位；任一侧没有槽位时与所有槽位匹配，
    /// 因为单槽布局与共用的 <c>my_*</c> 分区都不带后缀。</para>
    /// </summary>
    private static bool SlotsMerge(char? mySlot, char? systemSlot)
        => mySlot is null || systemSlot is null || mySlot == systemSlot;

    /// <summary>
    /// Reads the trailing slot letter, or <see langword="null"/> when the name carries no slot suffix.
    /// <para>读取结尾的槽位字母；名称没有槽位后缀时返回 <see langword="null"/>。</para>
    /// </summary>
    private static char? SlotSuffix(string name)
    {
        var stripped = RepackMetadata.StripSlotSuffix(name);
        return name.Length == stripped.Length ? null : char.ToLowerInvariant(name[^1]);
    }

    /// <summary>
    /// Lists the image files of one partition inside <c>images/</c>: every file whose name without
    /// extension equals the partition name, e.g. <c>my_product.img</c> or <c>my_product.br</c>.
    /// <para>列出 <c>images/</c> 内属于某个分区的镜像文件：去扩展名后与分区名相等的每个文件，
    /// 例如 <c>my_product.img</c> 或 <c>my_product.br</c>。</para>
    /// </summary>
    /// <param name="imagesRoot"><para>子项目 <c>images/</c> 目录。</para>The sub-project <c>images/</c> directory.</param>
    /// <param name="partition"><para>分区名。</para>Partition name.</param>
    /// <returns>
    /// The matching file paths; empty when the directory is missing.
    /// <para>匹配的文件路径；目录缺失时为空。</para>
    /// </returns>
    public static IReadOnlyList<string> FindPartitionImageFiles(string imagesRoot, string partition)
    {
        if (!Directory.Exists(imagesRoot)) return [];
        List<string> files = [];
        foreach (var file in Directory.EnumerateFiles(imagesRoot))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), partition, StringComparison.OrdinalIgnoreCase))
                files.Add(file);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}