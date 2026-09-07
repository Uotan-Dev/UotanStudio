using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

public static class MetadataReader
{
    internal const int MaximumMetadataBytes = 64 * 1024 * 1024;
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
        LpMetadataGeometry geometry;
        try { ReadPrimaryGeometry(stream, out geometry); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            ReadBackupGeometry(stream, out geometry);
        }
        try { return ReadPrimaryMetadata(stream, geometry, 0); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            stream.Position = GetBackupMetadataOffset(geometry, (ulong)stream.Length, 0);
            return ParseMetadata(geometry, stream);
        }
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


        if (geometry.StructSize != MetadataFormat.Sizes.Geometry)
        {
            throw new InvalidDataException("Invalid LP geometry structure size.");
        }
        ValidateGeometry(geometry);
        var structSize = (int)geometry.StructSize;
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
        ValidateGeometry(geometry);
        var headerBuffer = new byte[MetadataFormat.Sizes.LegacyHeader];
        stream.ReadExactly(headerBuffer);
        var header = LpMetadataHeader.FromBytes(headerBuffer);

        if (header.Magic != MetadataFormat.LP_METADATA_HEADER_MAGIC)
        {
            throw new InvalidDataException("Invalid LP metadata header magic.");
        }

        var headerSize = header.MinorVersion >= 2 ? MetadataFormat.Sizes.Header : MetadataFormat.Sizes.LegacyHeader;
        if (header.MajorVersion != MetadataFormat.LP_METADATA_MAJOR_VERSION || header.MinorVersion > 2 || header.HeaderSize != headerSize)
        {
            throw new InvalidDataException("Unsupported LP metadata header version or size.");
        }
        if (headerSize > headerBuffer.Length)
        {
            Array.Resize(ref headerBuffer, headerSize);
            stream.ReadExactly(headerBuffer.AsSpan(MetadataFormat.Sizes.LegacyHeader));
            header = LpMetadataHeader.FromBytes(headerBuffer);
        }
        if (header.HeaderSize > geometry.MetadataMaxSize || header.TablesSize > geometry.MetadataMaxSize - header.HeaderSize
            || (stream.CanSeek && header.TablesSize > stream.Length - stream.Position))
        {
            throw new InvalidDataException("LP metadata tables exceed their slot or input bounds.");
        }


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
        ParseTable(tablesBuffer, header.Partitions, MetadataFormat.Sizes.Partition, metadata.Partitions, LpMetadataPartition.FromBytes);
        ParseTable(tablesBuffer, header.Extents, MetadataFormat.Sizes.Extent, metadata.Extents, LpMetadataExtent.FromBytes);
        ParseTable(tablesBuffer, header.Groups, MetadataFormat.Sizes.PartitionGroup, metadata.Groups, LpMetadataPartitionGroup.FromBytes);
        ParseTable(tablesBuffer, header.BlockDevices, MetadataFormat.Sizes.BlockDevice, metadata.BlockDevices, LpMetadataBlockDevice.FromBytes);
        ValidateBlockDevices(metadata);
        foreach (var partition in metadata.Partitions)
        {
            var validAttributes = header.MinorVersion >= 1 ? 0xfu : 0x3u;
            if ((partition.Attributes & ~validAttributes) != 0)
                throw new InvalidDataException("LP partition attributes are invalid for the metadata version.");
            if (partition.GroupIndex >= metadata.Groups.Count || partition.FirstExtentIndex > metadata.Extents.Count
                || partition.NumExtents > metadata.Extents.Count - partition.FirstExtentIndex)
                throw new InvalidDataException("LP partition references an invalid group or extent range.");
        }
        foreach (var extent in metadata.Extents)
        {
            if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO) continue;
            if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR || extent.TargetSource >= metadata.BlockDevices.Count)
                throw new InvalidDataException("LP extent references an invalid target.");
            var device = metadata.BlockDevices[(int)extent.TargetSource];
            var sectors = device.Size / MetadataFormat.LP_SECTOR_SIZE;
            if (extent.TargetData < device.FirstLogicalSector || extent.TargetData > sectors || extent.NumSectors > sectors - extent.TargetData)
                throw new InvalidDataException("LP extent exceeds the target device data area.");
        }
        return metadata;
    }

    internal static void ValidateBlockDevices(LpMetadata metadata)
    {
        ValidateGeometry(metadata.Geometry);
        if (metadata.BlockDevices.Count == 0)
            throw new InvalidDataException("LP metadata does not specify a super device.");
        // liblp identifies the first block device as the device carrying metadata.
        var superDevice = metadata.BlockDevices[0];
        var metadataBytes = checked(MetadataFormat.LP_PARTITION_RESERVED_BYTES + 2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE
            + 2UL * metadata.Geometry.MetadataMaxSize * metadata.Geometry.MetadataSlotCount);
        var firstUsableSector = metadataBytes / MetadataFormat.LP_SECTOR_SIZE;
        if (superDevice.FirstLogicalSector < firstUsableSector
            || superDevice.FirstLogicalSector > superDevice.Size / MetadataFormat.LP_SECTOR_SIZE)
            throw new InvalidDataException("LP super device data area overlaps metadata or exceeds the device size.");
    }

    private static void ParseTable<T>(byte[] buffer, LpMetadataTableDescriptor desc, uint expectedSize, List<T> list, Func<ReadOnlySpan<byte>, T> fromBytes)
    {
        if (desc.EntrySize != expectedSize || desc.Offset > buffer.Length
            || (ulong)desc.NumEntries * desc.EntrySize > (ulong)buffer.Length - desc.Offset)
        {
            throw new InvalidDataException("Invalid LP table descriptor.");
        }
        if (desc.NumEntries == 0)
        {
            return;
        }
        for (uint i = 0; i < desc.NumEntries; i++)
        {
            var offset = checked((int)((ulong)desc.Offset + ((ulong)i * desc.EntrySize)));
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
    {
        ValidateGeometry(geometry);
        if (slotNumber >= geometry.MetadataSlotCount) throw new ArgumentOutOfRangeException(nameof(slotNumber));
        return checked(MetadataFormat.LP_PARTITION_RESERVED_BYTES + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2) + ((long)slotNumber * geometry.MetadataMaxSize));
    }

    /// <summary>
    /// Calculates the backup slot offset following the primary metadata slots.
    /// <para>计算位于全部主元数据槽之后的备份槽偏移。</para>
    /// </summary>
    public static long GetBackupMetadataOffset(LpMetadataGeometry geometry, ulong deviceSize, uint slotNumber)
    {
        var offset = checked(GetPrimaryMetadataOffset(geometry, slotNumber) + (long)geometry.MetadataMaxSize * geometry.MetadataSlotCount);
        if ((ulong)offset > deviceSize || geometry.MetadataMaxSize > deviceSize - (ulong)offset)
            throw new InvalidDataException("LP backup metadata exceeds the device bounds.");
        return offset;
    }

    internal static void ValidateGeometry(LpMetadataGeometry geometry)
    {
        if (geometry.MetadataMaxSize < MetadataFormat.Sizes.LegacyHeader || geometry.MetadataMaxSize > MaximumMetadataBytes
            || geometry.MetadataMaxSize % MetadataFormat.LP_SECTOR_SIZE != 0 || geometry.MetadataSlotCount is 0 or > 32
            || geometry.LogicalBlockSize < MetadataFormat.LP_SECTOR_SIZE || (geometry.LogicalBlockSize & (geometry.LogicalBlockSize - 1)) != 0)
            throw new InvalidDataException("Invalid or excessive LP geometry dimensions.");
    }
}