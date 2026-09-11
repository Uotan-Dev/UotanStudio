using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Services.Delivery;

/// <summary>Builds a full unsigned payload and standard update_engine property hashes.</summary>
internal static class DeliveryPayloadBuilder
{
    internal static void Validate(DeliveryPlan plan)
    {
        if (plan.Payload.Version != 2 || plan.Payload.MinorVersion != 0)
            throw new NotSupportedException("完整 Payload 目前支持主版本 2、次版本 0 / Full Payload requires version 2, minor 0.");
        if (plan.Payload.SignatureFile.Length > 0 || plan.Payload.PostinstallFile.Length > 0)
            throw new NotSupportedException("Payload 签名与 Postinstall 尚未实现，不能忽略这些输入 / Signature and Postinstall inputs are not supported yet.");
        if (plan.Container.Count == 0 || plan.Container.Any(f => !DeliveryValidation.IsPartition(f.Partition) || f.SourcePath.Length == 0)
            || plan.Container.Select(f => f.Partition).Distinct(StringComparer.Ordinal).Count() != plan.Container.Count)
            throw new InvalidDataException("Payload 需要名称唯一的实际分区镜像 / Payload requires unique source partitions.");
    }

    internal static DynamicPartitionMetadata? ReadDynamicLayout(DeliveryPlan plan, IReadOnlyDictionary<string, long> lengths)
    {
        if (plan.Payload.DynamicPartitionsFile.Length == 0)
        {
            if (plan.Container.Any(f => f.IncludeInSuper)) throw new InvalidDataException("请生成或选择动态分区配置 / Generate or select dynamic partition configuration.");
            return null;
        }
        var file = new FileInfo(plan.Payload.DynamicPartitionsFile);
        if (!file.Exists || file.Length > 1024 * 1024) throw new InvalidDataException("动态分区配置不存在或超过 1 MiB / Invalid dynamic partition configuration.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file.FullName))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var split = text.IndexOf('=');
            if (split < 1 || !values.TryAdd(text[..split].Trim(), text[(split + 1)..].Trim()))
                throw new InvalidDataException("Invalid or duplicate dynamic partition configuration key.");
        }
        static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new DynamicPartitionMetadata();
        var names = Words(values.GetValueOrDefault("super_partition_groups", ""));
        var allowed = new HashSet<string>(["super_partition_groups", "virtual_ab"], StringComparer.Ordinal);
        var partitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!DeliveryValidation.IsPartition(name) || result.Groups.Any(g => g.Name == name)) throw new InvalidDataException("Invalid dynamic group name.");
            var sizeKey = "super_" + name + "_group_size";
            var listKey = "super_" + name + "_partition_list";
            allowed.Add(sizeKey); allowed.Add(listKey);
            if (!ulong.TryParse(values.GetValueOrDefault(sizeKey), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size == 0)
                throw new InvalidDataException("Invalid dynamic group size: " + name);
            var group = new DynamicPartitionGroup { Name = name, Size = size };
            ulong used = 0;
            foreach (var partition in Words(values.GetValueOrDefault(listKey, "")))
            {
                if (!lengths.TryGetValue(partition, out var length) || !partitions.Add(partition))
                    throw new InvalidDataException("Unknown or duplicate dynamic partition: " + partition);
                used = checked(used + (ulong)length); group.PartitionNames.Add(partition);
            }
            if (used > size || group.PartitionNames.Count == 0) throw new InvalidDataException("Empty or oversized dynamic group: " + name);
            result.Groups.Add(group);
        }
        if (names.Length == 0 || values.Keys.Any(k => !allowed.Contains(k)))
            throw new NotSupportedException("Empty groups or unsupported dynamic partition configuration keys.");
        if (values.TryGetValue("virtual_ab", out var vab))
        {
            if (!bool.TryParse(vab, out var enabled)) throw new InvalidDataException("Invalid virtual_ab value.");
            result.SnapshotEnabled = enabled;
        }
        if (plan.Container.Any(f => f.IncludeInSuper && !partitions.Contains(f.Partition)))
            throw new InvalidDataException("A selected Super member is absent from the dynamic partition configuration.");
        return result;
    }

    internal static string GenerateDynamicLayout(DeliveryPlan plan)
    {
        var selected = plan.Container.Where(f => f.IncludeInSuper).ToArray();
        if (selected.Length == 0 || !DeliveryValidation.IsPartition(plan.Super.Group) || plan.Super.DeviceSize == 0)
            throw new InvalidDataException("请勾选动态分区并填写有效簇名及大小 / Select Super members and a valid group name and size.");
        if (selected.Any(f => !DeliveryValidation.IsPartition(f.Partition))) throw new InvalidDataException("Invalid partition name.");
        return $"super_partition_groups={plan.Super.Group}\nsuper_{plan.Super.Group}_group_size={plan.Super.DeviceSize}\nsuper_{plan.Super.Group}_partition_list={string.Join(' ', selected.Select(f => f.Partition))}\nvirtual_ab={plan.Super.VirtualAb.ToString().ToLowerInvariant()}\n";
    }

    internal static async Task BuildAsync(DeliveryPlan plan, string output, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        Validate(plan);
        await Task.Run(async () =>
        {
            var inputs = new List<Stream>();
            try
            {
                using var builder = new PayloadBuilder();
                var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var file in plan.Container)
                {
                    token.ThrowIfCancellationRequested(); DeliveryValidation.CheckSource(file);
                    var input = MetadataReader.OpenImage(file.SourcePath); inputs.Add(input);
                    if (input.Length == 0 || input.Length % 4096 != 0) throw new InvalidDataException("Payload image must be aligned to 4096 bytes: " + file.Partition);
                    lengths.Add(file.Partition, input.Length); builder.AddPartition(file.Partition, input);
                }
                var layout = ReadDynamicLayout(plan, lengths);
                if (layout is not null) builder.WithDynamicPartitions(layout);
                progress?.Report(new(10, "构建未签名 Payload / Building unsigned Payload"));
                using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                using var tracked = new DeliveryWriteProgressStream(destination, _ => progress?.Report(new(35, "写入 Payload / Writing Payload")), token);
                await builder.BuildAsync(tracked, token).ConfigureAwait(false);
            }
            finally { foreach (var input in inputs) input.Dispose(); }
        }, token).ConfigureAwait(false);
        await WritePropertiesAsync(output, Path.Combine(Path.GetDirectoryName(output)!, "payload_properties.txt"), token).ConfigureAwait(false);
    }

    private static async Task WritePropertiesAsync(string image, string properties, CancellationToken token)
    {
        await using var input = File.OpenRead(image);
        var header = new byte[24]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var size = checked(24L + (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12, 8)));
        input.Position = 0;
        using var metadata = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long remaining = size;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            metadata.AppendData(buffer, 0, count); remaining -= count;
        }
        input.Position = 0;
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        var text = $"FILE_HASH={Convert.ToBase64String(hash)}\nFILE_SIZE={input.Length}\nMETADATA_HASH={Convert.ToBase64String(metadata.GetHashAndReset())}\nMETADATA_SIZE={size}\n";
        await File.WriteAllTextAsync(properties, text, new UTF8Encoding(false), token).ConfigureAwait(false);
    }
}
