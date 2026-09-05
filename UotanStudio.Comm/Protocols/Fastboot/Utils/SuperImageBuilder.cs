using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Lp;

namespace UotanStudio.Comm.Protocols.Fastboot.Utils;

// ─────────────────────────────────────────────────────────────────────────────
// LP 模型类型现在直接使用 Firmware.Lp 中的实现，消除 Comm 侧的重复定义。
// LP model types now delegate directly to Firmware.Lp, eliminating duplicate definitions in Comm.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parsed <c>LpMetadataGeometry</c> from a super partition image (magic "gDla").
/// <para>从 super 分区镜像解析的 <c>LpMetadataGeometry</c>（魔数 "gDla"）。</para>
/// </summary>
/// <remarks>
/// 本类型为 <see cref="LpMetadataGeometry"/> 的兼容性包装，提供 <c>TryParse</c> 语义。
/// <para>This type is a compatibility wrapper over <see cref="LpMetadataGeometry"/> providing <c>TryParse</c> semantics.</para>
/// </remarks>
public sealed class LpGeometry
{
    /// <summary>
    /// Size of the on-disk <c>LpMetadataGeometry</c> struct in bytes.
    /// <para><c>LpMetadataGeometry</c> 结构在磁盘上的大小（字节）。</para>
    /// </summary>
    public const int StructSizeBytes = MetadataFormat.Sizes.Geometry;

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
    public static LpGeometry? TryParse(byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + StructSizeBytes > data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> s = data.AsSpan(offset, StructSizeBytes);
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
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
            // liblp hashes the first sizeof(lp_metadata_geometry) bytes of the struct
            // (magic + struct_size + the still zeroed checksum field + remaining fields)
            // and stores the result in the checksum field. Matches AOSP liblp and
            // Firmware's MetadataWriter/Reader.
            // liblp 对结构前 sizeof(lp_metadata_geometry) 字节求哈希并存入校验和字段。
            // 与 AOSP liblp 及 Firmware 的 MetadataWriter/Reader 一致。
            var checksumInput = new byte[StructSizeBytes];
            s[..StructSizeBytes].CopyTo(checksumInput);
            Array.Clear(checksumInput, 8, 32);
            checksumValid = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(checksumInput), checksum);
        }

        return new LpGeometry(
            structSize, checksum, metadataMaxSize, metadataSlotCount, logicalBlockSize, checksumValid);
    }

    /// <summary>将 Firmware 的 <see cref="LpMetadataGeometry"/> 转换为兼容的 <see cref="LpGeometry"/>。
    /// <para>Converts a Firmware <see cref="LpMetadataGeometry"/> to a compatible <see cref="LpGeometry"/>.</para>
    /// </summary>
    internal static LpGeometry FromFirmware(LpMetadataGeometry g)
        => new(g.StructSize, g.Checksum.ToArray(), g.MetadataMaxSize, g.MetadataSlotCount, g.LogicalBlockSize, true);
}

/// <summary>
/// A logical partition read from a super partition's LP metadata.
/// <para>从 super 分区的 LP 元数据读取的逻辑分区。</para>
/// </summary>
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

    /// <inheritdoc />
    public override string ToString() =>
        $"SuperLogicalPartition[Name={Name}, Group={GroupName ?? GroupIndex.ToString()}, Size={SizeInBytes} bytes]";
}

