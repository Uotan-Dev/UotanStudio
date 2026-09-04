using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 压缩簇元数据：描述一个已压缩（或待压缩）的16 页（64 KB）数据簇，
/// 包含压缩算法、原始大小与压缩后大小。每簇独立压缩，存储在数据块内。
/// <para>F2FS compressed cluster metadata: describes a compressed (or to-be-compressed)
/// 16-page (64 KB) data cluster with the algorithm, uncompressed size and compressed size.
/// Each cluster is compressed independently and stored in data blocks.</para>
/// </summary>
internal sealed record F2fsCompressedCluster
{
    /// <summary>
    /// 每簇页数（CLUSTER_BLOCK_SIZE = 16）。
    /// <para>Pages per cluster (CLUSTER_BLOCK_SIZE = 16).</para>
    /// </summary>
    public const int ClusterSizePages = 16;

    /// <summary>
    /// 每簇字节数（16 × 4096 = 65536）。
    /// <para>Bytes per cluster (16 × 4096 = 65536).</para>
    /// </summary>
    public const int ClusterSizeBytes = ClusterSizePages * F2fsNode.NodeSize;

    /// <summary>
    /// 压缩簇磁盘头大小（字节）。
    /// <para>On-disk cluster header size in bytes.</para>
    /// </summary>
    public const int HeaderSize = 12;

    // 头字段偏移。Header field offsets.
    private const int OffsetAlgorithm = 0;
    private const int OffsetClusterPages = 1;
    private const int OffsetReserved = 2;
    private const int OffsetUncompressedSize = 4;
    private const int OffsetCompressedSize = 8;

    /// <summary>
    /// 压缩算法标识。
    /// <para>Compression algorithm identifier.</para>
    /// </summary>
    public CompressionAlgorithm Algorithm { get; init; }

    /// <summary>
    /// 未压缩数据大小（字节，≤ 65536）。
    /// <para>Uncompressed data size in bytes (≤ 65536).</para>
    /// </summary>
    public int UncompressedSize { get; init; }

    /// <summary>
    /// 压缩后数据大小（字节，不含头部）。
    /// <para>Compressed data size in bytes (excluding header).</para>
    /// </summary>
    public int CompressedSize { get; init; }

    /// <summary>
    /// 该簇是否已压缩（压缩后比原始小才值得存储）。
    /// <para>Whether this cluster was actually compressed (worth storing only if smaller).</para>
    /// </summary>
    public bool IsCompressed => CompressedSize > 0 && CompressedSize < UncompressedSize;

    /// <summary>
    /// 含头部的磁盘总字节数。
    /// <para>Total on-disk bytes including the header.</para>
    /// </summary>
    public int DiskBytes => HeaderSize + (IsCompressed ? CompressedSize : UncompressedSize);

    /// <summary>
    /// 所需数据块数（每块 4096 字节，向上取整）。
    /// <para>Data blocks required (ceiling of bytes / 4096).</para>
    /// </summary>
    public int RequiredBlocks => (DiskBytes + F2fsNode.NodeSize - 1) / F2fsNode.NodeSize;

    /// <summary>
    /// 从磁盘头部解析压缩簇元数据。
    /// <para>Parses compressed cluster metadata from a disk header.</para>
    /// </summary>
    public static F2fsCompressedCluster FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new FormatException($"F2FS compressed cluster header too short: {data.Length} bytes.");
        }

        return new F2fsCompressedCluster
        {
            Algorithm = (CompressionAlgorithm)data[OffsetAlgorithm],
            UncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data[OffsetUncompressedSize..]),
            CompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data[OffsetCompressedSize..]),
        };
    }

    /// <summary>
    /// 将压缩簇头部写入缓冲区（至少 <see cref="HeaderSize"/> 字节）。
    /// <para>Writes the cluster header into a buffer of at least <see cref="HeaderSize"/> bytes.</para>
    /// </summary>
    public void WriteHeaderTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException("Cluster header destination is too small.", nameof(destination));
        }

        destination.Clear();
        destination[OffsetAlgorithm] = (byte)Algorithm;
        destination[OffsetClusterPages] = ClusterSizePages;
        BinaryPrimitives.WriteInt32LittleEndian(destination[OffsetUncompressedSize..], UncompressedSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination[OffsetCompressedSize..], CompressedSize);
    }
}