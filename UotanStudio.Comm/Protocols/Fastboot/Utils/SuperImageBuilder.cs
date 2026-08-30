using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Fastboot.Utils;

/// <summary>
/// Parsed <c>LpMetadataGeometry</c> from a super partition image (magic "gDla").
/// <para>从 super 分区镜像解析的 <c>LpMetadataGeometry</c>（魔数 "gDla"）。</para>
/// </summary>
public sealed class LpGeometry
{
    /// <summary>
    /// Size of the on-disk <c>LpMetadataGeometry</c> struct in bytes.
    /// <para><c>LpMetadataGeometry</c> 结构在磁盘上的大小（字节）。</para>
    /// </summary>
    public const int StructSizeBytes = 52;

    /// <summary>Gets the reported struct size from the blob. 从数据块中读取的报告结构大小。</summary>
    public uint StructSize { get; }

    /// <summary>Gets the 32-byte SHA256 checksum covering the first 20 bytes of the struct. 覆盖结构前 20 字节的 32 字节 SHA256 校验和。</summary>
    public byte[] Checksum { get; }

    /// <summary>Gets the maximum size of a metadata slot in bytes. 元数据槽位的最大大小（字节）。</summary>
    public uint MetadataMaxSize { get; }

    /// <summary>Gets the number of metadata slots. 元数据槽位数量。</summary>
    public uint MetadataSlotCount { get; }

    /// <summary>Gets the logical block size in bytes. 逻辑块大小（字节）。</summary>
    public uint LogicalBlockSize { get; }

    /// <summary>
    /// Gets whether the stored checksum matches the recomputed SHA256 over the first 20 bytes
    /// (with the checksum field zeroed). Only computed when <see cref="StructSize"/> is the
    /// standard 52 bytes.
    /// <para>存储的校验和是否与对前 20 字节（校验和字段清零）重新计算的 SHA256 一致。
    /// 仅在 <see cref="StructSize"/> 为标准 52 字节时计算。</para>
    /// </summary>
    public bool IsChecksumValid { get; }

    private LpGeometry(uint structSize, byte[] checksum, uint metadataMaxSize, uint metadataSlotCount, uint logicalBlockSize, bool isChecksumValid)
    {
        StructSize = structSize;
        Checksum = checksum;
        MetadataMaxSize = metadataMaxSize;
        MetadataSlotCount = metadataSlotCount;
        LogicalBlockSize = logicalBlockSize;
        IsChecksumValid = isChecksumValid;
    }

    /// <summary>
    /// Attempts to parse an LP geometry from <paramref name="data"/> at <paramref name="offset"/>.
    /// <para>尝试从 <paramref name="data"/> 的 <paramref name="offset"/> 处解析 LP geometry。</para>
    /// </summary>
    /// <param name="data">The raw super image data. 原始 super 镜像数据。</param>
    /// <param name="offset">The offset of the geometry struct. geometry 结构的偏移。</param>
    /// <returns>The parsed geometry, or <c>null</c> when the magic does not match or the data is too short. 解析出的 geometry；魔数不匹配或数据过短时返回 <c>null</c>。</returns>
    public static LpGeometry? TryParse(byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + StructSizeBytes > data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> s = data.AsSpan(offset, StructSizeBytes);
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != SuperImageBuilder.GeometryMagic)
        {
            return null;
        }

        var structSize = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4, 4));
        var checksum = s.Slice(8, 32).ToArray();
        var metadataMaxSize = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(40, 4));
        var metadataSlotCount = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(44, 4));
        var logicalBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(48, 4));

        var checksumValid = false;
        if (structSize == StructSizeBytes)
        {
            // liblp hashes the first 20 bytes of the struct (magic + struct_size + the still
            // zeroed checksum field) and stores the result in the checksum field.
            // liblp 对结构前 20 字节（magic + struct_size + 仍为零的校验和字段）求哈希，
            // 并将结果存入校验和字段。
            var checksumInput = new byte[20];
            s[..20].CopyTo(checksumInput);
            Array.Clear(checksumInput, 8, 12);
            checksumValid = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(checksumInput), checksum);
        }

        return new LpGeometry(
            structSize, checksum, metadataMaxSize, metadataSlotCount, logicalBlockSize, checksumValid);
    }
}