/// <summary>
/// A single extent of a logical partition, describing a contiguous run of 512-byte sectors.
/// <para>逻辑分区的单个 extent，描述一段连续的 512 字节扇区。</para>
/// </summary>
public sealed class SuperImageExtent(
    ulong numSectors, uint targetType, ulong targetData, uint targetSource)
{
    /// <summary>Gets the number of 512-byte sectors in this extent. 此 extent 的 512 字节扇区数。</summary>
    public ulong NumSectors { get; } = numSectors;

    /// <summary>Gets the target type (0=Linear, 1=Zero). 目标类型（0=线性，1=零）。</summary>
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

    /// <summary>
    /// 从 Firmware 的 <see cref="LpMetadata"/> 转换为 Comm 兼容的 <see cref="SuperImageMetadata"/>。
    /// <para>Converts a Firmware <see cref="LpMetadata"/> to a Comm-compatible <see cref="SuperImageMetadata"/>.</para>
    /// </summary>
    internal static SuperImageMetadata FromFirmware(LpMetadata lp, int slot)
    {
        var groupNames = lp.Groups.Select(g => g.NameString).ToList();
        var extents = lp.Extents.Select(e => new SuperImageExtent(e.NumSectors, e.TargetType, e.TargetData, e.TargetSource)).ToList();
        var blockDevices = lp.BlockDevices.Select(b => new SuperImageBlockDevice(
            b.FirstLogicalSector, b.Alignment, b.AlignmentOffset, b.Size,
            b.PartitionName is { Length: > 0 } pn ? Encoding.UTF8.GetString(pn.AsSpan().TrimEnd((byte)0)) : string.Empty,
            b.Flags)).ToList();

        var partitions = lp.Partitions.Select(p =>
        {
            var name = p.NameString;
            var groupName = p.GroupIndex < groupNames.Count ? groupNames[(int)p.GroupIndex] : null;
            long sizeInBytes = 0;
            for (var e = 0; e < p.NumExtents; e++)
            {
                var idx = (int)(p.FirstExtentIndex + e);
                if (idx < extents.Count) sizeInBytes += (long)extents[idx].NumSectors * MetadataFormat.LP_SECTOR_SIZE;
            }
            return new SuperLogicalPartition(name, p.GroupIndex, groupName, p.Attributes, p.FirstExtentIndex, p.NumExtents, sizeInBytes);
        }).ToList();

        var geo = LpGeometry.FromFirmware(lp.Geometry);
        var headerSize = lp.Header.HeaderSize;
        return new SuperImageMetadata(
            geo, slot,
            lp.Header.MajorVersion, lp.Header.MinorVersion,
            headerSize, lp.Header.TablesSize + headerSize,
            lp.Geometry.MetadataMaxSize, lp.Geometry.MetadataSlotCount,
            partitions, extents, blockDevices, groupNames);
    }
}

/// <summary>
/// Reads and builds super partition images. Internal parsing and construction delegate to
/// <see cref="MetadataReader"/> and <see cref="MetadataBuilder"/> in <c>UotanStudio.Firmware.Lp</c>,
/// eliminating the duplicate LP binary layout code that previously lived in Comm.
/// <para>读取与构建 super 分区镜像。内部解析与构建委托给 <c>UotanStudio.Firmware.Lp</c> 的
/// <see cref="MetadataReader"/> 和 <see cref="MetadataBuilder"/>，消除此前 Comm 中的重复 LP 二进制布局代码。</para>
/// </summary>
public sealed class SuperImageBuilder
{
    /// <summary>LP geometry magic ("gDla"). LP geometry 魔数。</summary>
    public const uint GeometryMagic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;

    /// <summary>Linear extent target type. 线性 extent 目标类型。</summary>
    public const uint LinearTargetType = MetadataFormat.LP_TARGET_TYPE_LINEAR;

    /// <summary>Zero-fill extent target type. 零填充 extent 目标类型。</summary>
    public const uint ZeroTargetType = MetadataFormat.LP_TARGET_TYPE_ZERO;

    /// <summary>Size of one geometry block in bytes. 单个 geometry 块大小（字节）。</summary>
    public const int GeometryBlockSize = MetadataFormat.LP_METADATA_GEOMETRY_SIZE;

    /// <summary>LP sector size in bytes. LP 扇区大小（字节）。</summary>
    public const int LpSectorSize = MetadataFormat.LP_SECTOR_SIZE;

