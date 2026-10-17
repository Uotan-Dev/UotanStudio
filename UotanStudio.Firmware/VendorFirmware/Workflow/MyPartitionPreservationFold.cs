using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.VendorFirmware.Workflow;

/// <summary>
/// Folds an extracted my-partition preservation payload into the system preservation manifest: every recorded
/// inode, projection entry, image-path alias, fs_config rule and SELinux label is rekeyed from the partition
/// root to its merged location, so a preserved rebuild of the merged system tree keeps the original ownership,
/// permissions, SELinux labels and symbolic links instead of falling back to donor inheritance. The system
/// manifest's configuration digest is re-sealed after the fold.
/// <para>把解包 my 分区的保留负载折叠进 system 保留清单：每条记录的 inode、镜像路径别名、fs_config
/// 规则与 SELinux 标签都从分区根改键到合并后的位置，使合并后 system 树的受保留重建保住原有属主、权限、
/// SELinux 标签与符号链接，而不是退回 donor 继承。折叠后重封 system 清单的配置摘要。</para>
/// </summary>
internal static class MyPartitionPreservationFold
{
    private const int MaximumEntries = 100000;

    /// <summary>
    /// Runs the fold described by <see cref="MyPartitionPreservationFold"/>.
    /// <para>执行 <see cref="MyPartitionPreservationFold"/> 所描述的折叠。</para>
    /// </summary>
    /// <param name="myMetadataDirectory">
    /// The merged partition's consolidated metadata directory holding its <c>metadata.json</c>.
    /// <para>被合并分区的统一元数据目录，含其 <c>metadata.json</c>。</para>
    /// </param>
    /// <param name="systemMetadataDirectory">
    /// The system partition's consolidated metadata directory receiving the fold.
    /// <para>接收折叠的 system 分区统一元数据目录。</para>
    /// </param>
    /// <param name="partition">The merged partition's full name (with slot suffix). <para>被合并分区的完整名称（含槽位后缀）。</para></param>
    /// <param name="systemPartitionName">The system partition's own name. <para>system 分区自身的名称。</para></param>
    /// <param name="rekeyPrefix">
    /// Merged location inside the system image, e.g. <c>system/my_product</c>.
    /// <para>system 镜像内的合并位置，例如 <c>system/my_product</c>。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the fold; no manifest is changed on cancellation. <para>取消折叠；取消时不改动任何清单。</para></param>
    /// <returns>
    /// Whether both manifests existed and the fold was applied, plus the folded inode entry count.
    /// <para>两份清单都存在且折叠已应用时为 <see langword="true"/>，并返回折叠的 inode 条目数。</para>
    /// </returns>
    /// <exception cref="FileSystemCorruptedException">A manifest is malformed or the fold exceeds the entry limit.
    /// <para>清单格式错误或折叠超出条目上限。</para></exception>
    /// <exception cref="IOException">Saving the folded manifest fails. <para>保存折叠后的清单失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static async Task<(bool Folded, int Entries)> FoldAsync(string myMetadataDirectory,
        string systemMetadataDirectory, string partition, string systemPartitionName, string rekeyPrefix,
        CancellationToken cancellationToken)
    {
        var sourceMetadata = RepackMetadata.ReadFromAbsolute(Path.Combine(myMetadataDirectory, RepackMetadata.FileName));
        var systemMetadata = RepackMetadata.ReadFromAbsolute(Path.Combine(systemMetadataDirectory, RepackMetadata.FileName));
        if (sourceMetadata?.FileSystemPreservation is not JsonObject sourceNode
            || systemMetadata?.FileSystemPreservation is not JsonObject systemNode)
        {
            return (false, 0);
        }
        var source = sourceNode.Deserialize(FileSystemPreservationJsonContext.Default.FileSystemPreservationData);
        var system = systemNode.Deserialize(FileSystemPreservationJsonContext.Default.FileSystemPreservationData);
        if (source is null || system is null
            || source.Partition.Length > 0 && source.Partition != partition
            || system.Partition.Length > 0 && system.Partition != systemPartitionName)
        {
            return (false, 0);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var prefix = FileSystemEditPlan.Normalize(rekeyPrefix);
        string Rekey(string path) => path.Length == 0 ? prefix : prefix + "/" + path;
        static bool Skip(string path) => path.Length > 0
            && path.Split('/')[0].Equals("lost+found", StringComparison.OrdinalIgnoreCase);

        var rekeyedEntries = new Dictionary<string, PreservedInodeEntry>(StringComparer.Ordinal);
        foreach (var entry in source.ParsedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = FileSystemEditPlan.Normalize(entry.Path);
            if (Skip(normalized)) continue;
            rekeyedEntries[Rekey(normalized)] = new PreservedInodeEntry
            {
                Path = Rekey(normalized),
                Size = entry.Size,
                Inode = entry.Inode,
                DeviceId = entry.DeviceId,
                Flags = entry.Flags,
                Mode = entry.Mode,
                Uid = entry.Uid,
                Gid = entry.Gid,
                SymlinkTarget = entry.SymlinkTarget,
                Metadata = entry.Metadata,
                Xattrs = entry.Xattrs?.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal)
            };
        }
        var stalePaths = rekeyedEntries.Keys.ToHashSet(StringComparer.Ordinal);
        var kept = system.ParsedEntries.Where(entry => !stalePaths.Contains(FileSystemEditPlan.Normalize(entry.Path))).ToList();
        kept.AddRange(rekeyedEntries.Values);
        system.ParsedEntries = kept;

        FoldTable(source.ImagePaths, system.ImagePaths, Rekey, Skip);
        FoldTable(source.FsConfig, system.FsConfig, Rekey, Skip);
        FoldTable(source.FileContexts, system.FileContexts, Rekey, Skip);
        if (system.ParsedEntries.Count > MaximumEntries)
        {
            throw new FileSystemCorruptedException("Folded preservation manifest exceeds the entry limit.");
        }

        var payload = JsonSerializer.SerializeToNode(system, FileSystemPreservationJsonContext.Default.FileSystemPreservationData)!.AsObject();
        systemMetadata.FileSystemPreservation = payload;
        payload.Remove("configurationHash");
        payload["configurationHash"] = HashConfiguration(systemMetadata);
        await systemMetadata.SaveAsync(systemMetadataDirectory, cancellationToken).ConfigureAwait(false);
        return (true, rekeyedEntries.Count);
    }

    /// <summary>
    /// Hashes the metadata document in one streaming pass with the preservation digest removed, using the same compact
    /// camel-cased JSON text <see cref="FileSystemPreservation.HashConfiguration"/> derives through a node graph, so the
    /// recorded digest stays verifiable by the existing reader.
    /// <para>以单趟流式方式对移除保留摘要后的元数据文档计算哈希，输出与 <see cref="FileSystemPreservation.HashConfiguration"/>
    /// 经节点图得到的紧凑驼峰 JSON 文本一致，因此记录的摘要仍可由既有读取方校验。</para>
    /// </summary>
    private static string HashConfiguration(RepackMetadata metadata)
    {
        using var hashing = new HashingWriteStream(Stream.Null, leaveOpen: true);
        using (var writer = new Utf8JsonWriter(hashing))
        {
            JsonSerializer.Serialize(writer, metadata, WorkflowJsonContext.Default.RepackMetadata);
        }

        return Convert.ToHexString(hashing.GetHashAndReset());
    }

    private static void FoldTable<TSource>(IEnumerable<KeyValuePair<string, TSource>> source,
        Dictionary<string, TSource> target, Func<string, string> rekey, Func<string, bool> skip)
    {
        foreach (var pair in source)
        {
            var normalized = FileSystemEditPlan.Normalize(pair.Key);
            if (skip(normalized)) continue;
            target[rekey(normalized)] = pair.Value;
        }
    }
}