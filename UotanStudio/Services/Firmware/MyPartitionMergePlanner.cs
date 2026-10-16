using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Planning logic for a new project's my-partition merge: partition-name derivation, merge applicability
/// and discovery of the extracted system and my_* trees.
/// <para>新建项目 my 分区合并的规划逻辑：分区名推导、合并适用性判断，以及解包出的 system 与 my_* 树的发现。</para>
/// </summary>
internal static class MyPartitionMergePlanner
{
    private const string SystemPartitionName = "system";
    private const string MyPartitionPrefix = "my_";

    /// <summary>
    /// Derives a partition name from a new-project entry name, using container partition entries verbatim
    /// and stripping the archive path and extension from file entries.
    /// <para>从新建项目条目名推导分区名：容器分区条目直接沿用，文件条目去除归档路径与扩展名。</para>
    /// </summary>
    /// <param name="entryName">Entry identity: partition name, ZIP inner path, or file name. <para>条目标识：分区名、ZIP 内部路径或文件名。</para></param>
    /// <param name="containerPartitionEntry"><see langword="true"/> when the entry itself is a container partition name. <para>该条目本身是容器分区名时为 <see langword="true"/>。</para></param>
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
    /// Decides whether the my-partition merge applies: both unpack options are enabled and the selected
    /// partition names pair a <c>system</c> partition with a same-slot <c>my_</c> partition.
    /// <para>判断 my 分区合并是否适用：两个解包选项都已启用，且勾选的分区名能把 <c>system</c> 分区与同槽位的 <c>my_</c> 分区配对。</para>
    /// </summary>
    /// <param name="unpackImages">Whether images are unpacked. <para>是否解包为镜像。</para></param>
    /// <param name="unpackFully">Whether images are unpacked into trees. <para>是否完全解包为目录树。</para></param>
    /// <param name="partitionNames">Partition names derived from selected entries. <para>勾选条目推导出的分区名。</para></param>
    /// <returns><see langword="true"/> when the merge applies. <para>合并适用时为 <see langword="true"/>。</para></returns>
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
    /// Discovers the extracted system tree and the same-slot <c>my_*</c> trees among the direct child
    /// directories of a sub-project root, taking the first system tree by name when several exist.
    /// <para>在子项目根的直接子目录中发现解包出的 system 树与同槽位的 <c>my_*</c> 树；存在多个 system 树时按名称取最先者。</para>
    /// </summary>
    /// <param name="projectRoot">Sub-project root directory. <para>子项目根目录。</para></param>
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
        var chosenPath = orderedSystems.Length > 0 ? orderedSystems[0].Path : null;
        var chosenSlot = orderedSystems.Length > 0 ? orderedSystems[0].Slot : null;
        List<string> myTrees = [.. myCandidates
            .Where(candidate => SlotsMerge(candidate.Slot, chosenSlot))
            .Select(candidate => candidate.Path)];
        myTrees.Sort(StringComparer.OrdinalIgnoreCase);
        return (chosenPath, myTrees);
    }

    /// <summary>
    /// Decides whether a my-partition slot merges into a system slot, matching every slot when either side
    /// carries none.
    /// <para>判定 my 分区槽位能否合并进 system 槽位；任一侧没有槽位时与所有槽位匹配。</para>
    /// </summary>
    /// <param name="mySlot">Slot letter of the my-partition name, null when unsuffixed. <para>my 分区名的槽位字母，无后缀时为 null。</para></param>
    /// <param name="systemSlot">Slot letter of the system name, null when unsuffixed. <para>system 名的槽位字母，无后缀时为 null。</para></param>
    /// <returns><see langword="true"/> when the two slots may merge. <para>两个槽位可以合并时为 <see langword="true"/>。</para></returns>
    private static bool SlotsMerge(char? mySlot, char? systemSlot)
        => mySlot is null || systemSlot is null || mySlot == systemSlot;

    /// <summary>
    /// Reads the trailing slot letter of a partition name.
    /// <para>读取分区名结尾的槽位字母。</para>
    /// </summary>
    /// <param name="name">The partition name to inspect. <para>待检查的分区名。</para></param>
    /// <returns>The lowercased slot letter, or <see langword="null"/> when the name has no suffix. <para>小写槽位字母，名称无后缀时为 <see langword="null"/>。</para></returns>
    private static char? SlotSuffix(string name)
    {
        var stripped = RepackMetadata.StripSlotSuffix(name);
        return name.Length == stripped.Length ? null : char.ToLowerInvariant(name[^1]);
    }

    /// <summary>
    /// Lists the image files of one partition inside <c>images/</c>, matching every file whose name without
    /// extension equals the partition name.
    /// <para>列出 <c>images/</c> 内属于某个分区的镜像文件：去扩展名后与分区名相等的每个文件。</para>
    /// </summary>
    /// <param name="imagesRoot">The sub-project <c>images/</c> directory. <para>子项目 <c>images/</c> 目录。</para></param>
    /// <param name="partition">Partition name. <para>分区名。</para></param>
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