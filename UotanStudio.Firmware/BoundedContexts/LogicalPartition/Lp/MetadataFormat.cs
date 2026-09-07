using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

public static class MetadataFormat
{
    public const uint LP_METADATA_GEOMETRY_MAGIC = 0x616c4467;

    public const int LP_METADATA_GEOMETRY_SIZE = 4096;

    public const uint LP_METADATA_HEADER_MAGIC = 0x414C5030;

    public const ushort LP_METADATA_MAJOR_VERSION = 10;

    public const int LP_SECTOR_SIZE = 512;

    public const int LP_PARTITION_RESERVED_BYTES = 4096;

    public const uint LP_TARGET_TYPE_LINEAR = 0;

    public const uint LP_TARGET_TYPE_ZERO = 1;

    public static class Sizes
    {
        public const int Geometry = 52;

        public const int Header = 256;

        /// <summary>Gets the original metadata header size. <para>原始版本元数据头长度。</para></summary>
        public const int LegacyHeader = 128;

        public const int TableDescriptor = 12;

        public const int Partition = 52;

        public const int Extent = 24;

        public const int PartitionGroup = 48;

        public const int BlockDevice = 64;
    }
}

public readonly struct LpMetadataGeometry
{
    public uint Magic { get; init; }

    public uint StructSize { get; init; }

    public byte[] Checksum { get; init; }

    public uint MetadataMaxSize { get; init; }

    public uint MetadataSlotCount { get; init; }

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

public readonly struct LpMetadataTableDescriptor
{
    public uint Offset { get; init; }

    public uint NumEntries { get; init; }

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

public readonly struct LpMetadataHeader
{
    public uint Magic { get; init; }

    public ushort MajorVersion { get; init; }

    public ushort MinorVersion { get; init; }

    public uint HeaderSize { get; init; }

    public byte[] HeaderChecksum { get; init; }

    public uint TablesSize { get; init; }

    public byte[] TablesChecksum { get; init; }

    public LpMetadataTableDescriptor Partitions { get; init; }

    public LpMetadataTableDescriptor Extents { get; init; }

    public LpMetadataTableDescriptor Groups { get; init; }

    public LpMetadataTableDescriptor BlockDevices { get; init; }

    public uint Flags { get; init; }

    /// <summary>
    /// Parses a header from binary data (little-endian).
    /// <para>从二进制数据解析头部(小端)。</para>
    /// </summary>
    public static LpMetadataHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < MetadataFormat.Sizes.LegacyHeader
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
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) >= 2
                    && BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == MetadataFormat.Sizes.Header
                    && data.Length >= MetadataFormat.Sizes.Header
                        ? BinaryPrimitives.ReadUInt32LittleEndian(data[128..]) : 0
            };
    }

    /// <summary>
    /// Serializes the header to binary (little-endian).
    /// <para>将头部序列化为二进制(小端)。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var size = MinorVersion >= 2 ? MetadataFormat.Sizes.Header : MetadataFormat.Sizes.LegacyHeader;
        if (HeaderSize != size || MajorVersion != MetadataFormat.LP_METADATA_MAJOR_VERSION || MinorVersion > 2)
        {
            throw new InvalidDataException("Unsupported LP metadata header version or size.");
        }
        var data = new byte[size];
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
        if (size == MetadataFormat.Sizes.Header)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(128), Flags);
        }
        return data;
    }
}

public readonly struct LpMetadataPartition
{
    public byte[] Name { get; init; }

    public uint Attributes { get; init; }

    public uint FirstExtentIndex { get; init; }

    public uint NumExtents { get; init; }

    public uint GroupIndex { get; init; }

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

public readonly struct LpMetadataExtent
{
    public ulong NumSectors { get; init; }

    public uint TargetType { get; init; }

    public ulong TargetData { get; init; }

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

public readonly struct LpMetadataPartitionGroup
{
    public byte[] Name { get; init; }

    public uint Flags { get; init; }

    public ulong MaximumSize { get; init; }

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

public readonly struct LpMetadataBlockDevice
{
    public ulong FirstLogicalSector { get; init; }

    public uint Alignment { get; init; }

    public uint AlignmentOffset { get; init; }

    public ulong Size { get; init; }

    public byte[] PartitionName { get; init; }

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