/// <summary>
/// A logical partition read from a super partition's LP metadata.
/// <para>从 super 分区的 LP 元数据读取的逻辑分区。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperLogicalPartition"/> class.
/// <para>初始化 <see cref="SuperLogicalPartition"/> 类的新实例。</para>
/// </remarks>
public sealed class SuperLogicalPartition(
    string name, uint groupIndex, string? groupName, uint attributes,
    uint firstExtentIndex, uint numExtents, long sizeInBytes)
{
    /// <summary>Gets the logical partition name. 逻辑分区名称。</summary>
    public string Name { get; } = name;

    /// <summary>Gets the index of the partition group. 分区组索引。</summary>
    public uint GroupIndex { get; } = groupIndex;

    /// <summary>Gets the partition group name, when resolvable. 分区组名称（可解析时）。</summary>
    public string? GroupName { get; } = groupName;

    /// <summary>Gets the partition attributes flags. 分区属性标志。</summary>
    public uint Attributes { get; } = attributes;

    /// <summary>Gets the index of the first extent. 首个 extent 的索引。</summary>
    public uint FirstExtentIndex { get; } = firstExtentIndex;

    /// <summary>Gets the number of extents. extent 数量。</summary>
    public uint NumExtents { get; } = numExtents;

    /// <summary>Gets the total partition size in bytes (sum of extent sectors × 512). 分区总大小（字节）（extent 扇区数之和 × 512）。</summary>
    public long SizeInBytes { get; } = sizeInBytes;

    /// <summary>
    /// Returns a string representation of the logical partition.
    /// <para>返回逻辑分区的字符串表示。</para>
    /// </summary>
    /// <returns>A string describing the partition. 描述分区的字符串。</returns>
    public override string ToString() =>
        $"SuperLogicalPartition[Name={Name}, Group={GroupName ?? GroupIndex.ToString()}, Size={SizeInBytes} bytes]";
}

/// <summary>
/// A single extent of a logical partition, describing a contiguous run of 512-byte sectors.
/// <para>逻辑分区的单个 extent，描述一段连续的 512 字节扇区。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperImageExtent"/> class.
/// <para>初始化 <see cref="SuperImageExtent"/> 类的新实例。</para>
/// </remarks>
public sealed class SuperImageExtent(
    ulong numSectors, uint targetType, ulong targetData, uint targetSource)
{
    /// <summary>Gets the number of 512-byte sectors in this extent. 此 extent 的 512 字节扇区数。</summary>
    public ulong NumSectors { get; } = numSectors;

    /// <summary>Gets the target type: <see cref="SuperImageBuilder.LinearTargetType"/> or
    /// <see cref="SuperImageBuilder.ZeroTargetType"/>. 目标类型：<see cref="SuperImageBuilder.LinearTargetType"/>
    /// 或 <see cref="SuperImageBuilder.ZeroTargetType"/>。</summary>
    public uint TargetType { get; } = targetType;

    /// <summary>Gets the starting sector, relative to the first logical sector of the block device.
    /// 起始扇区（相对块设备的首个逻辑扇区）。</summary>
    public ulong TargetData { get; } = targetData;

    /// <summary>Gets the source block device index. 源块设备索引。</summary>
    public uint TargetSource { get; } = targetSource;
}

/// <summary>
/// A block device described by LP metadata, locating the logical partition data area.
/// <para>LP 元数据描述的块设备，定位逻辑分区数据区。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperImageBlockDevice"/> class.
/// <para>初始化 <see cref="SuperImageBlockDevice"/> 类的新实例。</para>
/// </remarks>
public sealed class SuperImageBlockDevice(
    ulong firstLogicalSector, uint alignment, uint alignmentOffset, ulong size,
    string partitionName, uint flags)
{
    /// <summary>Gets the first sector available for logical partitions. 可用于逻辑分区的首个扇区。</summary>
    public ulong FirstLogicalSector { get; } = firstLogicalSector;

    /// <summary>Gets the alignment requirement in bytes. 对齐要求（字节）。</summary>
    public uint Alignment { get; } = alignment;

    /// <summary>Gets the alignment offset relative to the first sector. 相对首个扇区的对齐偏移。</summary>
    public uint AlignmentOffset { get; } = alignmentOffset;

    /// <summary>Gets the total size of the block device in bytes. 块设备总大小（字节）。</summary>
    public ulong Size { get; } = size;

    /// <summary>Gets the backing partition name. 支撑该块设备的分区名称。</summary>
    public string PartitionName { get; } = partitionName;

    /// <summary>Gets the device flags. 设备标志。</summary>
    public uint Flags { get; } = flags;
}

/// <summary>
/// A partition specification used to build a super image with <see cref="SuperImageBuilder.Build"/>.
/// <para>用于通过 <see cref="SuperImageBuilder.Build"/> 构建 super 镜像的分区规格。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperBuildPartition"/> class.
/// <para>初始化 <see cref="SuperBuildPartition"/> 类的新实例。</para>
/// </remarks>
public sealed class SuperBuildPartition(string name, string groupName, long sizeInBytes, uint attributes = 0)
{
    /// <summary>Gets the logical partition name. 逻辑分区名称。</summary>
    public string Name { get; } = name;

    /// <summary>Gets the partition group name. 分区组名称。</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Gets the partition size in bytes. 分区大小（字节）。</summary>
    public long SizeInBytes { get; } = sizeInBytes;

    /// <summary>Gets the partition attributes flags. 分区属性标志。</summary>
    public uint Attributes { get; } = attributes;
}

