using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;

/// <summary>
/// Holds one decoded LP metadata slot: geometry, header, and every table read from a super image.
/// <para>保存单个已解码的 LP 元数据槽位：来自 super 镜像的几何信息、头部以及全部表。</para>
/// </summary>
public sealed class LpMetadata
{
    /// <summary>
    /// Block layout and metadata capacity geometry validated during parsing.
    /// <para>解析时已校验的块布局与元数据容量几何信息。</para>
    /// </summary>
    /// <value>
    /// The geometry struct; its checksum, magic and dimensions are checked before a reader returns it.
    /// <para>几何信息结构体；读取器返回前已校验其校验和、魔数与尺寸。</para>
    /// </value>
    public LpMetadataGeometry Geometry { get; set; }

    /// <summary>
    /// Header describing the magic, version and byte offsets of each table within the slot.
    /// <para>描述槽位内各表魔数、版本与字节偏移的头部。</para>
    /// </summary>
    /// <value>
    /// The header struct as decoded from the image.
    /// <para>按镜像解码得到的头部结构体。</para>
    /// </value>
    public LpMetadataHeader Header { get; set; }

    /// <summary>
    /// Partition entries, each pointing into <see cref="Extents"/> and one group in <see cref="Groups"/>.
    /// <para>分区条目，每条指向 <see cref="Extents"/> 中的一段 extent 以及 <see cref="Groups"/> 中的一个组。</para>
    /// </summary>
    /// <value>
    /// Mutable partition list; empty when the image declares no partitions.
    /// <para>可变的分区列表；镜像未声明分区时为空。</para>
    /// </value>
    public List<LpMetadataPartition> Partitions { get; set; } = [];

    /// <summary>
    /// Flat extent table shared by all partitions; each partition slices it via first-index and count.
    /// <para>所有分区共享的扁平 extent 表；每个分区按起始索引与数量截取。</para>
    /// </summary>
    /// <value>
    /// Mutable extent list in image order; empty when no partition has extents.
    /// <para>按镜像顺序排列的可变 extent 列表；无分区持有 extent 时为空。</para>
    /// </value>
    public List<LpMetadataExtent> Extents { get; set; } = [];

    /// <summary>
    /// Partition groups that bound the combined size of their member partitions.
    /// <para>用于限定成员分区总大小的分区组。</para>
    /// </summary>
    /// <value>
    /// Mutable group list; the default group is not guaranteed to exist unless the image defines it.
    /// <para>可变的组列表；除非镜像自行定义，否则不保证存在默认组。</para>
    /// </value>
    public List<LpMetadataPartitionGroup> Groups { get; set; } = [];

    /// <summary>
    /// Backing block devices on which the extents are laid out.
    /// <para>extent 所排布的后备块设备。</para>
    /// </summary>
    /// <value>
    /// Mutable block-device list; decoded images normally contain a single <c>super</c> device at index 0.
    /// <para>可变的块设备列表；解码得到的镜像通常仅在索引 0 处含一个 <c>super</c> 设备。</para>
    /// </value>
    public List<LpMetadataBlockDevice> BlockDevices { get; set; } = [];

    /// <summary>
    /// Decoded names of every partition, derived from <see cref="Partitions"/> on each access.
    /// <para>每个分区的已解码名称，每次访问时由 <see cref="Partitions"/> 派生。</para>
    /// </summary>
    /// <value>
    /// A fresh snapshot list in partition order; empty when there are no partitions.
    /// <para>按分区顺序生成的全新快照列表；无分区时为空。</para>
    /// </value>
    public IReadOnlyList<string> PartitionNames => [.. Partitions.Select(p => p.NameString)];

    /// <summary>
    /// Computes the SHA256 of a byte buffer.
    /// <para>计算字节缓冲区的 SHA256。</para>
    /// </summary>
    internal static byte[] ComputeSha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}