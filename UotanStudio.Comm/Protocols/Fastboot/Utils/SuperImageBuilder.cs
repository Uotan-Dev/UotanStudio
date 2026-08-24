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
/// Parsed LP metadata (header + tables + partitions) for one metadata slot of a super image.
/// <para>super 镜像某个元数据槽位的解析结果（头 + 表 + 分区）。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SuperImageMetadata"/> class.
/// <para>初始化 <see cref="SuperImageMetadata"/> 类的新实例。</para>
/// </remarks>
public sealed class SuperImageMetadata(
    LpGeometry geometry, int slot, uint majorVersion, uint minorVersion,
    uint headerSize, uint metadataSize, uint metadataMaxSize, uint metadataSlotCount,
    IReadOnlyList<SuperLogicalPartition> partitions, IReadOnlyList<string> groupNames)
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

    /// <summary>Gets the partition group names, indexed by group index. 按组索引排列的分区组名称。</summary>
    public IReadOnlyList<string> GroupNames { get; } = groupNames;
}

/// <summary>
/// Reads super partition images: LP geometry / metadata header parsing and logical partition
/// name enumeration. Building optimized super images requires the LP metadata writer from an
/// external package and is intentionally left as a <see cref="NotSupportedException"/> skeleton.
/// <para>读取 super 分区镜像：LP geometry / 元数据头解析与逻辑分区名枚举。构建优化的 super
/// 镜像需要外部包的 LP 元数据写入器，有意保留为 <see cref="NotSupportedException"/> 骨架。</para>
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

        var extents = ReadExtents(blob, extentsTable.Value.Offset, extentsTable.Value.NumEntries, available);
        var groupNames = ReadGroupNames(blob, groupsTable.Value.Offset, groupsTable.Value.NumEntries, available);
        var partitions = ReadPartitions(
            blob, partitionsTable.Value.Offset, partitionsTable.Value.NumEntries,
            extents, groupNames, available);

        return new SuperImageMetadata(
            geometry, slot, majorVersion, minorVersion,
            headerSize, (uint)available, metadataMaxSize, metadataSlotCount,
            partitions, groupNames);
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
    /// Builds an optimized super image.
    /// <para>构建优化的 super 镜像。</para>
    /// </summary>
    /// <remarks>
    /// Skeleton only: building a super image requires the LP metadata writer and sparse image
    /// generator from an external package (FirmwareKit.Lp). This project deliberately provides
    /// parse/read helpers only, so building is not supported without adding that dependency.
    /// <para>仅骨架：构建 super 镜像需要外部包（FirmwareKit.Lp）的 LP 元数据写入器与稀疏镜像
    /// 生成器。本项目刻意只提供解析/读取辅助，未添加该依赖时不支持构建。</para>
    /// </remarks>
    /// <param name="deviceSize">The super device size in bytes. super 设备大小（字节）。</param>
    /// <param name="metadataMaxSize">The metadata slot size in bytes. 元数据槽位大小（字节）。</param>
    /// <param name="metadataSlotCount">The number of metadata slots. 元数据槽位数量。</param>
    /// <param name="blockSize">The logical block size in bytes. 逻辑块大小（字节）。</param>
    /// <returns>This method always throws. 本方法始终抛出异常。</returns>
    /// <exception cref="NotSupportedException">Thrown because no external LP package is referenced. 因未引用外部 LP 包而抛出。</exception>
    public byte[] Build(uint deviceSize, uint metadataMaxSize, uint metadataSlotCount, uint blockSize = 4096)
    {
        throw new NotSupportedException(
            "Building super images requires the LP metadata writer (FirmwareKit.Lp), which is " +
            "not referenced in this project. 构建 super 镜像需要 LP 元数据写入器（FirmwareKit.Lp），" +
            "本项目未引用该包。");
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

    private static List<(ulong NumSectors, uint TargetType, uint TargetData)> ReadExtents(
        ReadOnlySpan<byte> blob, uint offset, uint numEntries, long available)
    {
        var result = new List<(ulong, uint, uint)>((int)Math.Min(numEntries, 1024));
        var cursor = (long)offset;
        for (var i = 0; i < numEntries && cursor + 24 <= available; i++)
        {
            result.Add((
                BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice((int)cursor, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 12, 4))));
            cursor += 24;
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
        IReadOnlyList<(ulong NumSectors, uint TargetType, uint TargetData)> extents,
        IReadOnlyList<string> groupNames, long available)
    {
        var result = new List<SuperLogicalPartition>((int)Math.Min(numEntries, 256));
        var cursor = (long)offset;
        for (var i = 0; i < numEntries && cursor + 24 <= available; i++)
        {
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor, 4));
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 4, 4));
            var firstExtentIndex = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 8, 4));
            var numExtents = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 12, 4));
            var groupIndex = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice((int)cursor + 16, 4));

            if (nameSize == 0 || cursor + 24 + nameSize > available)
            {
                break;
            }

            var name = DecodeUtf8(blob.Slice((int)cursor + 24, (int)nameSize));
            var sizeInBytes = 0L;
            for (var e = 0; e < numExtents; e++)
            {
                var extentIndex = firstExtentIndex + e;
                if (extentIndex < extents.Count)
                {
                    sizeInBytes += (long)extents[(int)extentIndex].NumSectors * LpSectorSize;
                }
            }

            var groupName = groupIndex < groupNames.Count ? groupNames[(int)groupIndex] : null;
            result.Add(new SuperLogicalPartition(
                name, groupIndex, groupName, attributes, firstExtentIndex, numExtents, sizeInBytes));

            cursor += 24 + nameSize;
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