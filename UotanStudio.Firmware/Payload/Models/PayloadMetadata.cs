using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Models;

/// <summary>
/// Represents the metadata extracted from an Android OTA payload file.
/// <para>表示从 Android OTA payload 文件中提取的元数据信息。</para>
/// </summary>
public class PayloadMetadata
{
    /// <summary>
    /// Gets the block size (in bytes) used by the payload, typically 4096.
    /// <para>获取 payload 使用的块大小（字节），通常为 4096。</para>
    /// </summary>
    public uint BlockSize { get; internal set; }

    /// <summary>
    /// Gets the list of partition names contained in the payload.
    /// <para>获取 payload 中包含的分区名称列表。</para>
    /// </summary>
    public List<string> PartitionNames { get; } = [];

    /// <summary>
    /// Gets the length (in bytes) of the serialized manifest protobuf.
    /// <para>获取序列化的 manifest protobuf 的长度（字节）。</para>
    /// </summary>
    public ulong ManifestLength { get; internal set; }

    /// <summary>
    /// Gets the length (in bytes) of the metadata signature section.
    /// <para>获取元数据签名段的长度（字节）。</para>
    /// </summary>
    public uint MetadataSignatureLength { get; internal set; }

    /// <summary>
    /// Gets the base offset (in bytes) where payload data blobs begin.
    /// <para>获取 payload 数据块的起始偏移量（字节）。</para>
    /// </summary>
    public ulong BaseOffset { get; internal set; }

    /// <summary>
    /// Gets the parsed DeltaArchiveManifest protobuf message from the payload.
    /// <para>获取从 payload 解析得到的 DeltaArchiveManifest protobuf 消息。</para>
    /// </summary>
    public DeltaArchiveManifest Manifest { get; internal set; } = new();

    /// <summary>
    /// Gets the maximum timestamp of the OS allowed to apply this payload, used to prevent downgrading.
    /// <para>获取允许应用此载荷的操作系统最大时间戳，用于防止降级。</para>
    /// </summary>
    public long MaxTimestamp { get; internal set; }

    /// <summary>
    /// Gets the dynamic partition metadata, which describes dynamic partition groups.
    /// <para>获取动态分区元数据，描述动态分区组。</para>
    /// </summary>
    public DynamicPartitionMetadata DynamicPartitionMetadata { get; internal set; } = new();

    /// <summary>
    /// Gets the offset in the payload where the payload signatures begin.
    /// <para>获取载荷中载荷签名开始的偏移量。</para>
    /// </summary>
    public ulong SignaturesOffset { get; internal set; }

    /// <summary>
    /// Gets the size in bytes of the payload signatures.
    /// <para>获取载荷签名的字节大小。</para>
    /// </summary>
    public ulong SignaturesSize { get; internal set; }
}

/// <summary>
/// Represents size information for a single partition in the payload.
/// <para>表示 payload 中单个分区的大小信息。</para>
/// </summary>
public class PartitionInfoData
{
    /// <summary>
    /// Gets the name of the partition.
    /// <para>获取分区名称。</para>
    /// </summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets the total size of the partition in bytes.
    /// <para>获取分区的总大小（字节）。</para>
    /// </summary>
    public ulong SizeBytes { get; internal set; }

    /// <summary>
    /// Gets a human-readable string representation of the partition size (e.g., "2.0GB", "512.0MB").
    /// <para>获取分区大小的可读字符串表示（例如 "2.0GB"、"512.0MB"）。</para>
    /// </summary>
    public string SizeReadable { get; internal set; } = string.Empty;
}

/// <summary>
/// Provides helper methods for computing partition information from a DeltaArchiveManifest.
/// <para>提供从 DeltaArchiveManifest 计算分区信息的辅助方法。</para>
/// </summary>
internal static class PartitionInfoHelper
{
    /// <summary>
    /// Computes partition size information for all named partitions in the manifest.
    /// <para>计算 manifest 中所有已命名分区的分区大小信息。</para>
    /// </summary>
    /// <param name="manifest">The DeltaArchiveManifest containing partition definitions.
    /// <para>包含分区定义的 DeltaArchiveManifest。</para>
    /// </param>
    /// <returns>A list of <see cref="PartitionInfoData"/> objects representing each partition's size.
    /// <para>表示每个分区大小的 <see cref="PartitionInfoData"/> 对象列表。</para>
    /// </returns>
    internal static List<PartitionInfoData> ComputePartitionInfo(DeltaArchiveManifest manifest)
    {
        var list = new List<PartitionInfoData>();

        foreach (var part in manifest.Partitions)
        {
            if (string.IsNullOrEmpty(part.PartitionName))
            {
                continue;
            }

            ulong blocks = 0;
            foreach (var op in part.Operations)
            {
                foreach (var ext in op.DstExtents)
                {
                    blocks += ext.NumBlocks;
                }
            }

            var bytes = blocks * manifest.BlockSize;

            list.Add(new PartitionInfoData
            {
                Name = part.PartitionName,
                SizeBytes = bytes,
                SizeReadable = FormatSize(bytes)
            });
        }

        return list;
    }

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g., "2.0GB", "512.0MB").
    /// <para>将字节数格式化为可读的大小字符串（例如 "2.0GB"、"512.0MB"）。</para>
    /// </summary>
    /// <param name="bytes">The byte count to format.
    /// <para>要格式化的字节数。</para>
    /// </param>
    /// <returns>A human-readable size string.
    /// <para>可读的大小字符串。</para>
    /// </returns>
    internal static string FormatSize(ulong bytes)
    {
        return bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):F1}GB"
            : bytes >= 1024UL * 1024 ? $"{bytes / (1024.0 * 1024):F1}MB" : $"{bytes / 1024.0:F1}KB";
    }
}