using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.BlockImage.Lp;

/// <summary>
/// Constants of the dynamic-partition (LP) metadata format: region magics, sector unit and version.
/// <para>动态分区（LP）元数据格式常量：区域魔数、扇区单位与版本。</para>
/// </summary>
public static class MetadataFormat
{
    /// <summary>
    /// Magic identifying an LP metadata geometry block, stored little-endian as the bytes "ld4a".
    /// <para>标识 LP 元数据几何块的魔数，以小端存储为字节序列 "ld4a"。</para>
    /// </summary>
    public const uint LP_METADATA_GEOMETRY_MAGIC = 0x616c4467;

    /// <summary>
    /// Reserved size in bytes of one geometry slot inside the metadata region.
    /// <para>元数据区域内单个几何槽位的保留大小（字节）。</para>
    /// </summary>
    public const int LP_METADATA_GEOMETRY_SIZE = 4096;

    /// <summary>
    /// Magic identifying an LP metadata header, stored little-endian as the bytes "0PLA".
    /// <para>标识 LP 元数据头部的魔数，以小端存储为字节序列 "0PLA"。</para>
    /// </summary>
    public const uint LP_METADATA_HEADER_MAGIC = 0x414C5030;

    /// <summary>
    /// Major metadata version accepted by header serialization.
    /// <para>头部序列化所接受的主元数据版本号。</para>
    /// </summary>
    public const ushort LP_METADATA_MAJOR_VERSION = 10;

    /// <summary>
    /// Sector size in bytes used by extents and block-device measurements.
    /// <para>extent 与块设备计量使用的扇区大小（字节）。</para>
    /// </summary>
    public const int LP_SECTOR_SIZE = 512;

    /// <summary>
    /// Bytes reserved at the start of a super partition image for the metadata region that follows.
    /// <para>super 分区镜像开头为其后元数据区域预留的字节数。</para>
    /// </summary>
    public const int LP_PARTITION_RESERVED_BYTES = 4096;

    /// <summary>
    /// On-disk byte width of the fixed name field shared by the three LP name kinds, namely the
    /// partition name, the partition-group name and the block-device partition name; a
    /// value that does not fit cannot be stored at all and must never be clipped into the field.
    /// <para>LP 三种名称（分区名、分区组名、块设备分区名）共用的定宽名称字段磁盘字节数。
    /// 放不下的值根本无法存储，绝不能再裁剪写入字段。</para>
    /// </summary>
    /// <value>
    /// The 36 bytes each name field occupies: bytes 0..35 of a partition or group entry and bytes
    /// 24..59 of a block-device entry.
    /// <para>每个名称字段占据的 36 字节：分区或组条目的第 0..35 字节，块设备条目的第 24..59 字节。</para>
    /// </value>
    internal const int NameFieldBytes = 36;

    /// <summary>
    /// Extent target type whose target data is a linear sector offset into a block device.
    /// <para>extent 目标类型，其目标数据为块设备内的线性扇区偏移。</para>
    /// </summary>
    public const uint LP_TARGET_TYPE_LINEAR = 0;

    /// <summary>
    /// Extent target type whose sectors read back as zero and carry no target data.
    /// <para>extent 目标类型，其扇区读出为全零且不携带目标数据。</para>
    /// </summary>
    public const uint LP_TARGET_TYPE_ZERO = 1;

    /// <summary>
    /// On-disk byte sizes of each fixed-size LP metadata structure.
    /// <para>各定长 LP 元数据结构在磁盘上的字节大小。</para>
    /// </summary>
    public static class Sizes
    {
        /// <summary>
        /// Byte size of the parsed geometry record.
        /// <para>解析后的几何记录字节大小。</para>
        /// </summary>
        public const int Geometry = 52;

        /// <summary>
        /// Byte size of the metadata header written when the minor version is 2.
        /// <para>次版本为 2 时写入的元数据头部字节大小。</para>
        /// </summary>
        public const int Header = 256;

        /// <summary>Gets the original metadata header size. <para>原始版本元数据头长度。</para></summary>
        public const int LegacyHeader = 128;

        /// <summary>
        /// Byte size of one table descriptor (offset, entry count, entry size).
        /// <para>单个表描述符（偏移、条目数、条目大小）的字节大小。</para>
        /// </summary>
        public const int TableDescriptor = 12;

        /// <summary>
        /// Byte size of one partition table entry.
        /// <para>单个分区表条目的字节大小。</para>
        /// </summary>
        public const int Partition = 52;

        /// <summary>
        /// Byte size of one extent table entry.
        /// <para>单个 extent 表条目的字节大小。</para>
        /// </summary>
        public const int Extent = 24;

        /// <summary>
        /// Byte size of one partition-group table entry.
        /// <para>单个分区组表条目的字节大小。</para>
        /// </summary>
        public const int PartitionGroup = 48;

