using UotanStudio.Services.Firmware;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Resolves logical partition membership from the image's own source project.
/// <para>按镜像自身的源工程解析逻辑分区的归属关系。</para>
/// </summary>
internal static class DeliveryDynamicPartitions
{
    internal static IReadOnlySet<string>? Read(string projectRoot)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var found = false;
        foreach (var kind in new[] { "super", "payload" })
        {
            var settings = SourceContainerMetadataService.Read(projectRoot, kind);
            if (settings is null) continue;
            found = true;
            var partitions = kind == "super" ? settings.PartitionNames.AsEnumerable()
                : DeliveryGroups.FromSource(settings).SelectMany(group => group.Partitions);
            foreach (var name in partitions)
            {
                names.Add(name);
                if (name.EndsWith("_a", StringComparison.Ordinal) || name.EndsWith("_b", StringComparison.Ordinal))
                    names.Add(name[..^2]);
            }
        }
        return found ? names : null;
    }

    internal static bool Contains(IReadOnlySet<string> names, string partition) => names.Contains(partition)
        || ((partition.EndsWith("_a", StringComparison.Ordinal) || partition.EndsWith("_b", StringComparison.Ordinal))
            && names.Contains(partition[..^2]));
}