/// <summary>
/// Parsed LP metadata (header + tables + partitions + extents) for one metadata slot of a super image.
/// <para>super 镜像某个元数据槽位的解析结果（头 + 表 + 分区）。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperImageMetadata"/> class.
/// <para>初始化 <see cref="SuperImageMetadata"/> 类的新实例。</para>
/// </remarks>
public sealed class SuperImageMetadata(
    LpGeometry geometry, int slot, uint majorVersion, uint minorVersion,
    uint headerSize, uint metadataSize, uint metadataMaxSize, uint metadataSlotCount,
    IReadOnlyList<SuperLogicalPartition> partitions,
    IReadOnlyList<SuperImageExtent> extents,
    IReadOnlyList<SuperImageBlockDevice> blockDevices,
    IReadOnlyList<string> groupNames)
{
    /// <summary>Gets the parsed geometry. 解析出的 geometry。</summary>
    public LpGeometry Geometry { get; } = geometry;

    /// <summary>Gets the metadata slot index this blob was read from. 读取该数据块的元数据槽位索引。</summary>
    public int Slot { get; } = slot;

    /// <summary>Gets the LP metadata major version. LP 元数据主版本号。</summary>
    public uint MajorVersion { get; } = majorVersion;

    /// <summary>Gets the LP metadata minor version. LP 元数据次版本号。</summary>
    public uint MinorVersion { get; } = minorVersion;

    /// <summary>Gets the header size in bytes. 头大小（字节）。</summary>
    public uint HeaderSize { get; } = headerSize;

    /// <summary>Gets the actual metadata size in bytes. 实际元数据大小（字节）。</summary>
    public uint MetadataSize { get; } = metadataSize;

    /// <summary>Gets the maximum metadata slot size in bytes. 元数据槽位最大大小（字节）。</summary>
    public uint MetadataMaxSize { get; } = metadataMaxSize;

    /// <summary>Gets the metadata slot count. 元数据槽位数量。</summary>
    public uint MetadataSlotCount { get; } = metadataSlotCount;

    /// <summary>Gets the logical partitions described by this metadata slot. 该槽位描述的逻辑分区。</summary>
    public IReadOnlyList<SuperLogicalPartition> Partitions { get; } = partitions;

    /// <summary>Gets all extents referenced by the partitions, indexed by extent index.
    /// 分区引用的全部 extent（按 extent 索引排列）。</summary>
    public IReadOnlyList<SuperImageExtent> Extents { get; } = extents;

    /// <summary>Gets the block devices described by this metadata slot. 该槽位描述的块设备。</summary>
    public IReadOnlyList<SuperImageBlockDevice> BlockDevices { get; } = blockDevices;

    /// <summary>Gets the partition group names, indexed by group index. 按组索引排列的分区组名称。</summary>
    public IReadOnlyList<string> GroupNames { get; } = groupNames;
}

/// <summary>
/// Reads and builds super partition images: LP geometry / metadata header parsing, logical
/// partition enumeration, and self-contained LP metadata + super image construction that is
/// readable back through <see cref="ReadMetadata"/>.
/// <para>读取与构建 super 分区镜像：LP geometry / 元数据头解析、逻辑分区枚举，以及可被
/// <see cref="ReadMetadata"/> 回读的自包含 LP 元数据与 super 镜像构建。</para>
/// </summary>
public sealed class SuperImageBuilder
{
    /// <summary>
    /// LP geometry magic ("gDla" when read little-endian), <c>LP_METADATA_GEOMETRY_MAGIC</c>.
    /// <para>LP geometry 魔数（小端读取为 "gDla"），即 <c>LP_METADATA_GEOMETRY_MAGIC</c>。</para>
    /// </summary>
    public const uint GeometryMagic = 0x616C4467;

    /// <summary>
    /// LP metadata header magic ("0PLA" when read little-endian), <c>LP_METADATA_HEADER_MAGIC</c>.
    /// <para>LP 元数据头魔数（小端读取为 "0PLA"），即 <c>LP_METADATA_HEADER_MAGIC</c>。</para>
    /// </summary>
    public const uint MetadataMagic = 0x414C5030;

    /// <summary>
    /// Linear extent target type: data stored within the super device.
    /// <para>线性 extent 目标类型：数据存储于 super 设备内。</para>
    /// </summary>
    public const uint LinearTargetType = 0;

    /// <summary>
    /// Zero-fill extent target type.
    /// <para>零填充 extent 目标类型。</para>
    /// </summary>
    public const uint ZeroTargetType = 1;

    /// <summary>
    /// Size of one geometry block in bytes; the primary geometry lives at offset 0 and the
    /// backup copy at offset <see cref="GeometryBlockSize"/>.
    /// <para>单个 geometry 块的大小（字节）；主 geometry 位于偏移 0，备份副本位于偏移
    /// <see cref="GeometryBlockSize"/>。</para>
    /// </summary>
    public const int GeometryBlockSize = 4096;

    /// <summary>
    /// liblp sector size in bytes; all extents are measured in 512-byte sectors.
    /// <para>liblp 扇区大小（字节）；所有 extent 以 512 字节扇区计量。</para>
    /// </summary>
    public const int LpSectorSize = 512;