    /// <summary>
    /// Returns whether the byte array is a super partition image (LP geometry magic present).
    /// <para>返回字节数组是否为 super 分区镜像（存在 LP geometry 魔数）。</para>
    /// </summary>
    public bool IsSuperImage(byte[] data) => data != null && (LpGeometry.TryParse(data, 0) != null || LpGeometry.TryParse(data, GeometryBlockSize) != null);

    /// <summary>
    /// Returns whether the file is a super partition image.
    /// <para>返回文件是否为 super 分区镜像。</para>
    /// </summary>
    public bool IsSuperImage(string superImagePath)
    {
        if (!File.Exists(superImagePath)) return false;
        try { return IsSuperImage(File.ReadAllBytes(superImagePath)); }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Reads and parses the LP metadata for the specified slot from a raw super image.
    /// Delegates to <see cref="MetadataReader"/> in Firmware.Lp.
    /// <para>从原始 super 镜像读取并解析指定槽位的 LP 元数据。委托给 Firmware.Lp 的 <see cref="MetadataReader"/>。</para>
    /// </summary>
    public SuperImageMetadata? ReadMetadata(byte[] imageData, int slot = 0)
    {
        if (imageData == null || imageData.Length == 0 || slot < 0) return null;
        try
        {
            using var stream = new MemoryStream(imageData, writable: false);
            LpMetadataGeometry geo;
            try
            {
                geo = LpMetadataGeometry.FromBytes(imageData.AsSpan(MetadataFormat.LP_PARTITION_RESERVED_BYTES, MetadataFormat.Sizes.Geometry).ToArray());
            }
            catch
            {
                // Try backup geometry
                if (imageData.Length < MetadataFormat.LP_PARTITION_RESERVED_BYTES + GeometryBlockSize + MetadataFormat.Sizes.Geometry)
                    return null;
                try
                {
                    geo = LpMetadataGeometry.FromBytes(imageData.AsSpan(MetadataFormat.LP_PARTITION_RESERVED_BYTES + GeometryBlockSize, MetadataFormat.Sizes.Geometry).ToArray());
                }
                catch
                {
                    return null;
                }
            }

            if (geo.Magic != GeometryMagic) return null;
            if (slot >= (int)geo.MetadataSlotCount) return null;
            var lp = MetadataReader.ReadPrimaryMetadata(stream, geo, (uint)slot);
            return SuperImageMetadata.FromFirmware(lp, slot);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates the logical partitions described by the specified metadata slot of a super image file.
    /// <para>枚举 super 镜像文件指定元数据槽位描述的逻辑分区。</para>
    /// </summary>
    public IReadOnlyList<SuperLogicalPartition> GetLogicalPartitions(string superImagePath, int slot = 0)
    {
        if (string.IsNullOrWhiteSpace(superImagePath))
            throw new ArgumentException("Super image path is required.", nameof(superImagePath));
        if (!File.Exists(superImagePath))
            throw new FileNotFoundException($"Super image file not found: {superImagePath}", superImagePath);
        return GetLogicalPartitions(File.ReadAllBytes(superImagePath), slot);
    }

    /// <summary>
    /// Enumerates the logical partitions from a raw super image.
    /// <para>枚举原始 super 镜像的逻辑分区。</para>
    /// </summary>
    public IReadOnlyList<SuperLogicalPartition> GetLogicalPartitions(byte[] imageData, int slot = 0)
        => ReadMetadata(imageData, slot)?.Partitions ?? [];

    /// <summary>
    /// Builds a super partition image using <see cref="MetadataBuilder"/> and <see cref="MetadataWriter"/>
    /// from Firmware.Lp.
    /// <para>使用 Firmware.Lp 的 <see cref="MetadataBuilder"/> 和 <see cref="MetadataWriter"/> 构建 super 分区镜像。</para>
    /// </summary>
    public byte[] Build(
        IReadOnlyList<SuperBuildPartition> partitions,
        uint deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        uint blockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (deviceSize == 0) throw new ArgumentOutOfRangeException(nameof(deviceSize), "Super device size must be positive.");
        if (metadataSlotCount == 0) throw new ArgumentOutOfRangeException(nameof(metadataSlotCount), "At least one metadata slot is required.");
        if (metadataMaxSize < 256 + (4 * 12))
            throw new ArgumentOutOfRangeException(nameof(metadataMaxSize), "Metadata slot size is too small for an LP metadata header.");
        if (blockSize == 0 || blockSize % LpSectorSize != 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize));

        const int MaxNameBytes = 36;
        foreach (var p in partitions)
        {
            if (string.IsNullOrEmpty(p.Name))
                throw new ArgumentException("Partition name must not be empty.", nameof(partitions));
            var nameLen = Encoding.UTF8.GetByteCount(p.Name);
            if (nameLen > MaxNameBytes)
                throw new ArgumentException($"Partition name must be 1..{MaxNameBytes} UTF-8 bytes.", nameof(partitions));
            if (p.SizeInBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(partitions), $"Partition '{p.Name}' has a negative size.");
        }

        // Validate device size can fit metadata + at least some data.
        var metadataBytes = (ulong)metadataMaxSize * metadataSlotCount;
        var firstLogicalByte = ((MetadataFormat.LP_PARTITION_RESERVED_BYTES + (2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE) + metadataBytes + blockSize - 1) / blockSize) * blockSize;
        ulong totalSectors = 0;
        foreach (var p in partitions) totalSectors += (ulong)((Math.Max(0, p.SizeInBytes) + LpSectorSize - 1) / LpSectorSize);
        var dataBytes = totalSectors * (ulong)LpSectorSize;
        if (firstLogicalByte + dataBytes + metadataBytes > deviceSize)
            throw new ArgumentOutOfRangeException(nameof(deviceSize), "Super device size is too small for metadata and partitions.");

        // Register all groups before adding partitions.
        var groups = new HashSet<string>(StringComparer.Ordinal) { "default" };
        foreach (var p in partitions)
        {
            var g = string.IsNullOrWhiteSpace(p.GroupName) ? "default" : p.GroupName;
            groups.Add(g);
        }
        var builder = MetadataBuilder.New(deviceSize, metadataMaxSize, metadataSlotCount);
        foreach (var g in groups)
        {
            if (g != "default") builder.AddGroup(g, 0);
        }
        foreach (var p in partitions)
        {
            builder.AddPartition(p.Name, string.IsNullOrWhiteSpace(p.GroupName) ? "default" : p.GroupName, (ulong)p.SizeInBytes, p.Attributes);
        }
        var metadata = builder.Build();

        // Override geometry block size if caller specified a non-default value.
        if (blockSize != 4096)
        {
            metadata.Geometry = new LpMetadataGeometry
            {
                Magic = metadata.Geometry.Magic,
                StructSize = metadata.Geometry.StructSize,
                Checksum = metadata.Geometry.Checksum,
                MetadataMaxSize = metadata.Geometry.MetadataMaxSize,
                MetadataSlotCount = metadata.Geometry.MetadataSlotCount,
                LogicalBlockSize = blockSize
            };
            // Recompute block device FirstLogicalSector for the new block size.
            if (metadata.BlockDevices.Count > 0)
            {
                var bd = metadata.BlockDevices[0];
                metadata.BlockDevices[0] = new LpMetadataBlockDevice
                {
                    FirstLogicalSector = (MetadataFormat.LP_PARTITION_RESERVED_BYTES
                        + (2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE)
                        + (metadataMaxSize * metadataSlotCount * 2UL)) / blockSize,
                    Alignment = bd.Alignment,
                    AlignmentOffset = bd.AlignmentOffset,
                    Size = bd.Size,
                    PartitionName = bd.PartitionName,
                    Flags = bd.Flags
                };
            }
        }

        using var ms = new MemoryStream();
        MetadataWriter.WriteToImageStream(ms, metadata);
        return ms.ToArray();
    }
}