        /// <summary>
        /// Byte size of one block-device table entry.
        /// <para>单个块设备表条目的字节大小。</para>
        /// </summary>
        public const int BlockDevice = 64;
    }
}

/// <summary>
/// Fixed-size LP metadata geometry record describing the metadata region layout.
/// <para>描述元数据区域布局的定长 LP 元数据几何记录。</para>
/// </summary>
public readonly struct LpMetadataGeometry
{
    /// <summary>
    /// Geometry magic read from bytes 0..3.
    /// <para>从第 0..3 字节读出的几何魔数。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32; <see cref="MetadataFormat.LP_METADATA_GEOMETRY_MAGIC"/> in valid images.
    /// <para>小端 uint32；有效镜像中为 <see cref="MetadataFormat.LP_METADATA_GEOMETRY_MAGIC"/>。</para>
    /// </value>
    public uint Magic { get; init; }

    /// <summary>
    /// Byte size of the geometry structure recorded in the image.
    /// <para>镜像中记录的几何结构字节大小。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 4..7; callers compare it with <see cref="MetadataFormat.Sizes.Geometry"/>.
    /// <para>来自第 4..7 字节的小端 uint32；调用方将其与 <see cref="MetadataFormat.Sizes.Geometry"/> 比较。</para>
    /// </value>
    public uint StructSize { get; init; }

    /// <summary>
    /// Raw 32-byte header checksum stored alongside the geometry.
    /// <para>与几何信息一同存储的原始 32 字节头部校验和。</para>
    /// </summary>
    /// <value>
    /// A fresh copy of bytes 8..39; serialization truncates or zero-pads it to 32 bytes, and <c>null</c> writes zeros.
    /// <para>第 8..39 字节的新副本；序列化时截断或补零到 32 字节，<c>null</c> 写入全零。</para>
    /// </value>
    public byte[] Checksum { get; init; }

    /// <summary>
    /// Maximum size in bytes of the metadata region following the geometry.
    /// <para>几何信息之后元数据区域的最大字节数。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 40..43.
    /// <para>来自第 40..43 字节的小端 uint32。</para>
    /// </value>
    public uint MetadataMaxSize { get; init; }

    /// <summary>
    /// Number of replicated metadata slots described by the geometry.
    /// <para>几何信息描述的元数据冗余槽位数量。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 44..47.
    /// <para>来自第 44..47 字节的小端 uint32。</para>
    /// </value>
    public uint MetadataSlotCount { get; init; }

    /// <summary>
    /// Logical block size used when mapping extents onto the super partition.
    /// <para>将 extent 映射到 super 分区时使用的逻辑块大小。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 48..51.
    /// <para>来自第 48..51 字节的小端 uint32。</para>
    /// </value>
    public uint LogicalBlockSize { get; init; }

    /// <summary>
    /// Parses geometry from binary data (little-endian).
    /// <para>从二进制数据解析几何信息(小端)。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering at least <see cref="MetadataFormat.Sizes.Geometry"/> bytes of the geometry record.
    /// <para>至少覆盖 <see cref="MetadataFormat.Sizes.Geometry"/> 字节几何记录的区域。</para>
    /// </param>
    /// <returns>
    /// The parsed geometry; <see cref="Checksum"/> is a copy of bytes 8..39.
    /// <para>解析后的几何信息；<see cref="Checksum"/> 为第 8..39 字节的副本。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="MetadataFormat.Sizes.Geometry"/> bytes.
    /// <para><paramref name="data"/> 短于 <see cref="MetadataFormat.Sizes.Geometry"/> 字节。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="MetadataFormat.Sizes.Geometry"/>-byte array; <see cref="Checksum"/> is truncated or zero-padded to 32 bytes.
    /// <para>新的 <see cref="MetadataFormat.Sizes.Geometry"/> 字节数组；<see cref="Checksum"/> 会被截断或补零至 32 字节。</para>
    /// </returns>
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
/// Reference to one LP metadata table: byte offset, entry count and entry size.
/// <para>指向单个 LP 元数据表的引用：字节偏移、条目数与条目大小。</para>
/// </summary>
public readonly struct LpMetadataTableDescriptor
{
    /// <summary>
    /// Byte offset of the table relative to the start of the metadata after the header.
    /// <para>表相对于头部之后元数据起始处的字节偏移。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 0..3 of the descriptor.
    /// <para>来自描述符第 0..3 字节的小端 uint32。</para>
    /// </value>
    public uint Offset { get; init; }

    /// <summary>
    /// Number of entries stored in the referenced table.
    /// <para>被引用表中存储的条目数量。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 4..7 of the descriptor.
    /// <para>来自描述符第 4..7 字节的小端 uint32。</para>
    /// </value>
    public uint NumEntries { get; init; }

    /// <summary>
    /// Byte size of a single entry in the referenced table.
    /// <para>被引用表中单个条目的字节大小。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 from bytes 8..11 of the descriptor.
    /// <para>来自描述符第 8..11 字节的小端 uint32。</para>
    /// </value>
    public uint EntrySize { get; init; }

    /// <summary>
    /// Parses a table descriptor from binary data.
    /// <para>从二进制数据解析表描述符。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering at least <see cref="MetadataFormat.Sizes.TableDescriptor"/> bytes; fields are read little-endian.
    /// <para>至少覆盖 <see cref="MetadataFormat.Sizes.TableDescriptor"/> 字节的小端字段区域。</para>
    /// </param>
    /// <returns>
    /// The descriptor with <see cref="Offset"/>, <see cref="NumEntries"/> and <see cref="EntrySize"/> filled in.
    /// <para>已填充 <see cref="Offset"/>、<see cref="NumEntries"/> 与 <see cref="EntrySize"/> 的描述符。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> has fewer than <see cref="MetadataFormat.Sizes.TableDescriptor"/> readable bytes.
    /// <para><paramref name="data"/> 的可读字节少于 <see cref="MetadataFormat.Sizes.TableDescriptor"/> 个。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="MetadataFormat.Sizes.TableDescriptor"/>-byte little-endian array.
    /// <para>新的 <see cref="MetadataFormat.Sizes.TableDescriptor"/> 字节小端数组。</para>
    /// </returns>
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
/// LP metadata header carrying version, checksums and the four table descriptors.
/// <para>携带版本、校验和与四个表描述符的 LP 元数据头部。</para>
/// </summary>
public readonly struct LpMetadataHeader
{
    /// <summary>
    /// Header magic read from bytes 0..3.
    /// <para>从第 0..3 字节读出的头部魔数。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32; <see cref="MetadataFormat.LP_METADATA_HEADER_MAGIC"/> in valid images.
    /// <para>小端 uint32；有效镜像中为 <see cref="MetadataFormat.LP_METADATA_HEADER_MAGIC"/>。</para>
    /// </value>
    public uint Magic { get; init; }

    /// <summary>
    /// Major metadata version read from bytes 4..5.
    /// <para>从第 4..5 字节读出的主元数据版本。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint16; serialization requires <see cref="MetadataFormat.LP_METADATA_MAJOR_VERSION"/>.
    /// <para>小端 uint16；序列化要求等于 <see cref="MetadataFormat.LP_METADATA_MAJOR_VERSION"/>。</para>
    /// </value>
    public ushort MajorVersion { get; init; }

    /// <summary>
    /// Minor metadata version read from bytes 6..7.
    /// <para>从第 6..7 字节读出的次元数据版本。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint16; values 0 and 1 use the legacy header layout, value 2 adds <see cref="Flags"/>.
    /// <para>小端 uint16；0 和 1 使用原始头部布局，2 额外包含 <see cref="Flags"/>。</para>
    /// </value>
    public ushort MinorVersion { get; init; }

    /// <summary>
    /// Header byte size recorded in bytes 8..11.
    /// <para>第 8..11 字节记录的头部字节大小。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32; must equal <see cref="MetadataFormat.Sizes.Header"/> when minor version 2, otherwise <see cref="MetadataFormat.Sizes.LegacyHeader"/>, for serialization to accept it.
    /// <para>小端 uint32；次元数据版本为 2 时必须等于 <see cref="MetadataFormat.Sizes.Header"/>，否则必须等于 <see cref="MetadataFormat.Sizes.LegacyHeader"/>，序列化才会接受。</para>
    /// </value>
    public uint HeaderSize { get; init; }

    /// <summary>
    /// Header checksum stored in bytes 12..43.
    /// <para>存储于第 12..43 字节的头部校验和。</para>
    /// </summary>
    /// <value>
    /// A fresh copy of 32 bytes; serialization truncates or zero-pads it, and <c>null</c> writes zeros.
    /// <para>32 字节的新副本；序列化时截断或补零，<c>null</c> 写入全零。</para>
    /// </value>
    public byte[] HeaderChecksum { get; init; }

    /// <summary>
    /// Total byte size of the metadata tables following the header, read from bytes 44..47.
    /// <para>头部之后元数据表的总字节数，从第 44..47 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32.
    /// <para>小端 uint32。</para>
    /// </value>
    public uint TablesSize { get; init; }

    /// <summary>
    /// Checksum of the metadata tables stored in bytes 48..79.
    /// <para>存储于第 48..79 字节的元数据表校验和。</para>
    /// </summary>
    /// <value>
    /// A fresh copy of 32 bytes; serialization truncates or zero-pads it, and <c>null</c> writes zeros.
    /// <para>32 字节的新副本；序列化时截断或补零，<c>null</c> 写入全零。</para>
    /// </value>
    public byte[] TablesChecksum { get; init; }

    /// <summary>
    /// Descriptor of the partition table, parsed from bytes 80..91.
    /// <para>从第 80..91 字节解析的分区表描述符。</para>
    /// </summary>
    /// <value>
    /// The parsed <see cref="LpMetadataTableDescriptor"/>; its offset is relative to the end of the header.
    /// <para>解析得到的 <see cref="LpMetadataTableDescriptor"/>；其偏移相对于头部末尾。</para>
    /// </value>
    public LpMetadataTableDescriptor Partitions { get; init; }

    /// <summary>
    /// Descriptor of the extent table, parsed from bytes 92..103.
    /// <para>从第 92..103 字节解析的 extent 表描述符。</para>
    /// </summary>
    /// <value>
    /// The parsed <see cref="LpMetadataTableDescriptor"/>; its offset is relative to the end of the header.
    /// <para>解析得到的 <see cref="LpMetadataTableDescriptor"/>；其偏移相对于头部末尾。</para>
    /// </value>
    public LpMetadataTableDescriptor Extents { get; init; }

    /// <summary>
    /// Descriptor of the partition-group table, parsed from bytes 104..115.
    /// <para>从第 104..115 字节解析的分区组表描述符。</para>
    /// </summary>
    /// <value>
    /// The parsed <see cref="LpMetadataTableDescriptor"/>; its offset is relative to the end of the header.
    /// <para>解析得到的 <see cref="LpMetadataTableDescriptor"/>；其偏移相对于头部末尾。</para>
    /// </value>
    public LpMetadataTableDescriptor Groups { get; init; }

    /// <summary>
    /// Descriptor of the block-device table, parsed from bytes 116..127.
    /// <para>从第 116..127 字节解析的块设备表描述符。</para>
    /// </summary>
    /// <value>
    /// The parsed <see cref="LpMetadataTableDescriptor"/>; its offset is relative to the end of the header.
    /// <para>解析得到的 <see cref="LpMetadataTableDescriptor"/>；其偏移相对于头部末尾。</para>
    /// </value>
    public LpMetadataTableDescriptor BlockDevices { get; init; }

    /// <summary>
    /// Header flags read from bytes 128..131 of a version 1.2 header.
    /// <para>从 1.2 版头部第 128..131 字节读出的头部标志。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32, or <c>0</c> when the minor version is below 2, the recorded header size is not 256, or fewer than 256 bytes were supplied.
    /// <para>小端 uint32；当次元数据版本低于 2、记录的头部大小不是 256 或提供的字节不足 256 个时为 <c>0</c>。</para>
    /// </value>
    public uint Flags { get; init; }

    /// <summary>
    /// Parses a header from binary data (little-endian).
    /// <para>从二进制数据解析头部(小端)。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering at least <see cref="MetadataFormat.Sizes.LegacyHeader"/> bytes; bytes past 128 are read only when <see cref="Flags"/> applies.
    /// <para>至少覆盖 <see cref="MetadataFormat.Sizes.LegacyHeader"/> 字节的区域；仅当 <see cref="Flags"/> 适用时才读取第 128 字节之后的内容。</para>
    /// </param>
    /// <returns>
    /// The parsed header; checksums are 32-byte copies and <see cref="Flags"/> is <c>0</c> unless a full 256-byte version 1.2 header was present.
    /// <para>解析后的头部；校验和为 32 字节副本，且在缺少完整 256 字节 1.2 版头部时 <see cref="Flags"/> 为 <c>0</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="MetadataFormat.Sizes.LegacyHeader"/> bytes.
    /// <para><paramref name="data"/> 短于 <see cref="MetadataFormat.Sizes.LegacyHeader"/> 字节。</para>
    /// </exception>
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
    /// <returns>
    /// A new array of <see cref="MetadataFormat.Sizes.Header"/> bytes when the minor version is 2 or <see cref="MetadataFormat.Sizes.LegacyHeader"/> bytes otherwise; trailing bytes past the last written field stay zero.
    /// <para>次元数据版本为 2 时返回新的 <see cref="MetadataFormat.Sizes.Header"/> 字节数组，否则返回 <see cref="MetadataFormat.Sizes.LegacyHeader"/> 字节；最后一个已写字段之后的尾部字节保持为零。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// <see cref="HeaderSize"/> does not match the size implied by <see cref="MinorVersion"/>, <see cref="MajorVersion"/> is not <see cref="MetadataFormat.LP_METADATA_MAJOR_VERSION"/>, or <see cref="MinorVersion"/> is above 2.
    /// <para><see cref="HeaderSize"/> 与 <see cref="MinorVersion"/> 所隐含的大小不一致、<see cref="MajorVersion"/> 不是 <see cref="MetadataFormat.LP_METADATA_MAJOR_VERSION"/>，或 <see cref="MinorVersion"/> 大于 2。</para>
    /// </exception>
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

/// <summary>
/// One entry of the LP partition table: name, attributes and its extent range.
/// <para>LP 分区表中的一个条目：名称、属性及其 extent 范围。</para>
/// </summary>
public readonly struct LpMetadataPartition
{
    /// <summary>
    /// Raw 36-byte name field of the partition entry.
    /// <para>分区条目的原始 36 字节名称字段。</para>
    /// </summary>
    /// <value>
    /// Bytes 0..35 as stored, terminated by the first NUL; serialization rejects arrays longer than
    /// <see cref="MetadataFormat.NameFieldBytes"/>, zero-fills the rest of the field, and writes zeros
    /// for <c>null</c>.
    /// <para>按存储原样的第 0..35 字节，以首个 NUL 结尾；序列化会拒绝长度超过
    /// <see cref="MetadataFormat.NameFieldBytes"/> 的数组，把字段其余部分补零，<c>null</c> 写入全零。</para>
    /// </value>
    public byte[] Name { get; init; }

    /// <summary>
    /// Partition attribute flags read from bytes 36..39.
    /// <para>从第 36..39 字节读出的分区属性标志。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 as stored in the image.
    /// <para>镜像中存储的小端 uint32。</para>
    /// </value>
    public uint Attributes { get; init; }

    /// <summary>
    /// Index of this partition's first extent in the extent table, read from bytes 40..43.
    /// <para>该分区首个 extent 在 extent 表中的索引，从第 40..43 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32; the partition owns <see cref="NumExtents"/> entries starting here.
    /// <para>小端 uint32；该分区从此处起拥有 <see cref="NumExtents"/> 个条目。</para>
    /// </value>
    public uint FirstExtentIndex { get; init; }

    /// <summary>
    /// Number of extent entries belonging to this partition, read from bytes 44..47.
    /// <para>属于该分区的 extent 条目数量，从第 44..47 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32; <c>0</c> means the partition has no extents.
    /// <para>小端 uint32；<c>0</c> 表示分区没有 extent。</para>
    /// </value>
    public uint NumExtents { get; init; }

    /// <summary>
    /// Partition group index within the super, read from bytes 48..51.
    /// <para>分区组在 super 内的索引，从第 48..51 字节读出。</para>
    /// </summary>
    /// <value>
    /// Zero-based ordinal into the group table as a little-endian uint32.
    /// <para>指向分区组表的小端 uint32 从零开始的序号。</para>
    /// </value>
    public uint GroupIndex { get; init; }

    /// <summary>
    /// Partition name decoded from the raw <see cref="Name"/> bytes.
    /// <para>从原始 <see cref="Name"/> 字节解码出的分区名。</para>
    /// </summary>
    /// <value>
    /// UTF-8 text up to the first NUL byte; empty when <see cref="Name"/> is <c>null</c> or starts with NUL.
    /// <para>截至首个 NUL 字节的 UTF-8 文本；当 <see cref="Name"/> 为 <c>null</c> 或以 NUL 开头时为空字符串。</para>
    /// </value>
    public string NameString => ReadName(Name);

    /// <summary>
    /// Parses a partition entry from binary data.
    /// <para>从二进制数据解析分区条目。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering one <see cref="MetadataFormat.Sizes.Partition"/>-byte entry; fields are read little-endian.
    /// <para>覆盖一个 <see cref="MetadataFormat.Sizes.Partition"/> 字节条目的区域；字段按小端读取。</para>
    /// </param>
    /// <returns>
    /// The parsed entry with <see cref="Name"/> as a fresh 36-byte copy.
    /// <para>解析后的条目，<see cref="Name"/> 为新的 36 字节副本。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="data"/> is shorter than 36 bytes, so the name slice is out of range.
    /// <para><paramref name="data"/> 短于 36 字节，名称切片越界。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> has 36 to 51 bytes, so a trailing uint32 read is too short.
    /// <para><paramref name="data"/> 为 36 至 51 字节，末尾的 uint32 读取长度不足。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="MetadataFormat.Sizes.Partition"/>-byte little-endian array with the name field zero-padded.
    /// <para>新的 <see cref="MetadataFormat.Sizes.Partition"/> 字节小端数组，名称字段以零填充。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// <see cref="Name"/> is longer than <see cref="MetadataFormat.NameFieldBytes"/> bytes, which the
    /// fixed name field cannot hold.
    /// <para><see cref="Name"/> 的字节数大于 <see cref="MetadataFormat.NameFieldBytes"/>，定宽名称字段无法容纳。</para>
    /// </exception>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.Partition];
        WriteName(data.AsSpan(0, MetadataFormat.NameFieldBytes), Name, "partition name");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), Attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), FirstExtentIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), NumExtents);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), GroupIndex);
        return data;
    }

    internal static string ReadName(byte[]? nameBytes)
        => nameBytes is null ? string.Empty : NulText.ReadCString(nameBytes, Encoding.UTF8);

    /// <summary>
    /// Writes one LP name into the bytes of its fixed field, failing instead of clipping when the value
    /// does not fit: a clipped name would silently rename a partition on the device, and a
    /// value wider than the field would also overwrite the adjacent flags or index. The caller passes a
    /// destination slice that starts at the field, and <see cref="MetadataFormat.NameFieldBytes"/> stays
    /// the single authority on the accepted width for all three name kinds.
    /// <para>把一个 LP 名称写入其定宽字节的字段；值放不下时直接失败而不是裁剪——裁剪后的名称会在设备上
    /// 静默改名，超出字段宽度的值还会覆盖相邻的标志或索引字段。调用方传入从该字段起始的目标切片，而
    /// <see cref="MetadataFormat.NameFieldBytes"/> 始终是三种名称共用的唯一宽度依据。</para>
    /// </summary>
    /// <param name="dest">
    /// The name-field window of the entry record, starting at that field's first byte.
    /// <para>条目记录中的名称字段窗口，从该字段的首字节开始。</para>
    /// </param>
    /// <param name="name">
    /// The already encoded name bytes, or <c>null</c> to leave the field zero-filled.
    /// <para>已编码的名称字节；为 <c>null</c> 时字段保持全零。</para>
    /// </param>
    /// <param name="field">
    /// The field label quoted in the failure message.
    /// <para>失败消息中引用的字段标签。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// <paramref name="name"/> has more bytes than <see cref="MetadataFormat.NameFieldBytes"/>.
    /// <para><paramref name="name"/> 的字节数大于 <see cref="MetadataFormat.NameFieldBytes"/>。</para>
    /// </exception>
    internal static void WriteName(Span<byte> dest, byte[]? name, string field)
    {
        dest.Clear();
        if (name is null)
        {
            return;
        }
        if (name.Length > MetadataFormat.NameFieldBytes)
        {
            throw new InvalidDataException(
                $"LP {field} is not representable: {name.Length} UTF-8 bytes exceed the {MetadataFormat.NameFieldBytes}-byte name field: '{Encoding.UTF8.GetString(name)}'.");
        }
        name.CopyTo(dest);
    }
}

