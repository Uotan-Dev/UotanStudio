using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Lp;

/// <summary>
/// Constants and binary layouts for the Android Logical Partition (LP) metadata format.
/// <para>Android 逻辑分区(LP)元数据格式的常量与二进制布局。</para>
/// </summary>
public static class MetadataFormat
{
    /// <summary>
    /// Magic number for <see cref="LpMetadataGeometry"/>.
    /// <para>LpMetadataGeometry 的魔数。</para>
    /// </summary>
    public const uint LP_METADATA_GEOMETRY_MAGIC = 0x616c4467;

    /// <summary>
    /// Space reserved for geometry information, in bytes.
    /// <para>为几何信息保留的空间(字节)。</para>
    /// </summary>
    public const int LP_METADATA_GEOMETRY_SIZE = 4096;

    /// <summary>
    /// Magic number for <see cref="LpMetadataHeader"/>.
    /// <para>LpMetadataHeader 的魔数。</para>
    /// </summary>
    public const uint LP_METADATA_HEADER_MAGIC = 0x414C5030;

    /// <summary>
    /// Major metadata version.
    /// <para>元数据主版本。</para>
    /// </summary>
    public const ushort LP_METADATA_MAJOR_VERSION = 10;

    /// <summary>
    /// Size of a standard disk sector in bytes.
    /// <para>标准磁盘扇区大小(字节)。</para>
    /// </summary>
    public const int LP_SECTOR_SIZE = 512;

    /// <summary>
    /// Bytes reserved at the beginning of the super partition for the partition table.
    /// <para>super 分区起始处为分区表保留的字节数。</para>
    /// </summary>
    public const int LP_PARTITION_RESERVED_BYTES = 4096;

    /// <summary>
    /// Linear target type for an extent.
    /// <para>extent 的线性目标类型。</para>
    /// </summary>
    public const uint LP_TARGET_TYPE_LINEAR = 0;

    /// <summary>
    /// Zero-fill target type for an extent.
    /// <para>extent 的零填充目标类型。</para>
    /// </summary>
    public const uint LP_TARGET_TYPE_ZERO = 1;

    /// <summary>
    /// Sizes of the fixed binary structures in bytes.
    /// <para>固定二进制结构的大小(字节)。</para>
    /// </summary>
    public static class Sizes
    {
        /// <summary>Geometry struct size (52 bytes). <para>几何结构大小(52 字节)。</para></summary>
        public const int Geometry = 52;

        /// <summary>Header struct size (256 bytes). <para>头部结构大小(256 字节)。</para></summary>
        public const int Header = 256;

        /// <summary>Table descriptor size (12 bytes). <para>表描述符大小(12 字节)。</para></summary>
        public const int TableDescriptor = 12;

        /// <summary>Partition entry size (52 bytes). <para>分区条目大小(52 字节)。</para></summary>
        public const int Partition = 52;

        /// <summary>Extent entry size (24 bytes). <para>extent 条目大小(24 字节)。</para></summary>
        public const int Extent = 24;

        /// <summary>Partition group entry size (48 bytes). <para>分区组条目大小(48 字节)。</para></summary>
        public const int PartitionGroup = 48;

        /// <summary>Block device entry size (64 bytes). <para>块设备条目大小(64 字节)。</para></summary>
        public const int BlockDevice = 64;
    }
}

/// <summary>
/// Describes the global layout of the metadata area on disk.
/// <para>描述磁盘上元数据区域的全局布局。</para>
/// </summary>
public readonly struct LpMetadataGeometry
{
    /// <summary>Magic number. <para>魔数。</para></summary>
    public uint Magic { get; init; }

    /// <summary>Size of this structure. <para>此结构的大小。</para></summary>
    public uint StructSize { get; init; }

    /// <summary>SHA256 checksum of the geometry block. <para>几何块的 SHA256 校验和。</para></summary>
    public byte[] Checksum { get; init; }

    /// <summary>Maximum size allocated for metadata blocks. <para>为元数据块分配的最大大小。</para></summary>
    public uint MetadataMaxSize { get; init; }

    /// <summary>Number of metadata slots available. <para>可用的元数据槽数量。</para></summary>
    public uint MetadataSlotCount { get; init; }

    /// <summary>Logical block size of the underlying device. <para>底层设备的逻辑块大小。</para></summary>
    public uint LogicalBlockSize { get; init; }

    /// <summary>
    /// Parses geometry from binary data (little-endian).
    /// <para>从二进制数据解析几何信息(小端)。</para>
    /// </summary>
    public static LpMetadataGeometry FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < MetadataFormat.Sizes.Geometry
            ? throw new ArgumentException("Data too small for LpMetadataGeometry")
            : new LpMetadataGeometry
            {
                Magic = BinaryPrimitives.ReadUInt32LittleEndian(data),
                StructSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                Checksum = data.Slice(8, 32).ToArray(),
                MetadataMaxSize = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
                MetadataSlotCount = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]),
                LogicalBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(data[48..])
            };
    }

    /// <summary>
    /// Serializes the geometry to binary (little-endian).
    /// <para>将几何信息序列化为二进制(小端)。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.Geometry];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), StructSize);
        (Checksum ?? []).AsSpan(0, Math.Min(32, (Checksum ?? []).Length)).CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), MetadataMaxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), MetadataSlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), LogicalBlockSize);
        return data;
    }
}

