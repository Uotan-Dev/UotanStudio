using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BlockImage.Lp;

/// <summary>
/// Serialises dynamic-partition (LP) geometry and metadata into little-endian byte blobs and lays
/// them out inside a super image, including the primary and backup copies of every metadata slot.
/// <para>将动态分区（LP）几何信息与元数据序列化为小端字节块，并布局到 super 镜像内部，包含每个元数据槽位的主副本与备份副本。</para>
/// </summary>
public static class MetadataWriter
{
    /// <summary>
    /// Serializes an LpMetadataGeometry into a padded 4KB byte array.
    /// <para>将 LpMetadataGeometry 序列化为填充的 4KB 字节数组。</para>
    /// </summary>
    /// <param name="geometry">
    /// Geometry whose slot size, slot count and logical block size are written out; its stored magic,
    /// struct size and checksum fields are overwritten with the values this format requires.
    /// <para>要写出的槽位大小、槽位数量与逻辑块大小；其存储的魔数、结构大小和校验和字段会被本格式要求的值覆盖。</para>
    /// </param>
    /// <returns>
    /// A buffer of <c>LP_METADATA_GEOMETRY_SIZE</c> bytes holding the struct at the front, zero padding
    /// behind it, and the SHA-256 of the struct bytes at offset <c>8</c>.
    /// <para>长度为 <c>LP_METADATA_GEOMETRY_SIZE</c> 的缓冲区：开头是结构体，其后为零填充，偏移 <c>8</c> 处存放结构体字节的 SHA-256。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The geometry dimensions are out of range, not sector-aligned, or the slot count is unsupported.
    /// <para>几何尺寸越界、未按扇区对齐，或槽位数量不受支持。</para>
    /// </exception>
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
    /// <param name="metadata">
    /// Complete metadata model: geometry plus the partition, extent, group and block-device tables. The
    /// header's minor version is raised automatically when features need it, and its magic, versions,
    /// table descriptors, sizes and checksums are recomputed rather than taken over unchanged.
    /// <para>完整的元数据模型：几何信息以及分区、extent、分组和块设备表。当特性需要时会自动提升头部的次版本号，
    /// 其魔数、版本、表描述符、大小和校验和都会重新计算，而非原样沿用。</para>
    /// </param>
    /// <returns>
    /// Header bytes followed immediately by the table blob, with the SHA-256 of the tables stored in the
    /// header and the header checksum stored at byte offset <c>12</c> of the header.
    /// <para>头部字节后紧跟表数据块；表的 SHA-256 写入头部，头部校验和写入头部的字节偏移 <c>12</c> 处。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The geometry is invalid, the minor version exceeds <c>2</c>, a partition carries an unknown
    /// attribute bit, or the tables do not fit in one metadata slot.
    /// <para>几何信息无效、次版本号超过 <c>2</c>、分区带有未知属性位，或表无法放入单个元数据槽位。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// The combined table sizes exceed the addressable range of the format's 32-bit length fields.
    /// <para>表大小合计超出该格式 32 位长度字段的可表示范围。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Writable, seekable super-image stream positioned anywhere; only the reserved geometry region and
    /// the metadata slots are overwritten, so partition data already present keeps its bytes. The stream
    /// is neither flushed nor closed here and stays owned by the caller.
    /// <para>可写、可寻址的 super 镜像流，起始位置任意；只有保留的几何区域与元数据槽位会被覆盖，
    /// 因此已存在的分区数据保持原字节。此处既不刷新也不关闭该流，其所有权仍属调用方。</para>
    /// </param>
    /// <param name="metadata">
    /// Model whose first block device supplies the device size used to place backup slots.
    /// <para>模型；其第一个块设备提供用于定位备份槽位的设备大小。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The geometry or the block-device layout is invalid, or the metadata declares no super device.
    /// <para>几何信息或块设备布局无效，或元数据未声明 super 设备。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The serialized metadata is larger than the geometry's <c>MetadataMaxSize</c>.
    /// <para>序列化后的元数据大于几何信息的 <c>MetadataMaxSize</c>。</para>
    /// </exception>
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
    /// <param name="path">
    /// Super image path; the file is built in a temporary publication file and only replaces this path
    /// once the layout has been written completely.
    /// <para>super 镜像路径；先在临时发布文件中构建，只有布局完整写入后才会替换该路径。</para>
    /// </param>
    /// <param name="metadata">
    /// Model whose first block device size fixes the resulting file length; bytes of an existing image
    /// are copied up to the shorter of that length and the old file size.
    /// <para>模型；其第一个块设备大小决定结果文件长度；已有镜像的字节会按该长度与旧文件长度中较小者复制。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The geometry, the block-device layout, or the serialized tables are invalid.
    /// <para>几何信息、块设备布局或序列化后的表数据无效。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The serialized metadata is larger than the geometry's <c>MetadataMaxSize</c>.
    /// <para>序列化后的元数据大于几何信息的 <c>MetadataMaxSize</c>。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// The declared block-device size does not fit in a signed 64-bit length.
    /// <para>声明的块设备大小无法放入有符号 64 位长度。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The target path or its temporary publication file cannot be written.
    /// <para>无法写入目标路径或其临时发布文件。</para>
    /// </exception>
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