/// <summary>
/// One extent entry mapping a run of logical sectors onto a block device or zeros.
/// <para>将一段逻辑扇区映射到块设备或零数据的 extent 条目。</para>
/// </summary>
public readonly struct LpMetadataExtent
{
    /// <summary>
    /// Size of this extent in sectors, read from bytes 0..7.
    /// <para>该 extent 的扇区大小，从第 0..7 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint64; the logical span is a multiple of <see cref="MetadataFormat.LP_SECTOR_SIZE"/> bytes.
    /// <para>小端 uint64；逻辑跨度为 <see cref="MetadataFormat.LP_SECTOR_SIZE"/> 字节的整数倍。</para>
    /// </value>
    public ulong NumSectors { get; init; }

    /// <summary>
    /// Target kind of this extent, read from bytes 8..11.
    /// <para>该 extent 的目标类型，从第 8..11 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32, expected to be <see cref="MetadataFormat.LP_TARGET_TYPE_LINEAR"/> or <see cref="MetadataFormat.LP_TARGET_TYPE_ZERO"/>.
    /// <para>小端 uint32，预期为 <see cref="MetadataFormat.LP_TARGET_TYPE_LINEAR"/> 或 <see cref="MetadataFormat.LP_TARGET_TYPE_ZERO"/>。</para>
    /// </value>
    public uint TargetType { get; init; }

    /// <summary>
    /// Target payload read from the unaligned offset 12..19.
    /// <para>从未对齐的偏移 12..19 读出的目标数据。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint64: a sector offset into <see cref="TargetSource"/>'s block device for linear extents; unused for zero extents.
    /// <para>小端 uint64：linear 类型 extent 在 <see cref="TargetSource"/> 块设备内的扇区偏移；zero 类型不使用。</para>
    /// </value>
    public ulong TargetData { get; init; }

    /// <summary>
    /// Index of the block device this extent targets, read from bytes 20..23.
    /// <para>该 extent 目标块设备的索引，从第 20..23 字节读出。</para>
    /// </summary>
    /// <value>
    /// Zero-based ordinal into the block-device table as a little-endian uint32.
    /// <para>指向块设备表的小端 uint32 从零开始的序号。</para>
    /// </value>
    public uint TargetSource { get; init; }

    /// <summary>
    /// Parses an extent entry from binary data.
    /// <para>从二进制数据解析 extent 条目。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering one <see cref="MetadataFormat.Sizes.Extent"/>-byte entry; fields are read little-endian, including the uint64 at the unaligned offset 12.
    /// <para>覆盖一个 <see cref="MetadataFormat.Sizes.Extent"/> 字节条目的区域；字段按小端读取，包括非对齐偏移 12 处的 uint64。</para>
    /// </param>
    /// <returns>
    /// The parsed extent entry.
    /// <para>解析后的 extent 条目。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="MetadataFormat.Sizes.Extent"/> bytes, so a field read is too short.
    /// <para><paramref name="data"/> 短于 <see cref="MetadataFormat.Sizes.Extent"/> 字节，字段读取长度不足。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="MetadataFormat.Sizes.Extent"/>-byte little-endian array.
    /// <para>新的 <see cref="MetadataFormat.Sizes.Extent"/> 字节小端数组。</para>
    /// </returns>
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
/// One entry of the LP partition-group table: name, flags and maximum size.
/// <para>LP 分区组表中的一个条目：名称、标志与最大大小。</para>
/// </summary>
public readonly struct LpMetadataPartitionGroup
{
    /// <summary>
    /// Raw 36-byte name field of the group entry.
    /// <para>分区组条目的原始 36 字节名称字段。</para>
    /// </summary>
    /// <value>
    /// Bytes 0..35 as stored, terminated by the first NUL; serialization rejects arrays longer than
    /// <see cref="MetadataFormat.NameFieldBytes"/>, zero-fills the rest of the field, and writes zeros
    /// for <c>null</c>.
    /// <para>按存储原样的第 0..35 字节，以首个 NUL 结尾；序列化会拒绝长度超过
    /// <see cref="MetadataFormat.NameFieldBytes"/> 的数组，把字段其余部分补零，<c>null</c> 写入全零。</para>
    /// </value>
    public byte[] Name { get; init; }

    /// <summary>
    /// Group flags read from bytes 36..39.
    /// <para>从第 36..39 字节读出的分区组标志。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 as stored in the image.
    /// <para>镜像中存储的小端 uint32。</para>
    /// </value>
    public uint Flags { get; init; }

    /// <summary>
    /// Maximum combined size of the group's partitions, read from bytes 40..47.
    /// <para>组内分区合计大小的上限，从第 40..47 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint64 in bytes taken from the image; the parser applies no upper bound to it.
    /// <para>取自镜像的以字节表示的小端 uint64；解析器不对其施加上限。</para>
    /// </value>
    public ulong MaximumSize { get; init; }

    /// <summary>
    /// Group name decoded from the raw <see cref="Name"/> bytes.
    /// <para>从原始 <see cref="Name"/> 字节解码出的分区组名。</para>
    /// </summary>
    /// <value>
    /// UTF-8 text up to the first NUL byte; empty when <see cref="Name"/> is <c>null</c> or starts with NUL.
    /// <para>截至首个 NUL 字节的 UTF-8 文本；当 <see cref="Name"/> 为 <c>null</c> 或以 NUL 开头时为空字符串。</para>
    /// </value>
    public string NameString => LpMetadataPartition.ReadName(Name);

    /// <summary>
    /// Parses a partition group entry from binary data.
    /// <para>从二进制数据解析分区组条目。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering one <see cref="MetadataFormat.Sizes.PartitionGroup"/>-byte entry; fields are read little-endian.
    /// <para>覆盖一个 <see cref="MetadataFormat.Sizes.PartitionGroup"/> 字节条目的区域；字段按小端读取。</para>
    /// </param>
    /// <returns>
    /// The parsed group entry with <see cref="Name"/> as a fresh 36-byte copy.
    /// <para>解析后的分区组条目，<see cref="Name"/> 为新的 36 字节副本。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="data"/> is shorter than 36 bytes, so the name slice is out of range.
    /// <para><paramref name="data"/> 短于 36 字节，名称切片越界。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> has 36 to 47 bytes, so a trailing field read is too short.
    /// <para><paramref name="data"/> 为 36 至 47 字节，末尾字段读取长度不足。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="MetadataFormat.Sizes.PartitionGroup"/>-byte little-endian array with the name field zero-padded.
    /// <para>新的 <see cref="MetadataFormat.Sizes.PartitionGroup"/> 字节小端数组，名称字段以零填充。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// <see cref="Name"/> is longer than <see cref="MetadataFormat.NameFieldBytes"/> bytes, which the
    /// fixed name field cannot hold.
    /// <para><see cref="Name"/> 的字节数大于 <see cref="MetadataFormat.NameFieldBytes"/>，定宽名称字段无法容纳。</para>
    /// </exception>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.PartitionGroup];
        LpMetadataPartition.WriteName(data.AsSpan(0, MetadataFormat.NameFieldBytes), Name, "group name");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(40), MaximumSize);
        return data;
    }
}