/// <summary>
/// Describes the location and size of a table within the metadata area.
/// <para>描述元数据区域内表的位置和大小。</para>
/// </summary>
public readonly struct LpMetadataTableDescriptor
{
    /// <summary>Offset relative to the table data start. <para>相对表数据起始的偏移。</para></summary>
    public uint Offset { get; init; }

    /// <summary>Number of entries in the table. <para>表中的条目数。</para></summary>
    public uint NumEntries { get; init; }

    /// <summary>Size of each entry in bytes. <para>每个条目的字节大小。</para></summary>
    public uint EntrySize { get; init; }

    /// <summary>
    /// Parses a table descriptor from binary data.
    /// <para>从二进制数据解析表描述符。</para>
    /// </summary>
    public static LpMetadataTableDescriptor FromBytes(ReadOnlySpan<byte> data)
    {
        return new LpMetadataTableDescriptor
        {
            Offset = BinaryPrimitives.ReadUInt32LittleEndian(data),
            NumEntries = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            EntrySize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..])
        };
    }

    /// <summary>
    /// Serializes the table descriptor to binary.
    /// <para>将表描述符序列化为二进制。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.TableDescriptor];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), NumEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), EntrySize);
        return data;
    }
}

/// <summary>
/// Header of a metadata block.
/// <para>元数据块的头部。</para>
/// </summary>
public readonly struct LpMetadataHeader
{
    /// <summary>Magic number. <para>魔数。</para></summary>
    public uint Magic { get; init; }

    /// <summary>Major version number. <para>主版本号。</para></summary>
    public ushort MajorVersion { get; init; }

    /// <summary>Minor version number. <para>次版本号。</para></summary>
    public ushort MinorVersion { get; init; }

    /// <summary>Size of the header itself. <para>头部自身大小。</para></summary>
    public uint HeaderSize { get; init; }

    /// <summary>SHA256 checksum of the header. <para>头部的 SHA256 校验和。</para></summary>
    public byte[] HeaderChecksum { get; init; }

    /// <summary>Size of all metadata tables linked by this header. <para>此头部关联的所有元数据表的大小。</para></summary>
    public uint TablesSize { get; init; }

    /// <summary>SHA256 checksum of the tables area. <para>表区域的 SHA256 校验和。</para></summary>
    public byte[] TablesChecksum { get; init; }

    /// <summary>Descriptor for the partitions table. <para>分区表描述符。</para></summary>
    public LpMetadataTableDescriptor Partitions { get; init; }

    /// <summary>Descriptor for the extents table. <para>extent 表描述符。</para></summary>
    public LpMetadataTableDescriptor Extents { get; init; }

    /// <summary>Descriptor for the groups table. <para>组表描述符。</para></summary>
    public LpMetadataTableDescriptor Groups { get; init; }

    /// <summary>Descriptor for the block devices table. <para>块设备表描述符。</para></summary>
    public LpMetadataTableDescriptor BlockDevices { get; init; }

    /// <summary>Metadata header flags. <para>元数据头部标志。</para></summary>
    public uint Flags { get; init; }

    /// <summary>
    /// Parses a header from binary data (little-endian).
    /// <para>从二进制数据解析头部(小端)。</para>
    /// </summary>
    public static LpMetadataHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < MetadataFormat.Sizes.Header
            ? throw new ArgumentException("Data too small for LpMetadataHeader")
            : new LpMetadataHeader
            {
                Magic = BinaryPrimitives.ReadUInt32LittleEndian(data),
                MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
                MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
                HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
                HeaderChecksum = data.Slice(12, 32).ToArray(),
                TablesSize = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]),
                TablesChecksum = data.Slice(48, 32).ToArray(),
                Partitions = LpMetadataTableDescriptor.FromBytes(data[80..]),
                Extents = LpMetadataTableDescriptor.FromBytes(data[92..]),
                Groups = LpMetadataTableDescriptor.FromBytes(data[104..]),
                BlockDevices = LpMetadataTableDescriptor.FromBytes(data[116..]),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[128..])
            };
    }

    /// <summary>
    /// Serializes the header to binary (little-endian).
    /// <para>将头部序列化为二进制(小端)。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.Header];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), HeaderSize);
        (HeaderChecksum ?? []).AsSpan(0, Math.Min(32, (HeaderChecksum ?? []).Length)).CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), TablesSize);
        (TablesChecksum ?? []).AsSpan(0, Math.Min(32, (TablesChecksum ?? []).Length)).CopyTo(data.AsSpan(48));
        Partitions.ToBytes().CopyTo(data.AsSpan(80));
        Extents.ToBytes().CopyTo(data.AsSpan(92));
        Groups.ToBytes().CopyTo(data.AsSpan(104));
        BlockDevices.ToBytes().CopyTo(data.AsSpan(116));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(128), Flags);
        return data;
    }
}

