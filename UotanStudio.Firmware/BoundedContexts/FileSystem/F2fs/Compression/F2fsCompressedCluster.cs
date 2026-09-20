using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;

internal sealed record F2fsCompressedCluster
{
    public const int ClusterSizePages = 16;

    public const int ClusterSizeBytes = ClusterSizePages * F2fsNode.NodeSize;

    public const int HeaderSize = 24;

    public CompressionAlgorithm Algorithm { get; init; }

    public int UncompressedSize { get; init; }

    public int CompressedSize { get; init; }

    public uint Checksum { get; init; }

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
            UncompressedSize = ClusterSizeBytes,
            CompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data),
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
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

        destination[..HeaderSize].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(destination, CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Checksum);
    }
}