using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Models;

public class PayloadMetadata
{
    public uint BlockSize { get; internal set; }

    public List<string> PartitionNames { get; } = [];

    public ulong ManifestLength { get; internal set; }

    public uint MetadataSignatureLength { get; internal set; }

    public ulong BaseOffset { get; internal set; }

    public DeltaArchiveManifest Manifest { get; internal set; } = new();

    public long MaxTimestamp { get; internal set; }

    public DynamicPartitionMetadata DynamicPartitionMetadata { get; internal set; } = new();

    public ulong SignaturesOffset { get; internal set; }

    public ulong SignaturesSize { get; internal set; }
}

public class PartitionInfoData
{
    public string Name { get; internal set; } = string.Empty;

    public ulong SizeBytes { get; internal set; }

    public string SizeReadable { get; internal set; } = string.Empty;
}

internal static class PartitionInfoHelper
{
    /// <summary>
    /// Computes partition size information for all named partitions in the manifest.
    /// <para>计算 manifest 中所有已命名分区的分区大小信息。</para>
    /// </summary>
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
    internal static string FormatSize(ulong bytes)
    {
        return bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):F1}GB"
            : bytes >= 1024UL * 1024 ? $"{bytes / (1024.0 * 1024):F1}MB" : $"{bytes / 1024.0:F1}KB";
    }
}