/// <summary>
/// Describes a single logical partition.
/// <para>描述单个逻辑分区。</para>
/// </summary>
public readonly struct LpMetadataPartition
{
    /// <summary>Partition name (UTF-8, up to 36 bytes). <para>分区名称(UTF-8,最多 36 字节)。</para></summary>
    public byte[] Name { get; init; }

    /// <summary>Attributes flags. <para>属性标志。</para></summary>
    public uint Attributes { get; init; }

    /// <summary>Index of the first extent in the extents table. <para>extent 表中第一个 extent 的索引。</para></summary>
    public uint FirstExtentIndex { get; init; }

    /// <summary>Total number of extents for this partition. <para>此分区的 extent 总数。</para></summary>
    public uint NumExtents { get; init; }

    /// <summary>Index of the group this partition belongs to. <para>此分区所属组的索引。</para></summary>
    public uint GroupIndex { get; init; }

    /// <summary>
    /// Gets the partition name as a string.
    /// <para>以字符串形式获取分区名称。</para>
    /// </summary>
    public string NameString => ReadName(Name);

    /// <summary>
    /// Parses a partition entry from binary data.
    /// <para>从二进制数据解析分区条目。</para>
    /// </summary>
    public static LpMetadataPartition FromBytes(ReadOnlySpan<byte> data)
    {
        return new LpMetadataPartition
        {
            Name = data[..36].ToArray(),
            Attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]),
            FirstExtentIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
            NumExtents = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]),
            GroupIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[48..])
        };
    }

    /// <summary>
    /// Serializes the partition entry to binary.
    /// <para>将分区条目序列化为二进制。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.Partition];
        WriteName(data, Name);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), Attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), FirstExtentIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), NumExtents);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), GroupIndex);
        return data;
    }

    internal static string ReadName(byte[]? nameBytes)
    {
        if (nameBytes is null)
        {
            return string.Empty;
        }
        var len = 0;
        while (len < nameBytes.Length && nameBytes[len] != 0)
        {
            len++;
        }
        return Encoding.UTF8.GetString(nameBytes, 0, len);
    }

    internal static void WriteName(Span<byte> dest, byte[]? name)
    {
        dest.Clear();
        if (name is null)
        {
            return;
        }
        name.AsSpan(0, Math.Min(name.Length, dest.Length)).CopyTo(dest);
    }
}

