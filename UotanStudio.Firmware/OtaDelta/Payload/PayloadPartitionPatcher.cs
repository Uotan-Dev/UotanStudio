using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>Structural edits applied to an existing payload, mirroring the avbroot-extend <c>ota patch</c> verbs.
/// <para>对已有载荷施加的结构性改动，对应 avbroot-extend 的 <c>ota patch</c> 动作。</para></summary>
internal sealed record PayloadPartitionPatch
{
    /// <summary>Partition names to drop from the manifest. <para>要从清单移除的分区名。</para></summary>
    public IReadOnlyList<string> DeletePartitionNames { get; init; } = [];

    /// <summary>Existing partition names whose whole content is swapped for a new image file. <para>整体换用新镜像文件的已有分区名。</para></summary>
    public IReadOnlyDictionary<string, string> ReplaceImagePaths { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>New partitions injected as full REPLACE images. <para>以完整 REPLACE 镜像注入的新分区。</para></summary>
    public IReadOnlyList<(string PartitionName, string ImagePath)> AddedPartitions { get; init; } = [];

    /// <summary>Whether a deleted partition is also purged from every dynamic group's name list.
    /// <para>删除分区时是否同时从每个动态分组的名单中清除其名。</para></summary>
    public bool PurgeDeletedPartitionFromGroups { get; init; } = true;

    /// <summary>RSA key that signs the rebuilt payload; <see langword="null"/> leaves it unsigned.
    /// <para>对重建载荷签名的 RSA 密钥；<see langword="null"/> 表示不签名。</para></summary>
    public RSA? SigningKey { get; init; }
}

/// <summary>Rebuilds an A/B payload after adding, deleting or replacing partitions.
/// <para>在增删或替换分区后重建 A/B 载荷。</para></summary>
/// <remarks>
/// Every retained partition is extracted to its full target image and re-emitted as REPLACE operations, so the result is a
/// self-contained full payload rather than a delta. Postinstall flags are carried from the original manifest entries. Source-image
/// dictionaries are required only when a retained partition uses SOURCE operations.
/// <para>每个保留分区都会导出为完整目标镜像并重新以 REPLACE 操作输出，因此结果是自包含的完整载荷而非增量。postinstall 标志继承自原清单条目。
/// 仅当保留分区使用 SOURCE 操作时才需要源镜像字典。</para>
/// </remarks>
internal static class PayloadPartitionPatcher
{
    /// <summary>Parses <paramref name="source"/>, applies <paramref name="patch"/>, and writes a rebuilt payload to <paramref name="destination"/>.
    /// <para>解析 <paramref name="source"/>，应用 <paramref name="patch"/>，并把重建后的载荷写入 <paramref name="destination"/>。</para></summary>
    internal static async Task PatchAsync(Stream source, Stream destination, PayloadPartitionPatch patch,
        IReadOnlyDictionary<string, string>? sourceImages = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(patch);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The source payload stream must be readable and seekable.", nameof(source));
        }
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination payload stream must be writable.", nameof(destination));
        }
        foreach (var (_, imagePath) in patch.AddedPartitions)
        {
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"Added partition image not found: {imagePath}", imagePath);
            }
        }
        foreach (var imagePath in patch.ReplaceImagePaths.Values)
        {
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"Replacement partition image not found: {imagePath}", imagePath);
            }
        }

        var metadata = PayloadParser.GetPayloadMetadata(source);
        var deletes = new HashSet<string>(patch.DeletePartitionNames, StringComparer.Ordinal);
        var replacements = patch.ReplaceImagePaths;
        var added = new HashSet<string>(patch.AddedPartitions.Select(entry => entry.PartitionName), StringComparer.Ordinal);
        ValidateNoConflictingNames(deletes, replacements, added, metadata);

        var temporary = Path.Combine(Path.GetTempPath(), "uotan-payload-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        using var builder = new PayloadBuilder()
            .WithBlockSize(metadata.BlockSize)
            .WithMaxTimestamp(metadata.MaxTimestamp);
        if (metadata.Manifest.MinorVersion != 0)
        {
            _ = builder.WithMinorVersion(metadata.Manifest.MinorVersion);
        }
        if (metadata.Manifest.SecurityPatchLevel.Length > 0)
        {
            _ = builder.WithSecurityPatchLevel(metadata.Manifest.SecurityPatchLevel);
        }
        try
        {
            var dynamicMetadata = PrepareDynamicMetadata(metadata, deletes, patch);
            if (dynamicMetadata.Groups.Count > 0)
            {
                _ = builder.WithDynamicPartitions(dynamicMetadata);
            }
            if (patch.SigningKey is { } key)
            {
                _ = builder.WithSigningKey(key);
            }

            var extracted = new Dictionary<string, string>(StringComparer.Ordinal);
            var extractor = new PayloadExtractor();
            foreach (var partition in metadata.Manifest.Partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = partition.PartitionName;
                if (string.IsNullOrEmpty(name) || deletes.Contains(name) || replacements.ContainsKey(name))
                {
                    continue;
                }
                if (added.Contains(name))
                {
                    throw new PayloadGenerationException($"Duplicate payload partition name: {name}.");
                }
                var imagePath = Path.Combine(temporary, name + ".img");
                await extractor.ExtractPartitionAsync(partition, imagePath, source, metadata.BaseOffset, metadata.BlockSize,
                    cancellationToken, sourceImages).ConfigureAwait(false);
                extracted.Add(name, imagePath);
                if (partition.RunPostinstall == true || !string.IsNullOrEmpty(partition.PostinstallPath)
                    || !string.IsNullOrEmpty(partition.FilesystemType) || partition.PostinstallOptional == true)
                {
                    _ = builder.WithPostinstall(partition);
                }
            }

            foreach (var (name, imagePath) in extracted)
            {
                _ = builder.AddPartition(name, imagePath);
            }
            foreach (var (name, imagePath) in replacements)
            {
                _ = builder.AddPartition(name, imagePath);
            }
            foreach (var (name, imagePath) in patch.AddedPartitions)
            {
                _ = builder.AddPartition(name, imagePath);
            }

            await builder.BuildAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static DynamicPartitionMetadata PrepareDynamicMetadata(PayloadMetadata metadata, HashSet<string> deletes,
        PayloadPartitionPatch patch)
    {
        var dynamicMetadata = metadata.DynamicPartitionMetadata?.Clone() ?? new DynamicPartitionMetadata();
        if (!patch.PurgeDeletedPartitionFromGroups || deletes.Count == 0)
        {
            return dynamicMetadata;
        }
        foreach (var group in dynamicMetadata.Groups)
        {
            for (var index = group.PartitionNames.Count - 1; index >= 0; index--)
            {
                if (deletes.Contains(group.PartitionNames[index]))
                {
                    group.PartitionNames.RemoveAt(index);
                }
            }
        }
        return dynamicMetadata;
    }

    private static void ValidateNoConflictingNames(HashSet<string> deletes, IReadOnlyDictionary<string, string> replacements,
        HashSet<string> added, PayloadMetadata metadata)
    {
        foreach (var overlap in deletes.Intersect(replacements.Keys, StringComparer.Ordinal))
        {
            throw new PayloadGenerationException($"Partition '{overlap}' is both deleted and replaced.");
        }
        var existing = metadata.Manifest.Partitions.Select(static p => p.PartitionName).ToHashSet(StringComparer.Ordinal);
        foreach (var name in replacements.Keys.Where(name => !existing.Contains(name)))
        {
            throw new PayloadGenerationException($"Replacement target '{name}' is not present in the source payload.");
        }
        foreach (var name in added.Where(name => existing.Contains(name) || deletes.Contains(name)))
        {
            throw new PayloadGenerationException($"Added partition '{name}' conflicts with an existing or deleted name.");
        }
    }
}