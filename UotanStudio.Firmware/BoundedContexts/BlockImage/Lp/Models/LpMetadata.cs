using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;

public sealed class LpMetadata
{
    public LpMetadataGeometry Geometry { get; set; }

    public LpMetadataHeader Header { get; set; }

    public List<LpMetadataPartition> Partitions { get; set; } = [];

    public List<LpMetadataExtent> Extents { get; set; } = [];

    public List<LpMetadataPartitionGroup> Groups { get; set; } = [];

    public List<LpMetadataBlockDevice> BlockDevices { get; set; } = [];

    public IReadOnlyList<string> PartitionNames => [.. Partitions.Select(p => p.NameString)];

    /// <summary>
    /// Computes the SHA256 of a byte buffer.
    /// <para>计算字节缓冲区的 SHA256。</para>
    /// </summary>
    internal static byte[] ComputeSha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}