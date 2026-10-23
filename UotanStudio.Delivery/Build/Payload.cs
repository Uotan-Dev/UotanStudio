using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Delivery;

/// <summary><para>构建完整 Payload，可选使用所选私钥签名，并生成标准 update_engine 属性哈希。</para>
/// Builds a full payload, optionally signed with the selected private key, and standard update_engine property hashes.</summary>
public static class DeliveryPayloadBuilder
{
    /// <summary>
    /// Validates the Payload plan: version, signature, block size, timestamp, compression, postinstall config, and partition uniqueness.
    /// <para>校验 Payload 计划：版本、签名文件、块大小、时间戳、压缩方式、postinstall 配置及分区的唯一性。</para>
    /// </summary>
    public static void Validate(DeliveryPlan plan)
    {
        if (plan.Payload.Version != 2 || plan.Payload.MinorVersion != 0)
            throw new NotSupportedException("完整 Payload 目前支持主版本 2、次版本 0 / Full Payload requires version 2, minor 0.");
        if (plan.Payload.SignatureFile.Length > 0)
            throw new NotSupportedException("旧版预制签名文件不受支持；请使用签名密钥选项。");
        if (plan.Payload.BlockSize is < 512 or > 1048576 || (plan.Payload.BlockSize & (plan.Payload.BlockSize - 1)) != 0
            || plan.Payload.MaxTimestamp < 0 || plan.Payload.Compression is not ("None" or "BZip2" or "Xz")
            || plan.Payload.SecurityPatchLevel.Length > 0 && !DateOnly.TryParseExact(plan.Payload.SecurityPatchLevel, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("Payload 块大小、时间戳、安全补丁日期或压缩方式无效。");
        _ = DeliveryPostinstall.Read(plan.Payload.PostinstallFile, plan.Container.Select(f => f.Partition));
        if (plan.Payload.SigningKeyPath.Length == 0 && plan.Payload.SigningKeyFingerprint.Length > 0)
            throw new InvalidDataException("签名密钥路径缺失 / Signing key path is missing.");
        if (plan.Container.Count == 0 || plan.Container.Any(f => !DeliveryValidation.IsPartition(f.Partition, payload: true) || f.SourcePath.Length == 0)
            || plan.Container.Select(f => f.Partition).Distinct(StringComparer.Ordinal).Count() != plan.Container.Count)
            throw new InvalidDataException("Payload 需要名称唯一的实际分区镜像 / Payload requires unique source partitions.");
    }

    /// <summary>
    /// Reads the dynamic partition config file (super_partition_groups etc.) into DynamicPartitionMetadata; returns null when absent, otherwise errors on rule violations.
    /// <para>读取动态分区配置文件（super_partition_groups 等）为 DynamicPartitionMetadata；缺失或含 Super 成员时按规则返回或报错。</para>
    /// </summary>
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

    /// <summary>
    /// Generates the update_engine dynamic partition configuration text from the plan's selected Super members and groups.
    /// <para>根据计划内选中的动态分区与 Super 分组配置生成 update_engine 的动态分区配置文件文本。</para>
    /// </summary>
    public static string GenerateDynamicLayout(DeliveryPlan plan)
    {
        var selected = plan.Container.Where(f => f.IncludeInSuper).ToArray();
        if (selected.Length == 0 || !DeliveryValidation.IsPartition(plan.Super.Group) || plan.Super.DeviceSize == 0)
            throw new InvalidDataException("请勾选动态分区并填写有效簇名及大小 / Select Super members and a valid group name and size.");
        if (selected.Any(f => !DeliveryValidation.IsPartition(f.Partition))) throw new InvalidDataException("Invalid partition name.");
        if (plan.Super.Groups.Count > 0)
        {
            var groups = DeliveryGroups.Parse(DeliveryGroups.Format(plan.Super.Groups));
            var names = selected.Select(f => f.Partition).ToHashSet(StringComparer.Ordinal);
            if (names.Any(n => !groups.Any(g => g.Partitions.Contains(n)))) throw new InvalidDataException("动态分区缺少原组归属，请编辑分组配置。");
            var text = new StringBuilder("super_partition_groups=" + string.Join(' ', groups.Where(g => g.Partitions.Any(names.Contains)).Select(g => g.Name)) + "\n");
            foreach (var group in groups.Where(g => g.Partitions.Any(names.Contains)))
            {
                if (group.Size == 0) throw new InvalidDataException("Payload 动态组需要明确容量：" + group.Name);
                text.AppendLine($"super_{group.Name}_group_size={group.Size}");
                text.AppendLine($"super_{group.Name}_partition_list={string.Join(' ', group.Partitions.Where(names.Contains))}");
            }
            text.AppendLine("virtual_ab=" + plan.Super.VirtualAb.ToString().ToLowerInvariant());
            return text.ToString();
        }
        return $"super_partition_groups={plan.Super.Group}\nsuper_{plan.Super.Group}_group_size={plan.Super.DeviceSize}\nsuper_{plan.Super.Group}_partition_list={string.Join(' ', selected.Select(f => f.Partition))}\nvirtual_ab={plan.Super.VirtualAb.ToString().ToLowerInvariant()}\n";
    }

    /// <summary>
    /// Builds a full payload to the output file and writes the update_engine property hashes, optionally signed with the
    /// selected private key. The write side is wrapped in a <see cref="HashingWriteStream"/> so FILE_HASH comes straight
    /// off the bytes as they leave the builder, which keeps the freshly written payload.bin from being read in full a
    /// second time; METADATA_HASH needs the metadata region of the finished manifest, so that region alone is re-read
    /// through a bounded pass and cannot be fused into the write stream.
    /// <para>构建完整 Payload 到输出文件，并按 update_engine 约定写出 payload 的属性哈希文件；可选使用所选私钥签名。
    /// 写盘侧套一层 <see cref="HashingWriteStream"/>，FILE_HASH 直接由写出的字节算出，因此不会第二次全量读取刚写完的
    /// payload.bin；METADATA_HASH 需要成品清单里元数据区的那段字节，所以只对元数据区做一次有界读取，无法并入写侧。</para>
    /// </summary>
    internal static async Task BuildAsync(DeliveryPlan plan, string output, IProgress<DeliveryProgress>? progress,
        CancellationToken token, IDeliverySigningKeyProvider? signingKeys = null)
    {
        Validate(plan);
        var fileHash = Array.Empty<byte>();
        long fileSize = 0;
        await Task.Run(async () =>
        {
            var inputs = new List<Stream>();
            try
            {
                using var builder = new PayloadBuilder();
                builder.WithBlockSize(plan.Payload.BlockSize).WithMaxTimestamp(plan.Payload.MaxTimestamp)
                    .WithSecurityPatchLevel(plan.Payload.SecurityPatchLevel);
                foreach (var config in DeliveryPostinstall.Read(plan.Payload.PostinstallFile, plan.Container.Select(f => f.Partition)).Values)
                    builder.WithPostinstall(config);
                using var signingKey = LoadSigningKey(plan.Payload, signingKeys);
                if (signingKey is not null) builder.WithSigningKey(signingKey);
                var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
                progress?.Report(new(0, "Preparing Payload", Stage: "Delivery_ProgressPayloadPrepare", Items: (0, plan.Container.Count)));
                foreach (var file in plan.Container)
                {
                    token.ThrowIfCancellationRequested(); DeliveryValidation.CheckSource(file);
                    var input = MetadataReader.OpenImage(file.SourcePath); inputs.Add(input);
                    if (input.Length == 0 || input.Length % plan.Payload.BlockSize != 0) throw new InvalidDataException("Payload image must be block-aligned: " + file.Partition);
                    lengths.Add(file.Partition, input.Length); builder.AddPartitionCompressed(file.Partition, input, Enum.Parse<CompressionType>(plan.Payload.Compression));
                    progress?.Report(new(10.0 * lengths.Count / plan.Container.Count, "Preparing Payload",
                        Stage: "Delivery_ProgressPayloadPrepare", Items: (lengths.Count, plan.Container.Count), Detail: file.Partition));
                }
                var layout = ReadDynamicLayout(plan, lengths);
                if (layout is not null) builder.WithDynamicPartitions(layout);
                progress?.Report(new(10, "Writing Payload", (0, 0), "Delivery_ProgressPayloadWrite"));
                using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                using var hashed = new HashingWriteStream(destination, leaveOpen: true);
                using var tracked = new DeliveryWriteProgressStream(hashed, written => progress?.Report(new(35, "Writing Payload",
                    (written, 0), "Delivery_ProgressPayloadWrite")), token);
                await builder.BuildAsync(tracked, token).ConfigureAwait(false);
                progress?.Report(new(45, "Writing Payload", (tracked.Length, tracked.Length), "Delivery_ProgressPayloadWrite"));
                fileHash = hashed.GetHashAndReset();
                fileSize = hashed.BytesWritten;
            }
            finally { foreach (var input in inputs) input.Dispose(); }
        }, token).ConfigureAwait(false);
        await WritePropertiesAsync(output, Path.Combine(Path.GetDirectoryName(output)!, "payload_properties.txt"), fileHash, fileSize, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads and validates the signing key per the Payload options: RSA 2048/4096-bit, matching the selected fingerprint.
    /// <para>按 Payload 选项加载并校验签名密钥：要求 RSA 2048/4096 位，且指纹与所选一致。</para>
    /// </summary>
    internal static RSA? LoadSigningKey(DeliveryPayloadOptions options, IDeliverySigningKeyProvider? signingKeys = null)
    {
        if (options.SigningKeyPath.Length == 0) return null;
        if (!File.Exists(options.SigningKeyPath)) throw new FileNotFoundException("所选签名密钥不存在，请重新选择 / Selected signing key is missing; select it again.", options.SigningKeyPath);
        var key = DeliverySigning.Load(signingKeys, options.SigningKeyPath);
        try
        {
            if (key.KeySize is not (2048 or 4096)) throw new InvalidDataException("Payload 签名需要 RSA 2048 或 4096 / Payload requires RSA 2048 or 4096.");
            var fingerprint = "SHA256:" + Convert.ToHexString(Sha256.ComputeHash(key.ExportSubjectPublicKeyInfo()));
            if (options.SigningKeyFingerprint.Length > 0 && fingerprint != options.SigningKeyFingerprint)
                throw new InvalidDataException("所选密钥已被替换，请重新选择 / The selected key has changed; select it again.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    /// <summary>
    /// Writes the four update_engine properties: FILE_HASH and FILE_SIZE come from the whole-file digest folded out of
    /// the write pass, and only METADATA_HASH re-reads the metadata region (the 24-byte header plus the metadata length it
    /// declares), leaving every other byte unread a second time. The supplied digest and length must describe the file as
    /// it now stands on disk; a mismatch means the written bytes and the published file disagree, and the delivery aborts.
    /// <para>写出 update_engine 的四个属性：FILE_HASH 与 FILE_SIZE 取自写入 payload.bin 时顺带算出的整份摘要，
    /// 只有 METADATA_HASH 需要再读一次元数据区（24 字节头加上头部声明的元数据长度），其余字节不做二次读取。
    /// 传入的摘要与长度必须描述磁盘上已经写好的那份文件；一旦对不上，说明写出与落盘不一致，交付直接中止。</para>
    /// </summary>
    private static async Task WritePropertiesAsync(string image, string properties, byte[] fileHash, long fileSize, CancellationToken token)
    {
        await using var input = File.OpenRead(image);
        if (fileHash.Length != 32 || input.Length != fileSize)
            throw new IOException("Payload digest does not describe the finished image.");
        var header = new byte[24]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var size = checked(24L + (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12, 8)));
        input.Position = 0;
        using var metadata = Sha256.CreateAccumulator();
        var buffer = new byte[1024 * 1024];
        var remaining = size;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            metadata.Append(buffer.AsSpan(0, count));
            remaining -= count;
        }

        var text = $"FILE_HASH={Convert.ToBase64String(fileHash)}\nFILE_SIZE={fileSize}\nMETADATA_HASH={Convert.ToBase64String(metadata.GetHashAndReset())}\nMETADATA_SIZE={size}\n";
        await File.WriteAllTextAsync(properties, text, new UTF8Encoding(false), token).ConfigureAwait(false);
    }
}