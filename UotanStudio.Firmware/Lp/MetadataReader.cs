using System.Security.Cryptography;

namespace UotanStudio.Firmware.Lp;

/// <summary>
/// Reads and parses Android Logical Partition (LP) metadata from a super image.
/// <para>从 super 镜像读取并解析 Android 逻辑分区(LP)元数据。</para>
/// </summary>
public static class MetadataReader
{
    /// <summary>
    /// Reads LP metadata from an image file (uses slot 0 primary metadata).
    /// <para>从镜像文件读取 LP 元数据(使用槽 0 主元数据)。</para>
    /// </summary>
    public static LpMetadata ReadFromImageFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadFromImageStream(stream);
    }

    /// <summary>
    /// Reads LP metadata from an image stream (uses slot 0 primary metadata).
    /// <para>从镜像流读取 LP 元数据(使用槽 0 主元数据)。</para>
    /// </summary>
    public static LpMetadata ReadFromImageStream(Stream stream)
    {
        ReadPrimaryGeometry(stream, out var geometry);
        return ReadPrimaryMetadata(stream, geometry, 0);
    }

    /// <summary>
    /// Parses the geometry structure from the beginning of an image stream.
    /// <para>从镜像流起始处解析几何结构。</para>
    /// </summary>
    public static void ReadPrimaryGeometry(Stream stream, out LpMetadataGeometry geometry) => ReadGeometry(stream, MetadataFormat.LP_PARTITION_RESERVED_BYTES, out geometry);

    /// <summary>
    /// Parses the backup geometry structure from an image stream.
    /// <para>从镜像流解析备份几何结构。</para>
    /// </summary>
    public static void ReadBackupGeometry(Stream stream, out LpMetadataGeometry geometry) => ReadGeometry(stream, MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE, out geometry);

    private static void ReadGeometry(Stream stream, long offset, out LpMetadataGeometry geometry)
    {
        var buffer = new byte[MetadataFormat.Sizes.Geometry];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        geometry = LpMetadataGeometry.FromBytes(buffer);

        if (geometry.Magic != MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
        {
            throw new InvalidDataException("Invalid LP geometry magic.");
        }

        // Verify geometry checksum (SHA256 of the struct with checksum zeroed).
        var structSize = (int)Math.Min(geometry.StructSize, MetadataFormat.Sizes.Geometry);
        var structData = buffer.AsSpan(0, structSize).ToArray();
        structData.AsSpan(8, 32).Clear();
        var hash = SHA256.HashData(structData);
        if (!hash.AsSpan().SequenceEqual(geometry.Checksum))
        {
            throw new InvalidDataException("LP geometry checksum mismatch.");
        }
    }

    /// <summary>
    /// Reads primary metadata for the given slot number.
    /// <para>读取指定槽号的主元数据。</para>
    /// </summary>
    public static LpMetadata ReadPrimaryMetadata(Stream stream, LpMetadataGeometry geometry, uint slotNumber)
    {
        var offset = GetPrimaryMetadataOffset(geometry, slotNumber);
        stream.Position = offset;
        return ParseMetadata(geometry, stream);
    }

    /// <summary>
    /// Parses a metadata block (header + tables) from the current stream position.
    /// <para>从当前流位置解析元数据块(头部 + 表)。</para>
    /// </summary>
    public static LpMetadata ParseMetadata(LpMetadataGeometry geometry, Stream stream)
    {
        var headerBuffer = new byte[MetadataFormat.Sizes.Header];
        stream.ReadExactly(headerBuffer);
        var header = LpMetadataHeader.FromBytes(headerBuffer);

        if (header.Magic != MetadataFormat.LP_METADATA_HEADER_MAGIC)
        {
            throw new InvalidDataException("Invalid LP metadata header magic.");
        }

        // Verify header checksum: SHA256 of header with the checksum field zeroed.
        var headerHashData = headerBuffer.AsSpan(0, (int)Math.Min(header.HeaderSize, headerBuffer.Length)).ToArray();
        headerHashData.AsSpan(12, 32).Clear();
        var headerHash = SHA256.HashData(headerHashData);
        if (!headerHash.AsSpan().SequenceEqual(header.HeaderChecksum))
        {
            throw new InvalidDataException("LP metadata header checksum mismatch.");
        }

        var tablesBuffer = new byte[header.TablesSize];
        stream.ReadExactly(tablesBuffer);
        var tablesHash = SHA256.HashData(tablesBuffer);
        if (!tablesHash.AsSpan().SequenceEqual(header.TablesChecksum))
        {
            throw new InvalidDataException("LP metadata tables checksum mismatch.");
        }

        var metadata = new LpMetadata { Geometry = geometry, Header = header };
        ParseTable(tablesBuffer, header.Partitions, metadata.Partitions, LpMetadataPartition.FromBytes);
        ParseTable(tablesBuffer, header.Extents, metadata.Extents, LpMetadataExtent.FromBytes);
        ParseTable(tablesBuffer, header.Groups, metadata.Groups, LpMetadataPartitionGroup.FromBytes);
        ParseTable(tablesBuffer, header.BlockDevices, metadata.BlockDevices, LpMetadataBlockDevice.FromBytes);
        return metadata;
    }

    private static void ParseTable<T>(byte[] buffer, LpMetadataTableDescriptor desc, List<T> list, Func<ReadOnlySpan<byte>, T> fromBytes)
    {
        if (desc.NumEntries == 0)
        {
            return;
        }
        for (uint i = 0; i < desc.NumEntries; i++)
        {
            var offset = (int)(desc.Offset + (i * desc.EntrySize));
            if (offset + desc.EntrySize > buffer.Length)
            {
                throw new InvalidDataException("LP table entry exceeds table buffer.");
            }
            list.Add(fromBytes(buffer.AsSpan(offset, (int)desc.EntrySize)));
        }
    }

    /// <summary>
    /// Calculates the byte offset of the primary metadata for a given slot.
    /// <para>计算给定槽号的主元数据字节偏移。</para>
    /// </summary>
    public static long GetPrimaryMetadataOffset(LpMetadataGeometry geometry, uint slotNumber)
        => MetadataFormat.LP_PARTITION_RESERVED_BYTES + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2) + ((long)slotNumber * geometry.MetadataMaxSize);

    /// <summary>
    /// Calculates the byte offset of the backup metadata for a given slot at the end of the device.
    /// <para>计算设备末尾给定槽号的备份元数据字节偏移。</para>
    /// </summary>
    public static long GetBackupMetadataOffset(LpMetadataGeometry geometry, ulong deviceSize, uint slotNumber)
    {
        var backupSize = (long)geometry.MetadataMaxSize * geometry.MetadataSlotCount;
        return (long)deviceSize - backupSize + ((long)slotNumber * geometry.MetadataMaxSize);
    }
}