    /// <summary>
    /// Offset of the metadata area within a built image: two geometry blocks precede it.
    /// <para>构建镜像中元数据区的偏移：前面是两个 geometry 块。</para>
    /// </summary>
    private const int MetadataAreaStart = 2 * GeometryBlockSize;

    /// <summary>Size of the LP metadata header region written by <see cref="Build"/>.
    /// <see cref="Build"/> 写入的 LP 元数据头区域大小（字节）。</summary>
    private const int MetadataHeaderSize = 256;

    /// <summary>Size of one table descriptor entry in bytes. 单个表描述符条目的大小（字节）。</summary>
    private const int TableDescriptorBytes = 12;

    /// <summary>
    /// Number of fixed bytes before the variable-length UTF-8 name in a partition table entry.
    /// <para>分区表条目中变长 UTF-8 名称之前的固定字节数。</para>
    /// </summary>
    private const int PartitionEntryHeaderBytes = 24;

    /// <summary>Size of one extent table entry in bytes. 单个 extent 表条目的大小（字节）。</summary>
    private const int ExtentEntryBytes = 24;

    /// <summary>
    /// Number of fixed bytes before the variable-length UTF-8 name in a group table entry.
    /// <para>组表条目中变长 UTF-8 名称之前的固定字节数。</para>
    /// </summary>
    private const int GroupEntryHeaderBytes = 16;

    /// <summary>Size of one block device table entry in bytes. 单个块设备表条目的大小（字节）。</summary>
    private const int BlockDeviceEntryBytes = 64;

    /// <summary>Maximum UTF-8 byte length of partition and group names. 分区名与组名的最大 UTF-8 字节长度。</summary>
    private const int MaxNameBytes = 36;

    /// <summary>LP metadata major version written by <see cref="Build"/>.
    /// <see cref="Build"/> 写入的 LP 元数据主版本号。</summary>
    private const uint MetadataMajorVersion = 10;

    /// <summary>LP metadata minor version written by <see cref="Build"/>.
    /// <see cref="Build"/> 写入的 LP 元数据次版本号。</summary>
    private const uint MetadataMinorVersion = 0;

    /// <summary>
    /// Returns whether the byte array is a super partition image (LP geometry magic present).
    /// <para>返回字节数组是否为 super 分区镜像（存在 LP geometry 魔数）。</para>
    /// </summary>
    /// <param name="data">The raw image data. 原始镜像数据。</param>
    /// <returns><c>true</c> when the primary or backup geometry magic matches. 主或备份 geometry 魔数匹配时返回 <c>true</c>。</returns>
    public bool IsSuperImage(byte[] data) => data != null && (LpGeometry.TryParse(data, 0) != null || LpGeometry.TryParse(data, GeometryBlockSize) != null);

