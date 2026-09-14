using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Services.Delivery;

/// <summary>Explicit group capacity and membership, shared by Super and Payload.</summary>
internal sealed record DeliveryGroup
{
    public string Name { get; init; } = "";
    public ulong Size { get; init; }
    public List<string> Partitions { get; init; } = [];
}

internal static class DeliveryGroups
{
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

    internal static string Format(IEnumerable<DeliveryGroup> groups) => string.Join('\n', groups.Select(g =>
        $"{g.Name} {g.Size.ToString(CultureInfo.InvariantCulture)} {string.Join(' ', g.Partitions)}"));

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