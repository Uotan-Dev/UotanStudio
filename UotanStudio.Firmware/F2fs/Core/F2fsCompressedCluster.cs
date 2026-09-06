using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

internal sealed record F2fsCompressedCluster
{
    public const int ClusterSizePages = 16;

    public const int ClusterSizeBytes = ClusterSizePages * F2fsNode.NodeSize;

    public const int HeaderSize = 12;


    private const int OffsetAlgorithm = 0;
    private const int OffsetClusterPages = 1;
    private const int OffsetReserved = 2;
    private const int OffsetUncompressedSize = 4;
    private const int OffsetCompressedSize = 8;

    public CompressionAlgorithm Algorithm { get; init; }

    public int UncompressedSize { get; init; }

    public int CompressedSize { get; init; }

    public bool IsCompressed => CompressedSize > 0 && CompressedSize < UncompressedSize;

    public int DiskBytes => HeaderSize + (IsCompressed ? CompressedSize : UncompressedSize);

    public int RequiredBlocks => (DiskBytes + F2fsNode.NodeSize - 1) / F2fsNode.NodeSize;

    /// <summary>
    /// Parses compressed cluster metadata from a disk header.
    /// <para>从磁盘头部解析压缩簇元数据。</para>
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
    /// Writes the cluster header into a buffer of at least HeaderSize bytes.
    /// <para>将压缩簇头部写入缓冲区（至少 HeaderSize 字节）。</para>
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
