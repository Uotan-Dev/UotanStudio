using System.Globalization;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Services.Delivery;

/// <summary><para>显式的分组容量与成员关系，Super 与 Payload 共用。</para>
/// Explicit group capacity and membership, shared by Super and Payload.</summary>
internal sealed record DeliveryGroup
{
    public string Name { get; init; } = "";
    public ulong Size { get; init; }
    public List<string> Partitions { get; init; } = [];
}

/// <summary><para>解析、格式化并从源容器元数据还原显示的动态分区组配置。</para>
/// Parses, formats, and restores display dynamic partition group configuration from source container metadata.</summary>
internal static class DeliveryGroups
{
    /// <summary>
    /// <para>把逐行“组名 容量字节 分区1 分区2 …”文本解析为分组列表；校验组名、容量与分区是否合法且不重复。</para>
    /// Parses per-line "group size partitions…" text into a list of groups, validating names, sizes, and partitions for uniqueness.
    /// </summary>
    internal static List<DeliveryGroup> Parse(string text)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("分组配置超过 1 MiB。");
        var result = new List<DeliveryGroup>();
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !DeliveryValidation.IsPartition(fields[0])
                || !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || result.Any(g => g.Name == fields[0])) throw new InvalidDataException("分组格式：组名 容量字节 分区1 分区2 …");
            var partitions = fields.Skip(2).ToList();
            if (partitions.Any(p => !DeliveryValidation.IsPartition(p) || !members.Add(p)))
                throw new InvalidDataException("分组包含无效或重复分区。");
            result.Add(new() { Name = fields[0], Size = size, Partitions = partitions });
        }
        return result;
    }

    /// <summary><para>把分组列表格式化为逐行“组名 容量字节 分区…”文本。</para>
    /// Formats a group list into per-line "group size partitions…" text.</summary>
    internal static string Format(IEnumerable<DeliveryGroup> groups) => string.Join('\n', groups.Select(g =>
        $"{g.Name} {g.Size.ToString(CultureInfo.InvariantCulture)} {string.Join(' ', g.Partitions)}"));

    /// <summary>
    /// <para>从源容器元数据重建分组：Payload 来源解析动态分区配置，Super 来源解析 LP 元数据中的分组与分区归属。</para>
    /// Rebuilds groups from source container metadata: Payload sources parse the dynamic partition config, Super sources parse LP metadata groups and memberships.
    /// </summary>
    internal static List<DeliveryGroup> FromSource(DeliverySourceContainerSettings settings)
    {
        if (settings.Kind == "payload")
        {
            if (settings.OriginalStructures.Length == 0) return [];
            return DynamicPartitionMetadata.Parser.ParseFrom(settings.OriginalStructures[0]).Groups.Select(g =>
                new DeliveryGroup { Name = g.Name, Size = g.Size, Partitions = g.PartitionNames.ToList() }).ToList();
        }
        if (settings.OriginalStructures.Length < 2) return [];
        var header = LpMetadataHeader.FromBytes(settings.OriginalStructures[1]);
        if (header.BlockDevices.NumEntries != 1)
            throw new NotSupportedException("原 Super 使用多个物理设备；当前单文件构建器无法保留该布局，请使用对应设备的专用构建流程。");
        var count = checked(2L + header.Partitions.NumEntries + header.Extents.NumEntries + header.Groups.NumEntries + header.BlockDevices.NumEntries);
        if (count != settings.OriginalStructures.Length) throw new InvalidDataException("Original Super metadata is incomplete.");
        var offset = checked((int)(2 + header.Partitions.NumEntries + header.Extents.NumEntries));
        var groups = Enumerable.Range(offset, (int)header.Groups.NumEntries)
            .Select(i => LpMetadataPartitionGroup.FromBytes(settings.OriginalStructures[i]))
            .Select(g => new DeliveryGroup { Name = g.NameString, Size = g.MaximumSize }).ToList();
        foreach (var bytes in settings.OriginalStructures.Skip(2).Take((int)header.Partitions.NumEntries))
        {
            var partition = LpMetadataPartition.FromBytes(bytes);
            if (partition.GroupIndex >= groups.Count) throw new InvalidDataException("Original Super group index is invalid.");
            groups[(int)partition.GroupIndex].Partitions.Add(partition.NameString);
        }
        return groups;
    }
}