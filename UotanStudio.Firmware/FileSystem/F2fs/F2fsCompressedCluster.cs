using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.F2fs.Models;

namespace UotanStudio.Firmware.FileSystem.F2fs;

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
    /// <param name="data">Cluster header bytes, at least <see cref="HeaderSize"/> long, whose first little-endian int32 is the compressed size and whose next uint32 is the cluster checksum; the remaining header bytes carry no cluster information.
    /// <para>簇头部字节，长度至少为 <see cref="HeaderSize"/>：首个小端 int32 是压缩后大小，随后的 uint32 是簇校验和，其余头部字节不携带簇信息。</para></param>
    /// <returns>Cluster whose <see cref="UncompressedSize"/> is the fixed full cluster size and whose algorithm field keeps the <see cref="CompressionAlgorithm"/> default, because the disk header does not store it.
    /// <para>返回的簇其 <see cref="UncompressedSize"/> 为固定的整簇大小，算法字段保持 <see cref="CompressionAlgorithm"/> 默认值，因为磁盘头部不存储算法。</para></returns>
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
    /// <param name="destination">Buffer receiving the header; its first <see cref="HeaderSize"/> bytes are cleared, then the little-endian int32 compressed size is written at offset 0 and the uint32 checksum at offset 4. Fewer writable bytes than the header size are rejected.
    /// <para>接收头部的缓冲区；先清零前 <see cref="HeaderSize"/> 字节，再在偏移 0 写入小端 int32 压缩后大小、偏移 4 写入 uint32 校验和。可写字节少于头部长度时被拒绝。</para></param>
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