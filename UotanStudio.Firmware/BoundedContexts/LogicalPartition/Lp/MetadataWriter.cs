using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

public static class MetadataWriter
{
    /// <summary>
    /// Serializes an LpMetadataGeometry into a padded 4KB byte array.
    /// <para>将 LpMetadataGeometry 序列化为填充的 4KB 字节数组。</para>
    /// </summary>
    public static byte[] SerializeGeometry(LpMetadataGeometry geometry)
    {
        MetadataReader.ValidateGeometry(geometry);
        geometry = new LpMetadataGeometry
        {
            Magic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC,
            StructSize = MetadataFormat.Sizes.Geometry,
            Checksum = new byte[32],
            MetadataMaxSize = geometry.MetadataMaxSize,
            MetadataSlotCount = geometry.MetadataSlotCount,
            LogicalBlockSize = geometry.LogicalBlockSize
        };

        var padded = new byte[MetadataFormat.LP_METADATA_GEOMETRY_SIZE];
        geometry.ToBytes().CopyTo(padded);

        var hash = LpMetadata.ComputeSha256(padded.AsSpan(0, MetadataFormat.Sizes.Geometry));
        hash.CopyTo(padded.AsSpan(8, 32));
        return padded;
    }

    /// <summary>
    /// Serializes LpMetadata (header + all tables) into a binary byte array.
    /// <para>将 LpMetadata(头部 + 所有表)序列化为二进制字节数组。</para>
    /// </summary>
    public static byte[] SerializeMetadata(LpMetadata metadata)
    {
        MetadataReader.ValidateGeometry(metadata.Geometry);
        if (metadata.Header.MinorVersion > 2)
            throw new InvalidDataException("Unsupported LP metadata minor version.");
        var minorVersion = metadata.Header.Flags != 0 ? (ushort)2 : metadata.Header.MinorVersion;
        foreach (var partition in metadata.Partitions)
        {
            if ((partition.Attributes & ~0xfu) != 0)
                throw new InvalidDataException("Unknown LP partition attributes.");
            if ((partition.Attributes & 0xcu) != 0 && minorVersion < 1)
                minorVersion = 1;
        }
        var partitionTableSize = checked((uint)metadata.Partitions.Count * MetadataFormat.Sizes.Partition);
        var extentTableSize = checked((uint)metadata.Extents.Count * MetadataFormat.Sizes.Extent);
        var groupTableSize = checked((uint)metadata.Groups.Count * MetadataFormat.Sizes.PartitionGroup);
        var blockDeviceTableSize = checked((uint)metadata.BlockDevices.Count * MetadataFormat.Sizes.BlockDevice);
        var total = checked((ulong)partitionTableSize + extentTableSize + groupTableSize + blockDeviceTableSize);
        var headerSize = (uint)(minorVersion >= 2 ? MetadataFormat.Sizes.Header : MetadataFormat.Sizes.LegacyHeader);
        if (total + headerSize > metadata.Geometry.MetadataMaxSize)
            throw new InvalidDataException("LP tables exceed the metadata slot size.");

        var header = new LpMetadataHeader
        {
            Partitions = new LpMetadataTableDescriptor { Offset = 0, NumEntries = (uint)metadata.Partitions.Count, EntrySize = MetadataFormat.Sizes.Partition },
            Extents = new LpMetadataTableDescriptor { Offset = partitionTableSize, NumEntries = (uint)metadata.Extents.Count, EntrySize = MetadataFormat.Sizes.Extent },
            Groups = new LpMetadataTableDescriptor { Offset = partitionTableSize + extentTableSize, NumEntries = (uint)metadata.Groups.Count, EntrySize = MetadataFormat.Sizes.PartitionGroup },
            BlockDevices = new LpMetadataTableDescriptor { Offset = partitionTableSize + extentTableSize + groupTableSize, NumEntries = (uint)metadata.BlockDevices.Count, EntrySize = MetadataFormat.Sizes.BlockDevice },
            Magic = MetadataFormat.LP_METADATA_HEADER_MAGIC,
            MajorVersion = MetadataFormat.LP_METADATA_MAJOR_VERSION,
            MinorVersion = minorVersion,
            HeaderSize = headerSize,
            HeaderChecksum = new byte[32],
            TablesChecksum = new byte[32],
            Flags = metadata.Header.Flags
        };
        header = new LpMetadataHeader
        {
            Magic = header.Magic,
            MajorVersion = header.MajorVersion,
            MinorVersion = header.MinorVersion,
            HeaderSize = header.HeaderSize,
            HeaderChecksum = header.HeaderChecksum,
            TablesSize = header.BlockDevices.Offset + blockDeviceTableSize,
            TablesChecksum = header.TablesChecksum,
            Partitions = header.Partitions,
            Extents = header.Extents,
            Groups = header.Groups,
            BlockDevices = header.BlockDevices,
            Flags = header.Flags
        };

        var tables = new byte[header.TablesSize];
        WriteTable(tables, header.Partitions, metadata.Partitions, p => p.ToBytes());
        WriteTable(tables, header.Extents, metadata.Extents, e => e.ToBytes());
        WriteTable(tables, header.Groups, metadata.Groups, g => g.ToBytes());
        WriteTable(tables, header.BlockDevices, metadata.BlockDevices, b => b.ToBytes());

        var tablesHash = LpMetadata.ComputeSha256(tables);
        var header2 = new LpMetadataHeader
        {
            Magic = header.Magic,
            MajorVersion = header.MajorVersion,
            MinorVersion = header.MinorVersion,
            HeaderSize = header.HeaderSize,
            HeaderChecksum = header.HeaderChecksum,
            TablesSize = header.TablesSize,
            TablesChecksum = tablesHash,
            Partitions = header.Partitions,
            Extents = header.Extents,
            Groups = header.Groups,
            BlockDevices = header.BlockDevices,
            Flags = header.Flags
        };

        var headerBytes = header2.ToBytes();
        var headerHash = LpMetadata.ComputeSha256(headerBytes);
        headerHash.CopyTo(headerBytes.AsSpan(12, 32));

        var result = new byte[headerBytes.Length + tables.Length];
        headerBytes.CopyTo(result, 0);
        tables.CopyTo(result, headerBytes.Length);
        return result;
    }