/// <summary>
/// Describes a contiguous segment of blocks.
/// <para>描述一块连续的块段。</para>
/// </summary>
public readonly struct LpMetadataExtent
{
    /// <summary>Total number of sectors for this extent. <para>此 extent 的总扇区数。</para></summary>
    public ulong NumSectors { get; init; }

    /// <summary>Type of target (0 = Linear, 1 = Zero). <para>目标类型(0 = 线性,1 = 零)。</para></summary>
    public uint TargetType { get; init; }

    /// <summary>Starting sector on the block device. <para>块设备上的起始扇区。</para></summary>
    public ulong TargetData { get; init; }

    /// <summary>Index of the source block device. <para>源块设备的索引。</para></summary>
    public uint TargetSource { get; init; }

    /// <summary>
    /// Parses an extent entry from binary data.
    /// <para>从二进制数据解析 extent 条目。</para>
    /// </summary>
    public static LpMetadataExtent FromBytes(ReadOnlySpan<byte> data)
    {
        return new LpMetadataExtent
        {
            NumSectors = BinaryPrimitives.ReadUInt64LittleEndian(data),
            TargetType = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            TargetData = BinaryPrimitives.ReadUInt64LittleEndian(data[12..]),
            TargetSource = BinaryPrimitives.ReadUInt32LittleEndian(data[20..])
        };
    }

    /// <summary>
    /// Serializes the extent entry to binary.
    /// <para>将 extent 条目序列化为二进制。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.Extent];
        BinaryPrimitives.WriteUInt64LittleEndian(data, NumSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), TargetType);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(12), TargetData);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), TargetSource);
        return data;
    }
}

/// <summary>
/// Describes a partition group that limits the total size of its partitions.
/// <para>描述限制其分区总大小的分区组。</para>
/// </summary>
public readonly struct LpMetadataPartitionGroup
{
    /// <summary>Group name (UTF-8, up to 36 bytes). <para>组名称(UTF-8,最多 36 字节)。</para></summary>
    public byte[] Name { get; init; }

    /// <summary>Group flags. <para>组标志。</para></summary>
    public uint Flags { get; init; }

    /// <summary>Maximum size of all partitions in this group in bytes. <para>此组全部分区的最大大小(字节)。</para></summary>
    public ulong MaximumSize { get; init; }

    /// <summary>
    /// Gets the group name as a string.
    /// <para>以字符串形式获取组名称。</para>
    /// </summary>
    public string NameString => LpMetadataPartition.ReadName(Name);

    /// <summary>
    /// Parses a partition group entry from binary data.
    /// <para>从二进制数据解析分区组条目。</para>
    /// </summary>
    public static LpMetadataPartitionGroup FromBytes(ReadOnlySpan<byte> data)
    {
        return new LpMetadataPartitionGroup
        {
            Name = data[..36].ToArray(),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]),
            MaximumSize = BinaryPrimitives.ReadUInt64LittleEndian(data[40..])
        };
    }

    /// <summary>
    /// Serializes the partition group entry to binary.
    /// <para>将分区组条目序列化为二进制。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.PartitionGroup];
        LpMetadataPartition.WriteName(data, Name);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(40), MaximumSize);
        return data;
    }
}

/// <summary>
/// Describes a block device used by the logical partition layout.
/// <para>描述逻辑分区布局使用的块设备。</para>
/// </summary>
public readonly struct LpMetadataBlockDevice
{
    /// <summary>First sector that can be used for logical partitions. <para>可用于逻辑分区的第一个扇区。</para></summary>
    public ulong FirstLogicalSector { get; init; }

    /// <summary>Alignment requirement in bytes. <para>对齐要求(字节)。</para></summary>
    public uint Alignment { get; init; }

    /// <summary>Offset relative to the first sector to maintain alignment. <para>相对第一个扇区保持对齐的偏移。</para></summary>
    public uint AlignmentOffset { get; init; }

    /// <summary>Total size of the block device in bytes. <para>块设备总大小(字节)。</para></summary>
    public ulong Size { get; init; }

    /// <summary>Name of the partition backing this block device. <para>支撑此块设备的分区名称。</para></summary>
    public byte[] PartitionName { get; init; }

    /// <summary>Device flags. <para>设备标志。</para></summary>
    public uint Flags { get; init; }

    /// <summary>
    /// Parses a block device entry from binary data.
    /// <para>从二进制数据解析块设备条目。</para>
    /// </summary>
    public static LpMetadataBlockDevice FromBytes(ReadOnlySpan<byte> data)
    {
        return new LpMetadataBlockDevice
        {
            FirstLogicalSector = BinaryPrimitives.ReadUInt64LittleEndian(data),
            Alignment = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            AlignmentOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            Size = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            PartitionName = data.Slice(24, 36).ToArray(),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data[60..])
        };
    }

    /// <summary>
    /// Serializes the block device entry to binary.
    /// <para>将块设备条目序列化为二进制。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.BlockDevice];
        BinaryPrimitives.WriteUInt64LittleEndian(data, FirstLogicalSector);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), Alignment);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), AlignmentOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), Size);
        LpMetadataPartition.WriteName(data.AsSpan(24), PartitionName);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60), Flags);
        return data;
    }
}