/// <summary>
/// One entry of the LP block-device table describing a physical partition that backs the super.
/// <para>LP 块设备表中的一个条目，描述支撑 super 的物理分区。</para>
/// </summary>
public readonly struct LpMetadataBlockDevice
{
    /// <summary>
    /// First logical sector of the super on this block device, read from bytes 0..7.
    /// <para>super 在该块设备上的首个逻辑扇区，从第 0..7 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint64 counted in <see cref="MetadataFormat.LP_SECTOR_SIZE"/>-byte sectors.
    /// <para>以 <see cref="MetadataFormat.LP_SECTOR_SIZE"/> 字节扇区为单位计数的小端 uint64。</para>
    /// </value>
    public ulong FirstLogicalSector { get; init; }

    /// <summary>
    /// Required alignment of extent starts, read from bytes 8..11.
    /// <para>extent 起始所要求的对齐值，从第 8..11 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 in sectors; <c>1</c> means no alignment constraint beyond sector granularity.
    /// <para>以扇区为单位的小端 uint32；<c>1</c> 表示除扇区粒度外没有对齐约束。</para>
    /// </value>
    public uint Alignment { get; init; }

    /// <summary>
    /// Group-aligned offset applied to alignment checks, read from bytes 12..15.
    /// <para>用于对齐检查的组对齐偏移，从第 12..15 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 in sectors.
    /// <para>以扇区为单位的小端 uint32。</para>
    /// </value>
    public uint AlignmentOffset { get; init; }

    /// <summary>
    /// Size of the block device, read from bytes 16..23.
    /// <para>块设备的大小，从第 16..23 字节读出。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint64 in bytes taken from the image; the parser applies no upper bound to it.
    /// <para>取自镜像的以字节表示的小端 uint64；解析器不对其施加上限。</para>
    /// </value>
    public ulong Size { get; init; }

    /// <summary>
    /// Raw 36-byte name of the physical partition backing this block device, stored at bytes 24..59.
    /// <para>支撑该块设备的物理分区的原始 36 字节名称，存储于第 24..59 字节。</para>
    /// </summary>
    /// <value>
    /// A fresh 36-byte copy, terminated by the first NUL; serialization rejects arrays longer than
    /// <see cref="MetadataFormat.NameFieldBytes"/>, zero-fills the rest of the field, and writes zeros
    /// for <c>null</c>.
    /// <para>新的 36 字节副本，以首个 NUL 结尾；序列化会拒绝长度超过
    /// <see cref="MetadataFormat.NameFieldBytes"/> 的数组，把字段其余部分补零，<c>null</c> 写入全零。</para>
    /// </value>
    public byte[] PartitionName { get; init; }

    /// <summary>
    /// Block-device flags read from bytes 60..63.
    /// <para>从第 60..63 字节读出的块设备标志。</para>
    /// </summary>
    /// <value>
    /// Little-endian uint32 as stored in the image; <c>0</c> means no flags set.
    /// <para>镜像中存储的小端 uint32；<c>0</c> 表示未设置任何标志。</para>
    /// </value>
    public uint Flags { get; init; }

    /// <summary>
    /// Parses a block device entry from binary data.
    /// <para>从二进制数据解析块设备条目。</para>
    /// </summary>
    /// <param name="data">
    /// Span covering one <see cref="MetadataFormat.Sizes.BlockDevice"/>-byte entry; fields are read little-endian.
    /// <para>覆盖一个 <see cref="MetadataFormat.Sizes.BlockDevice"/> 字节条目的区域；字段按小端读取。</para>
    /// </param>
    /// <returns>
    /// The parsed block-device entry with <see cref="PartitionName"/> as a fresh 36-byte copy.
    /// <para>解析后的块设备条目，<see cref="PartitionName"/> 为新的 36 字节副本。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="MetadataFormat.Sizes.BlockDevice"/> bytes, so a field read or the name slice is too short.
    /// <para><paramref name="data"/> 短于 <see cref="MetadataFormat.Sizes.BlockDevice"/> 字节，字段读取或名称切片长度不足。</para>
    /// </exception>
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
    /// <returns>
    /// A new <see cref="MetadataFormat.Sizes.BlockDevice"/>-byte little-endian array with the partition-name field zero-padded.
    /// <para>新的 <see cref="MetadataFormat.Sizes.BlockDevice"/> 字节小端数组，分区名字段以零填充。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// <see cref="PartitionName"/> is longer than <see cref="MetadataFormat.NameFieldBytes"/> bytes,
    /// which the fixed name field cannot hold.
    /// <para><see cref="PartitionName"/> 的字节数大于 <see cref="MetadataFormat.NameFieldBytes"/>，定宽名称字段无法容纳。</para>
    /// </exception>
    public byte[] ToBytes()
    {
        var data = new byte[MetadataFormat.Sizes.BlockDevice];
        BinaryPrimitives.WriteUInt64LittleEndian(data, FirstLogicalSector);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), Alignment);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), AlignmentOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), Size);
        LpMetadataPartition.WriteName(data.AsSpan(24, MetadataFormat.NameFieldBytes), PartitionName, "block-device partition name");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60), Flags);
        return data;
    }
}