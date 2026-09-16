using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

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

    /// <summary>Gets the SHA256 checksum covering the complete 52-byte structure. 覆盖完整 52 字节结构的 SHA256 校验和。</summary>
    public byte[] Checksum { get; }

    /// <summary>Gets the maximum size of a metadata slot in bytes. 元数据槽位的最大大小（字节）。</summary>
    public uint MetadataMaxSize { get; }

    /// <summary>Gets the number of metadata slots. 元数据槽位数量。</summary>
    public uint MetadataSlotCount { get; }

    /// <summary>Gets the logical block size in bytes. 逻辑块大小（字节）。</summary>
    public uint LogicalBlockSize { get; }

    /// <summary>
    /// Gets whether the stored checksum matches the recomputed SHA256 over all 52 bytes
    /// (with the checksum field zeroed). Only computed when <see cref="StructSize"/> is the
    /// standard 52 bytes.
    /// <para>存储的校验和是否与对完整 52 字节（校验和字段清零）重新计算的 SHA256 一致。
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
        if (data == null || offset < 0 || offset > data.Length || StructSizeBytes > data.Length - offset)
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

    /// <summary>Gets the absolute starting sector within the source block device.
    /// 获取源块设备内的绝对起始扇区。</summary>
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
/// A partition specification used to build a super image with <see cref="SuperImageBuilder"/>.
/// <para>用于通过 <see cref="SuperImageBuilder"/> 构建 super 镜像的分区规格。</para>
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
                if (idx < extents.Count) sizeInBytes = checked(sizeInBytes + checked((long)extents[idx].NumSectors * MetadataFormat.LP_SECTOR_SIZE));
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
/// Reads and builds super partition images. Parsing and construction delegate to
/// <see cref="MetadataReader"/> and <see cref="MetadataBuilder"/> in <c>UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp</c>,
/// keeping the LP binary layout implementation in a single place.
/// <para>读取与构建 super 分区镜像。解析与构建委托给 <c>UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp</c> 的
/// <see cref="MetadataReader"/> 与 <see cref="MetadataBuilder"/>，LP 二进制布局实现保持单一来源。</para>
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
    public bool IsSuperImage(byte[] data)
    {
        if (data == null) return false;
        using var stream = new MemoryStream(data, writable: false);
        return IsSuperImage(stream);
    }

    /// <summary>
    /// Returns whether the file is a super partition image.
    /// <para>返回文件是否为 super 分区镜像。</para>
    /// </summary>
    public bool IsSuperImage(string superImagePath)
    {
        if (!File.Exists(superImagePath)) return false;
        try
        {
            using var stream = File.OpenRead(superImagePath);
            return IsSuperImage(stream);
        }
        catch (IOException) { return false; }
    }

    private static bool IsSuperImage(Stream stream)
    {
        try { _ = ReadGeometry(stream); return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return false; }
    }

    private static LpMetadataGeometry ReadGeometry(Stream stream)
    {
        try { MetadataReader.ReadPrimaryGeometry(stream, out var geometry); return geometry; }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            MetadataReader.ReadBackupGeometry(stream, out var geometry);
            return geometry;
        }
    }

    /// <summary>
    /// Reads and parses the LP metadata for the specified slot from a raw super image.
    /// Delegates to <see cref="MetadataReader"/> in Firmware.Lp.
    /// <para>从原始 super 镜像读取并解析指定槽位的 LP 元数据。委托给 Firmware.Lp 的 <see cref="MetadataReader"/>。</para>
    /// </summary>
    public SuperImageMetadata? ReadMetadata(byte[] imageData, int slot = 0)
    {
        if (imageData == null || imageData.Length == 0 || slot < 0) return null;
        using var stream = new MemoryStream(imageData, writable: false);
        return ReadMetadata(stream, slot);
    }

    internal static SuperImageMetadata? ReadMetadata(Stream stream, int slot)
    {
        try { return SuperImageMetadata.FromFirmware(ReadFirmwareMetadata(stream, slot), slot); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or EndOfStreamException or IOException or OverflowException)
        {
            return null;
        }
    }

    internal static LpMetadata ReadFirmwareMetadata(Stream stream, int slot)
    {
        var geometry = ReadGeometry(stream);
        if (slot < 0 || (uint)slot >= geometry.MetadataSlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        try { return MetadataReader.ReadPrimaryMetadata(stream, geometry, (uint)slot); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            stream.Position = MetadataReader.GetBackupMetadataOffset(geometry, (ulong)stream.Length, (uint)slot);
            return MetadataReader.ParseMetadata(geometry, stream);
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
        using var stream = File.OpenRead(superImagePath);
        return ReadMetadata(stream, slot)?.Partitions ?? [];
    }

    /// <summary>
    /// Enumerates the logical partitions from a raw super image.
    /// <para>枚举原始 super 镜像的逻辑分区。</para>
    /// </summary>
    public IReadOnlyList<SuperLogicalPartition> GetLogicalPartitions(byte[] imageData, int slot = 0)
        => ReadMetadata(imageData, slot)?.Partitions ?? [];

    /// <summary>
    /// Builds a super partition image using <see cref="MetadataBuilder"/> and <see cref="MetadataWriter"/>
    /// from Firmware.Lp. This convenience API is limited to 256 MiB; use the stream overload for larger images.
    /// <para>使用 Firmware.Lp 的 <see cref="MetadataBuilder"/> 和 <see cref="MetadataWriter"/> 构建 super 分区镜像。
    /// 此便利 API 限制为 256 MiB，更大镜像请使用流重载。</para>
    /// </summary>
    public byte[] Build(
        IReadOnlyList<SuperBuildPartition> partitions,
        uint deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        uint blockSize = 4096)
    {
        if (deviceSize > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(deviceSize), "The byte-array API is limited to 256 MiB; use the stream overload for larger images.");
        using var stream = new MemoryStream();
        Build(stream, partitions, deviceSize, metadataMaxSize, metadataSlotCount, blockSize);
        return stream.ToArray();
    }

    /// <summary>
    /// Builds a zero-filled raw super image with standard LP metadata in a seekable stream.
    /// <para>在可定位流中构建含标准 LP 元数据、分区数据区填零的原始 super 镜像。</para>
    /// </summary>
    /// <param name="output">Writable seekable destination; its existing contents are replaced. 可写且可定位的目标流，替换其原有内容。</param>
    /// <param name="partitions">Logical partition definitions. 逻辑分区定义。</param>
    /// <param name="deviceSize">Total device size in bytes. 设备总字节数。</param>
    /// <param name="metadataMaxSize">Maximum bytes per metadata slot. 每个元数据槽的最大字节数。</param>
    /// <param name="metadataSlotCount">Number of primary and backup metadata slots. 主、备份元数据各自的槽数。</param>
    /// <param name="blockSize">Logical block size in bytes. 逻辑块字节数。</param>
    public void Build(
        Stream output,
        IReadOnlyList<SuperBuildPartition> partitions,
        ulong deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        uint blockSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite || !output.CanSeek)
            throw new ArgumentException("The super image destination must be writable and seekable.", nameof(output));
        ArgumentNullException.ThrowIfNull(partitions);
        if (deviceSize == 0 || deviceSize > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(deviceSize), "Super device size must be positive and fit a stream length.");
        if (metadataSlotCount is 0 or > 32) throw new ArgumentOutOfRangeException(nameof(metadataSlotCount));
        if (metadataMaxSize < 512 || metadataMaxSize > 64 * 1024 * 1024 || metadataMaxSize % LpSectorSize != 0)
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

        var metadataBytes = MetadataFormat.LP_PARTITION_RESERVED_BYTES + 2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE
            + 2UL * metadataMaxSize * metadataSlotCount;
        var firstLogicalByte = (metadataBytes + blockSize - 1) / blockSize * blockSize;
        if (firstLogicalByte > deviceSize || blockSize > deviceSize - firstLogicalByte)
            throw new ArgumentOutOfRangeException(nameof(deviceSize), "Super device size is too small for metadata and a logical block.");
        var remainingBytes = deviceSize - firstLogicalByte;
        foreach (var partition in partitions)
        {
            var size = ((ulong)partition.SizeInBytes + blockSize - 1) / blockSize * blockSize;
            if (size > remainingBytes)
                throw new ArgumentOutOfRangeException(nameof(deviceSize), "Super device size is too small for the aligned partitions.");
            remainingBytes -= size;
        }

        var groups = new HashSet<string>(StringComparer.Ordinal) { "default" };
        foreach (var p in partitions)
        {
            var g = string.IsNullOrWhiteSpace(p.GroupName) ? "default" : p.GroupName;
            groups.Add(g);
        }
        var builder = MetadataBuilder.New(deviceSize, metadataMaxSize, metadataSlotCount, blockSize);
        foreach (var g in groups)
        {
            if (g != "default") builder.AddGroup(g, 0);
        }
        foreach (var p in partitions)
        {
            builder.AddPartition(p.Name, string.IsNullOrWhiteSpace(p.GroupName) ? "default" : p.GroupName, (ulong)p.SizeInBytes, p.Attributes);
        }
        var metadata = builder.Build();

        _ = MetadataWriter.SerializeMetadata(metadata);
        output.SetLength(0);
        output.SetLength((long)deviceSize);
        MetadataWriter.WriteToImageStream(output, metadata);
    }
}