    private static void WriteTable<T>(byte[] tables, LpMetadataTableDescriptor desc, List<T> entries, Func<T, byte[]> toBytes)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = (int)(desc.Offset + ((uint)i * desc.EntrySize));
            toBytes(entries[i]).CopyTo(tables, offset);
        }
    }

    /// <summary>
    /// Writes geometry + metadata to an image stream.
    /// <para>将几何信息 + 元数据写入镜像流。</para>
    /// </summary>
    public static void WriteToImageStream(Stream stream, LpMetadata metadata)
    {
        MetadataReader.ValidateBlockDevices(metadata);
        var geometryBlob = SerializeGeometry(metadata.Geometry);
        var metadataBlob = SerializeMetadata(metadata);

        var deviceSize = metadata.BlockDevices.Count > 0 ? metadata.BlockDevices[0].Size : (ulong)stream.Length;
        _ = MetadataReader.GetBackupMetadataOffset(metadata.Geometry, deviceSize, metadata.Geometry.MetadataSlotCount - 1);

        if (metadataBlob.Length > metadata.Geometry.MetadataMaxSize)
        {
            throw new InvalidOperationException("Serialized metadata size exceeds the MetadataMaxSize defined in geometry.");
        }

        stream.Position = MetadataFormat.LP_PARTITION_RESERVED_BYTES;
        stream.Write(geometryBlob);
        stream.Write(geometryBlob);

        for (uint i = 0; i < metadata.Geometry.MetadataSlotCount; i++)
        {
            var primaryOffset = MetadataReader.GetPrimaryMetadataOffset(metadata.Geometry, i);
            stream.Position = primaryOffset;
            stream.Write(metadataBlob);

            {
                var backupOffset = MetadataReader.GetBackupMetadataOffset(metadata.Geometry, deviceSize, i);
                stream.Position = backupOffset;
                stream.Write(metadataBlob);
            }
        }
    }

    /// <summary>
    /// Atomically writes geometry and metadata, preserving existing partition data within the new device size.
    /// <para>原子写入几何信息和元数据，保留新设备大小范围内已有的分区数据。</para>
    /// </summary>
    public static void WriteToImageFile(string path, LpMetadata metadata)
    {
        MetadataReader.ValidateGeometry(metadata.Geometry);
        MetadataReader.ValidateBlockDevices(metadata);
        var deviceSize = checked((long)metadata.BlockDevices[0].Size);
        FileExtraction.WriteAtomic(path, output =>
        {
            output.SetLength(deviceSize);
            if (File.Exists(path))
            {
                using var original = File.OpenRead(path);
                StreamCopy.CopyExactly(original, output, Math.Min(original.Length, deviceSize));
            }
            WriteToImageStream(output, metadata);
        });
    }
}