    /// <summary>
    /// Returns whether the file is a super partition image.
    /// <para>返回文件是否为 super 分区镜像。</para>
    /// </summary>
    /// <param name="superImagePath">The super image file path. super 镜像文件路径。</param>
    /// <returns><c>true</c> when the file exists and contains a valid geometry magic. 文件存在且含有效 geometry 魔数时返回 <c>true</c>。</returns>
    public bool IsSuperImage(string superImagePath)
    {
        if (!File.Exists(superImagePath))
        {
            return false;
        }

        try
        {
            return IsSuperImage(File.ReadAllBytes(superImagePath));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads and parses the LP metadata for the specified slot from a raw super image.
    /// <para>从原始 super 镜像读取并解析指定槽位的 LP 元数据。</para>
    /// </summary>
    /// <param name="imageData">The raw super partition image data. 原始 super 分区镜像数据。</param>
    /// <param name="slot">The metadata slot index (0-based). 元数据槽位索引（从 0 开始）。</param>
    /// <returns>The parsed metadata, or <c>null</c> when the image or slot is invalid. 解析出的元数据；镜像或槽位无效时返回 <c>null</c>。</returns>
    public SuperImageMetadata? ReadMetadata(byte[] imageData, int slot = 0)
    {
        if (imageData == null || slot < 0)
        {
            return null;
        }

        var geometry = LpGeometry.TryParse(imageData, 0) ?? LpGeometry.TryParse(imageData, GeometryBlockSize);
        if (geometry == null || geometry.MetadataSlotCount == 0)
        {
            return null;
        }

        if (slot >= geometry.MetadataSlotCount)
        {
            return null;
        }

        var metadataStart = (2L * GeometryBlockSize) + (slot * geometry.MetadataMaxSize);
        if (metadataStart + 32 > imageData.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> blob = imageData.AsSpan((int)metadataStart);
        if (BinaryPrimitives.ReadUInt32LittleEndian(blob) != MetadataMagic)
        {
            return null;
        }

        var majorVersion = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4, 4));
        var minorVersion = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8, 4));
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12, 4));
        var metadataSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16, 4));
        var metadataMaxSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20, 4));
        var metadataSlotCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(24, 4));

        if (headerSize < 28 || headerSize > blob.Length)
        {
            return null;
        }

        // Bound the usable region by the reported metadata size and the bytes available.
        // 以报告的实际元数据大小与可用字节为界，限制可用区域。
        var available = Math.Min(metadataSize, blob.Length);
        if (available < headerSize)
        {
            return null;
        }

        var partitionsTable = ReadTable(blob, headerSize + 0, available);
        var extentsTable = ReadTable(blob, headerSize + 12, available);
        var groupsTable = ReadTable(blob, headerSize + 24, available);
        if (partitionsTable == null || extentsTable == null || groupsTable == null)
        {
            return null;
        }

        var blockDevicesTable = ReadTable(blob, headerSize + 36, available);
        var extents = ReadExtents(blob, extentsTable.Value.Offset, extentsTable.Value.NumEntries, available);
        var groupNames = ReadGroupNames(blob, groupsTable.Value.Offset, groupsTable.Value.NumEntries, available);
        var blockDevices = blockDevicesTable == null
            ? []
            : ReadBlockDevices(blob, blockDevicesTable.Value.Offset, blockDevicesTable.Value.NumEntries, available);
        var partitions = ReadPartitions(
            blob, partitionsTable.Value.Offset, partitionsTable.Value.NumEntries,
            extents, groupNames, available);

        return new SuperImageMetadata(
            geometry, slot, majorVersion, minorVersion,
            headerSize, (uint)available, metadataMaxSize, metadataSlotCount,
            partitions, extents, blockDevices, groupNames);
    }

    /// <summary>
    /// Enumerates the logical partitions described by the specified metadata slot of a super
    /// image file.
    /// <para>枚举 super 镜像文件指定元数据槽位描述的逻辑分区。</para>
    /// </summary>
    /// <param name="superImagePath">The super image file path. super 镜像文件路径。</param>
    /// <param name="slot">The metadata slot index (0-based). 元数据槽位索引（从 0 开始）。</param>
    /// <returns>The logical partitions. 逻辑分区列表。</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist. 文件不存在时抛出。</exception>
    /// <exception cref="IOException">Thrown when the file cannot be read. 文件无法读取时抛出。</exception>
    public IReadOnlyList<SuperLogicalPartition> GetLogicalPartitions(string superImagePath, int slot = 0)
    {
        if (string.IsNullOrWhiteSpace(superImagePath))
        {
            throw new ArgumentException("Super image path is required. super 镜像路径必填。", nameof(superImagePath));
        }

        if (!File.Exists(superImagePath))
        {
            throw new FileNotFoundException($"Super image file not found: {superImagePath} super 镜像文件不存在。", superImagePath);
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(superImagePath);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new IOException($"Failed to read super image file: {superImagePath} 读取 super 镜像文件失败。", ex);
        }

        return GetLogicalPartitions(data, slot);
    }

    /// <summary>
    /// Enumerates the logical partitions described by the specified metadata slot of a raw
    /// super image.
    /// <para>枚举原始 super 镜像指定元数据槽位描述的逻辑分区。</para>
    /// </summary>
    /// <param name="imageData">The raw super partition image data. 原始 super 分区镜像数据。</param>
    /// <param name="slot">The metadata slot index (0-based). 元数据槽位索引（从 0 开始）。</param>
    /// <returns>The logical partitions. 逻辑分区列表。</returns>
    public IReadOnlyList<SuperLogicalPartition> GetLogicalPartitions(byte[] imageData, int slot = 0)
    {
        var metadata = ReadMetadata(imageData, slot);
        return metadata?.Partitions ?? [];
    }

    /// <summary>
    /// Builds a super partition image (LP metadata + zero-filled logical partition extents)
    /// that can be read back with <see cref="ReadMetadata"/>.
    /// <para>构建可被 <see cref="ReadMetadata"/> 回读的 super 分区镜像（LP 元数据 + 零填充的
    /// 逻辑分区 extent）。</para>
    /// </summary>
    /// <param name="partitions">The logical partitions to describe, in extent order.
    /// 要描述的逻辑分区（按 extent 顺序）。</param>
    /// <param name="deviceSize">The super device size in bytes. super 设备大小（字节）。</param>
    /// <param name="metadataMaxSize">The metadata slot size in bytes. 元数据槽位大小（字节）。</param>
    /// <param name="metadataSlotCount">The number of metadata slots (primary + backup copies).
    /// 元数据槽位数量（主/备份副本）。</param>
    /// <param name="blockSize">The logical block size in bytes (power of two, multiple of 512).
    /// 逻辑块大小（字节，512 的二次幂倍数）。</param>
    /// <returns>The complete super image bytes. 完整的 super 镜像字节。</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partitions"/> is null.
    /// <paramref name="partitions"/> 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="deviceSize"/>,
    /// <paramref name="metadataMaxSize"/>, <paramref name="metadataSlotCount"/>,
    /// <paramref name="blockSize"/> or a partition size is invalid.
    /// 任一布局参数或分区大小无效时抛出。</exception>
    /// <exception cref="ArgumentException">Thrown when a partition name or group name is empty or
    /// too long. 分区名称或组名称为空或过长时抛出。</exception>
    /// <exception cref="InvalidOperationException">Thrown when the serialized metadata exceeds
    /// <paramref name="metadataMaxSize"/>. 序列化元数据超过 <paramref name="metadataMaxSize"/> 时抛出。</exception>
    public byte[] Build(
        IReadOnlyList<SuperBuildPartition> partitions,
        uint deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        uint blockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (deviceSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceSize), "Super device size must be positive. super 设备大小必须为正。");
        }

        if (metadataSlotCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadataSlotCount), "At least one metadata slot is required. 至少需要一个元数据槽位。");
        }

        if (metadataMaxSize < MetadataHeaderSize + (4 * TableDescriptorBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(metadataMaxSize), "Metadata slot size is too small for an LP metadata header. 元数据槽位大小过小，无法容纳 LP 元数据头。");
        }

        if (blockSize == 0 || blockSize % LpSectorSize != 0 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a non-zero power of two that is a multiple of 512. 块大小必须是非零且为 512 的整数倍的二次幂。");
        }

        // Group list: "default" first, then every referenced group in order of first appearance.
        // 组列表："default" 在最前，其余按首次出现顺序排列。
        var groupNames = BuildGroupNames(partitions);
        var groupIndexes = new Dictionary<string, uint>(groupNames.Count, StringComparer.Ordinal);
        for (var i = 0; i < groupNames.Count; i++)
        {
            groupIndexes[groupNames[i]] = (uint)i;
        }

        // Assign one linear extent per partition, starting at the first logical sector.
        // 为每个分区分配一个线性 extent，从首个逻辑扇区起连续排布。
        var extents = new (ulong NumSectors, ulong TargetData)[partitions.Count];
        ulong nextSector = 0;
        for (var i = 0; i < partitions.Count; i++)
        {
            var partition = partitions[i];
            var nameBytes = Encoding.UTF8.GetBytes(partition.Name);
            if (nameBytes.Length == 0 || nameBytes.Length > MaxNameBytes)
            {
                throw new ArgumentException($"Partition name must be 1..{MaxNameBytes} UTF-8 bytes. 分区名称必须是 1..{MaxNameBytes} 个 UTF-8 字节。", nameof(partitions));
            }

            if (partition.SizeInBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(partitions), $"Partition '{partition.Name}' has a negative size. 分区 '{partition.Name}' 大小为负。");
            }

            var sectors = SectorCount(partition.SizeInBytes);
            extents[i] = (sectors, nextSector);
            nextSector = checked(nextSector + sectors);
        }

        // Image layout: two geometry blocks, primary metadata slots, logical data area, then
        // backup metadata slots at the end of the device.
        // 镜像布局：两个 geometry 块、主元数据槽位、逻辑数据区，设备末尾为备份元数据槽位。
        var metadataAreaBytes = checked((ulong)metadataMaxSize * metadataSlotCount);
        var firstLogicalByte = AlignUp((ulong)MetadataAreaStart + metadataAreaBytes, blockSize);
        var firstLogicalSector = firstLogicalByte / LpSectorSize;
        var dataBytes = checked(nextSector * LpSectorSize);
        var backupStart = checked((ulong)deviceSize - metadataAreaBytes);
        if (firstLogicalByte + dataBytes > backupStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceSize),
                $"Super device size {deviceSize} is too small; metadata and logical partitions need at least {firstLogicalByte + dataBytes + metadataAreaBytes} bytes. super 设备大小 {deviceSize} 过小；元数据与逻辑分区至少需要 {firstLogicalByte + dataBytes + metadataAreaBytes} 字节。");
        }

        var metadataBlob = BuildMetadataBlob(
            partitions, groupIndexes, groupNames, extents,
            firstLogicalSector, deviceSize, metadataMaxSize, metadataSlotCount, blockSize);

        var image = new byte[deviceSize];
        WriteGeometry(image, 0, metadataMaxSize, metadataSlotCount, blockSize);
        WriteGeometry(image, GeometryBlockSize, metadataMaxSize, metadataSlotCount, blockSize);

        for (uint slot = 0; slot < metadataSlotCount; slot++)
        {
            metadataBlob.CopyTo(image, checked((int)(MetadataAreaStart + (slot * metadataMaxSize))));
            metadataBlob.CopyTo(image, checked((int)(backupStart + (slot * metadataMaxSize))));
        }

        return image;
    }

    private static List<string> BuildGroupNames(IReadOnlyList<SuperBuildPartition> partitions)
    {
        var groups = new List<string> { "default" };
        foreach (var partition in partitions)
        {
            var name = GroupOf(partition.GroupName);
            if (!groups.Contains(name, StringComparer.Ordinal))
            {
                groups.Add(name);
            }
        }

        return groups;
    }

    private static string GroupOf(string? groupName) =>
        string.IsNullOrWhiteSpace(groupName) ? "default" : groupName;

    private static ulong SectorCount(long sizeInBytes) =>
        (ulong)((sizeInBytes + LpSectorSize - 1L) / LpSectorSize);

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private byte[] BuildMetadataBlob(
        IReadOnlyList<SuperBuildPartition> partitions,
        IReadOnlyDictionary<string, uint> groupIndexes,
        IReadOnlyList<string> groupNames,
        IReadOnlyList<(ulong NumSectors, ulong TargetData)> extents,
        ulong firstLogicalSector,
        uint deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        uint blockSize)
    {
        long partitionsTableLength = 0;
        foreach (var partition in partitions)
        {
            partitionsTableLength = checked(partitionsTableLength + PartitionEntryHeaderBytes + Encoding.UTF8.GetByteCount(partition.Name));
        }

        long groupsTableLength = 0;
        foreach (var name in groupNames)
        {
            groupsTableLength = checked(groupsTableLength + GroupEntryHeaderBytes + Encoding.UTF8.GetByteCount(name));
        }

        var extentsTableLength = checked(extents.Count * (long)ExtentEntryBytes);
        long tablesStart = checked(MetadataHeaderSize + (4 * TableDescriptorBytes));
        var partitionsTableOffset = tablesStart;
        var extentsTableOffset = checked(partitionsTableOffset + partitionsTableLength);
        var groupsTableOffset = checked(extentsTableOffset + extentsTableLength);
        var blockDevicesTableOffset = checked(groupsTableOffset + groupsTableLength);
        var metadataSize = checked(blockDevicesTableOffset + BlockDeviceEntryBytes);
        if (metadataSize > metadataMaxSize)
        {
            throw new InvalidOperationException(
                $"Serialized LP metadata ({metadataSize} bytes) exceeds the metadata slot size ({metadataMaxSize} bytes). 序列化的 LP 元数据（{metadataSize} 字节）超过元数据槽位大小（{metadataMaxSize} 字节）。");
        }

        var blob = new byte[metadataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, MetadataMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), MetadataMajorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), MetadataMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), (uint)MetadataHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(16), (uint)metadataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(20), metadataMaxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(24), metadataSlotCount);

        WriteTableDescriptor(blob, MetadataHeaderSize + 0, partitionsTableOffset, (uint)partitions.Count, PartitionEntryHeaderBytes);
        WriteTableDescriptor(blob, MetadataHeaderSize + 12, extentsTableOffset, (uint)extents.Count, ExtentEntryBytes);
        WriteTableDescriptor(blob, MetadataHeaderSize + 24, groupsTableOffset, (uint)groupNames.Count, GroupEntryHeaderBytes);
        WriteTableDescriptor(blob, MetadataHeaderSize + 36, blockDevicesTableOffset, 1, BlockDeviceEntryBytes);

        // Partitions table: variable-length entries (fixed header + UTF-8 name).
        // 分区表：变长条目（固定头 + UTF-8 名称）。
        long cursor = partitionsTableOffset;
        for (var i = 0; i < partitions.Count; i++)
        {
            var partition = partitions[i];
            var nameBytes = Encoding.UTF8.GetBytes(partition.Name);
            var span = blob.AsSpan((int)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), partition.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), groupIndexes[GroupOf(partition.GroupName)]);
            nameBytes.CopyTo(span.Slice(PartitionEntryHeaderBytes));
            cursor = checked(cursor + PartitionEntryHeaderBytes + nameBytes.Length);
        }

        // Extents table: fixed 24-byte entries.
        // extent 表：固定 24 字节条目。
        cursor = extentsTableOffset;
        for (var i = 0; i < extents.Count; i++)
        {
            var span = blob.AsSpan((int)cursor);
            BinaryPrimitives.WriteUInt64LittleEndian(span, extents[i].NumSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), LinearTargetType);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(12), extents[i].TargetData);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), 0);
            cursor = checked(cursor + ExtentEntryBytes);
        }

        // Groups table: variable-length entries (fixed header + UTF-8 name).
        // 组表：变长条目（固定头 + UTF-8 名称）。
        cursor = groupsTableOffset;
        foreach (var name in groupNames)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var span = blob.AsSpan((int)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)nameBytes.Length);
            nameBytes.CopyTo(span.Slice(GroupEntryHeaderBytes));
            cursor = checked(cursor + GroupEntryHeaderBytes + nameBytes.Length);
        }

        // Block devices table: a single fixed 64-byte entry for the super device.
        // 块设备表：单个固定 64 字节条目，对应 super 设备。
        cursor = blockDevicesTableOffset;
        var blockDevice = blob.AsSpan((int)cursor);
        BinaryPrimitives.WriteUInt64LittleEndian(blockDevice, firstLogicalSector);
        BinaryPrimitives.WriteUInt32LittleEndian(blockDevice.Slice(8), blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blockDevice.Slice(12), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(blockDevice.Slice(16), deviceSize);
        Encoding.UTF8.GetBytes("super").CopyTo(blockDevice.Slice(24));
        BinaryPrimitives.WriteUInt32LittleEndian(blockDevice.Slice(60), 0);

        return blob;
    }

    private static void WriteTableDescriptor(byte[] blob, int descriptorOffset, long tableOffset, uint numEntries, uint entrySize)
    {
        // The parser reads the descriptor in the order (numEntries, entrySize, offset).
        // 解析器按 (numEntries, entrySize, offset) 的顺序读取描述符。
        var span = blob.AsSpan(descriptorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span, numEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)tableOffset);
    }

    private static void WriteGeometry(byte[] image, int offset, uint metadataMaxSize, uint metadataSlotCount, uint blockSize)
    {
        // liblp hashes the first 20 bytes (magic + struct_size + the still zeroed checksum
        // field) and stores the result in the checksum field.
        // liblp 对前 20 字节（magic + struct_size + 仍为零的校验和字段）求哈希并存入校验和字段。
        var checksumInput = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(checksumInput, GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(checksumInput.AsSpan(4), (uint)LpGeometry.StructSizeBytes);
        var checksum = SHA256.HashData(checksumInput);

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4), (uint)LpGeometry.StructSizeBytes);
        checksum.CopyTo(image.AsSpan(offset + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 40), metadataMaxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 44), metadataSlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 48), blockSize);
    }

    private static (uint NumEntries, uint EntrySize, uint Offset)? ReadTable(ReadOnlySpan<byte> blob, long offset, long available)
    {
        return offset < 0 || offset + 12 > available
            ? null
            : (
            BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)offset, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)offset + 4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)offset + 8, 4)));
    }

    private static List<SuperImageExtent> ReadExtents(
        ReadOnlySpan<byte> blob, uint offset, uint numEntries, long available)
    {
        var result = new List<SuperImageExtent>((int)Math.Min(numEntries, 1024));
        var cursor = (long)offset;
        for (var i = 0; i < numEntries && cursor + ExtentEntryBytes <= available; i++)
        {
            var span = blob.Slice((int)cursor, ExtentEntryBytes);
            result.Add(new SuperImageExtent(
                BinaryPrimitives.ReadUInt64LittleEndian(span),
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20))));
            cursor += ExtentEntryBytes;
        }

        return result;
    }

    private static List<SuperImageBlockDevice> ReadBlockDevices(
        ReadOnlySpan<byte> blob, uint offset, uint numEntries, long available)
    {
        var result = new List<SuperImageBlockDevice>((int)Math.Min(numEntries, 8));
        var cursor = (long)offset;
        for (var i = 0; i < numEntries && cursor + BlockDeviceEntryBytes <= available; i++)
        {
            var span = blob.Slice((int)cursor, BlockDeviceEntryBytes);
            result.Add(new SuperImageBlockDevice(
                BinaryPrimitives.ReadUInt64LittleEndian(span),
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16)),
                DecodeUtf8(span.Slice(24, 36)),
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(60))));
            cursor += BlockDeviceEntryBytes;
        }

        return result;
    }

    private static List<string> ReadGroupNames(
        ReadOnlySpan<byte> blob, uint offset, uint numEntries, long available)
    {
        var result = new List<string>((int)Math.Min(numEntries, 256));
        var cursor = (long)offset;
        for (var i = 0; i < numEntries && cursor + 16 <= available; i++)
        {
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor, 4));
            if (nameSize > 0 && cursor + 16 + nameSize <= available)
            {
                result.Add(DecodeUtf8(blob.Slice((int)cursor + 16, (int)nameSize)));
                cursor += 16 + nameSize;
            }
            else
            {
                result.Add(string.Empty);
                cursor += 16;
            }
        }

        return result;
    }

    private static List<SuperLogicalPartition> ReadPartitions(
        ReadOnlySpan<byte> blob, uint offset, uint numEntries,
        IReadOnlyList<SuperImageExtent> extents,
        IReadOnlyList<string> groupNames, long available)
    {
        var result = new List<SuperLogicalPartition>((int)Math.Min(numEntries, 256));
        var cursor = (long)offset;
        for (var i = 0; i < numEntries && cursor + PartitionEntryHeaderBytes <= available; i++)
        {
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor, 4));
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 4, 4));
            var firstExtentIndex = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 8, 4));
            var numExtents = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 12, 4));
            var groupIndex = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 16, 4));

            if (nameSize == 0 || cursor + PartitionEntryHeaderBytes + nameSize > available)
            {
                break;
            }

            var name = DecodeUtf8(blob.Slice((int)cursor + PartitionEntryHeaderBytes, (int)nameSize));
            var sizeInBytes = 0L;
            for (var e = 0; e < numExtents; e++)
            {
                var extentIndex = firstExtentIndex + e;
                if (extentIndex < extents.Count)
                {
                    sizeInBytes = checked(sizeInBytes + (long)extents[(int)extentIndex].NumSectors * LpSectorSize);
                }
            }

            var groupName = groupIndex < groupNames.Count ? groupNames[(int)groupIndex] : null;
            result.Add(new SuperLogicalPartition(
                name, groupIndex, groupName, attributes, firstExtentIndex, numExtents, sizeInBytes));

            cursor += PartitionEntryHeaderBytes + nameSize;
        }

        return result;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes[..length]);
    }
}