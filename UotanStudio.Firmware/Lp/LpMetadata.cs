using System.Security.Cryptography;

namespace UotanStudio.Firmware.Lp;

/// <summary>
/// Represents the complete set of Android Logical Partition (LP) metadata.
/// <para>表示完整的 Android 逻辑分区(LP)元数据。</para>
/// </summary>
public sealed class LpMetadata
{
    /// <summary>
    /// Gets or sets the geometry describing metadata slots and sizes.
    /// <para>获取或设置描述元数据槽与大小的几何信息。</para>
    /// </summary>
    public LpMetadataGeometry Geometry { get; set; }

    /// <summary>
    /// Gets or sets the metadata header.
    /// <para>获取或设置元数据头部。</para>
    /// </summary>
    public LpMetadataHeader Header { get; set; }

    /// <summary>
    /// Gets or sets the list of logical partitions.
    /// <para>获取或设置逻辑分区列表。</para>
    /// </summary>
    public List<LpMetadataPartition> Partitions { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of extents belonging to the partitions.
    /// <para>获取或设置分区所属的 extent 列表。</para>
    /// </summary>
    public List<LpMetadataExtent> Extents { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of partition groups.
    /// <para>获取或设置分区组列表。</para>
    /// </summary>
    public List<LpMetadataPartitionGroup> Groups { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of block devices described in this metadata.
    /// <para>获取或设置此元数据描述的块设备列表。</para>
    /// </summary>
    public List<LpMetadataBlockDevice> BlockDevices { get; set; } = [];

    /// <summary>
    /// Gets the partition names in table order.
    /// <para>按表顺序获取分区名称。</para>
    /// </summary>
    public IReadOnlyList<string> PartitionNames => [.. Partitions.Select(p => p.NameString)];

    /// <summary>
    /// Computes the SHA256 of a byte buffer.
    /// <para>计算字节缓冲区的 SHA256。</para>
    /// </summary>
    internal static byte[] ComputeSha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}