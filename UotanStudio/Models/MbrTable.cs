using System.Buffers.Binary;
using System.Globalization;

namespace UotanStudio.Models;

/// <summary>
/// <para>MBR 分区表条目（16 字节主分区槽位或 EBR 链中的逻辑分区）。</para>
/// An MBR partition-table entry (16-byte primary slot or a logical partition in the EBR chain).
/// </summary>
public sealed class MbrPartition
{
    /// <summary>
    /// <para>是否可引导（0x80）。</para>
    /// Whether the partition is bootable (0x80).
    /// </summary>
    public bool Bootable { get; init; }

    /// <summary>
    /// <para>分区类型字节（如 0x83 Linux、0x07 NTFS）。</para>
    /// Partition type byte (e.g. 0x83 Linux, 0x07 NTFS).
    /// </summary>
    public byte Type { get; init; }

    /// <summary>
    /// <para>起始 LBA。</para>
    /// Start LBA.
    /// </summary>
    public ulong StartLba { get; init; }

    /// <summary>
    /// <para>大小（扇区数）。</para>
    /// Size in sectors.
    /// </summary>
    public ulong SizeSectors { get; init; }
}

/// <summary>
/// <para>解析出的 MBR 分区表信息（主分区与扩展分区中的逻辑分区按顺序合并）。</para>
/// Parsed MBR partition-table information (primary and extended logical partitions merged in order).
/// </summary>
public sealed class MbrTableInfo
{
    /// <summary>
    /// <para>磁盘签名（偏移 440，小端）。</para>
    /// Disk signature (offset 440, little-endian).
    /// </summary>
    public uint DiskSignature { get; init; }

    /// <summary>
    /// <para>分区列表。</para>
    /// Partition list.
    /// </summary>
    public IReadOnlyList<MbrPartition> Partitions { get; init; } = [];
}

/// <summary>
/// <para>经典 MBR 分区表构建与解析：0x55AA 签名、4 个 16 字节主分区槽位、扩展分区（0x05/0x0F/0x85）
/// 经 EBR 链承载逻辑分区。整数均为小端。</para>
/// Classic MBR partition-table builder and parser: 0x55AA signature, four 16-byte primary slots,
/// extended partitions (0x05/0x0F/0x85) carrying logical partitions through an EBR chain.
/// Integers are little-endian.
/// </summary>
public static class MbrTable
{
    /// <summary>扇区大小（字节）。 Sector size in bytes.</summary>
    public const int SectorSize = 512;

    /// <summary>分区表在扇区内的字节偏移。 Partition-table byte offset within the sector.</summary>
    public const int PartitionTableOffset = 446;

    /// <summary>单个分区项大小（字节）。 Single partition-entry size in bytes.</summary>
    public const int PartitionEntrySize = 16;

    /// <summary>主分区槽位数量。 Primary partition slot count.</summary>
    public const int MaxPrimaryPartitions = 4;

    /// <summary>磁盘签名偏移。 Disk-signature offset.</summary>
    public const int DiskSignatureOffset = 440;

    /// <summary>签名低字节。 Signature low byte.</summary>
    public const byte SignatureLo = 0x55;

    /// <summary>签名高字节。 Signature high byte.</summary>
    public const byte SignatureHi = 0xAA;

    /// <summary>扩展分区类型字节集合。 Extended partition type bytes.</summary>
    public static ReadOnlySpan<byte> ExtendedTypes => [0x05, 0x0F, 0x85];

    /// <summary>默认 Linux 分区类型。 Default Linux partition type.</summary>
    public const byte DefaultLinuxType = 0x83;

    /// <summary>可引导标志。 Bootable flag.</summary>
    public const byte BootableFlag = 0x80;

    /// <summary>EBR 链最大深度（防环）。 Maximum EBR chain depth (cycle guard).</summary>
    public const int MaxEbrDepth = 64;

    /// <summary>构建镜像的最大扇区数（64 MiB）。 Maximum built-image sector count (64 MiB).</summary>
    public const ulong MaxImageSectors = 64UL * 1024 * 1024 / SectorSize;

    private const int Heads = 255;
    private const int SectorsPerTrack = 63;

    /// <summary>
    /// <para>检查给定字节是否携带 MBR 签名（0x55AA @510）。</para>
    /// Checks whether the given bytes carry the MBR signature (0x55AA @510).
    /// </summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= SectorSize && data[510] == SignatureLo && data[511] == SignatureHi;

    /// <summary>
    /// Identifies a populated MBR partition table without mistaking a FAT volume boot record for a disk.
    /// <para>检查有效的非空 MBR 分区项，排除同样带有 55 AA 签名的 FAT 卷引导扇区。</para>
    /// </summary>
    public static bool HasPartitionTable(ReadOnlySpan<byte> data)
    {
        if (!HasSignature(data) || IsFatBootRecord(data))
        {
            return false;
        }

        var populated = false;
        for (var i = 0; i < MaxPrimaryPartitions; i++)
        {
            var entry = data.Slice(PartitionTableOffset + i * PartitionEntrySize, PartitionEntrySize);
            if (entry.IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            // Do not compare against the probe length: standalone partition-table exports are valid inputs.
            if (entry[0] is not (0 or BootableFlag) || entry[4] == 0 || start == 0 || size == 0
                || (ulong)start + size > 1UL << 32)
            {
                return false;
            }
            populated = true;
        }
        return populated;
    }

    private static bool IsFatBootRecord(ReadOnlySpan<byte> data)
    {
        if (data[0] != 0xE9 && !(data[0] == 0xEB && data[2] == 0x90))
        {
            return false;
        }
        var sectorBytes = BinaryPrimitives.ReadUInt16LittleEndian(data[11..]);
        var clusterSectors = data[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var roots = BinaryPrimitives.ReadUInt16LittleEndian(data[17..]);
        var sectors16 = BinaryPrimitives.ReadUInt16LittleEndian(data[19..]);
        var sectors = sectors16 != 0 ? (uint)sectors16 : BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        var fatSectors = fat16 != 0 ? (uint)fat16 : BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        if (sectorBytes is not (512 or 1024 or 2048 or 4096)
            || clusterSectors is 0 or > 128 || (clusterSectors & (clusterSectors - 1)) != 0
            || reserved == 0 || data[16] is not (1 or 2) || fatSectors == 0
            || data[21] is not (0xF0 or >= 0xF8) || (fat16 == 0 ? roots != 0 : roots == 0))
        {
            return false;
        }
        var rootSectors = ((ulong)roots * 32 + sectorBytes - 1) / sectorBytes;
        return sectors > reserved + (ulong)data[16] * fatSectors + rootSectors;
    }

    /// <summary>
    /// <para>解析分区类型文本：支持 0x 十六进制与十进制，失败返回 <c>false</c>。</para>
    /// Parses a partition-type text: 0x-hex or decimal are supported; returns <c>false</c> on failure.
    /// </summary>
    public static bool TryParseType(string? text, out byte type)
    {
        type = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out type);
        }

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out type);
    }

    /// <summary>
    /// <para>由归一化条目构建 MBR 镜像：不超过 4 个分区时全部为主分区；超过 4 个时前 3 个为主分区，
    /// 第 4 槽为扩展分区，逻辑分区经 EBR 链承载（逻辑分区起始 LBA 必须 ≥ 1 且相邻分区之间
    /// 至少间隔 1 个扇区留给下一个 EBR）。</para>
    /// Builds an MBR image from normalized entries: up to 4 partitions become primary slots; above 4,
    /// the first 3 are primary, slot 4 becomes the extended partition and logical partitions ride the
    /// EBR chain (logical start LBAs must be ≥ 1 with at least one spare sector between neighbours).
    /// </summary>
    public static byte[] Build(uint diskSignature, IReadOnlyList<PartitionEntry> entries, IReadOnlyList<bool>? bootableFlags = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("MBR requires at least one partition. MBR 至少需要一个分区。", nameof(entries));
        }

        if (entries.Count > MaxEbrDepth + MaxPrimaryPartitions)
        {
            throw new ArgumentException($"MBR partition count {entries.Count} is too large. MBR 分区数量过多。", nameof(entries));
        }

        // MBR 的起始 LBA 与大小是两个独立的 32 位字段，超出即需要 GPT。保护性 0xEE 条目把大小写成
        // 整盘扇区数（可达 0xFFFFFFFF），因此不能按 start+size 的上限来判定。
        // MBR start LBA and size are separate 32-bit fields; beyond them the disk needs GPT. A protective
        // 0xEE entry carries the whole-disk sector count as its size (up to 0xFFFFFFFF), so the fields are
        // bounded individually rather than by their sum.
        foreach (var entry in entries)
        {
            if (entry.SizeSectors == 0)
            {
                throw new ArgumentException($"MBR partition '{entry.Name}' has zero size. MBR 分区大小不能为零。", nameof(entries));
            }

            if (entry.StartSector > uint.MaxValue || entry.SizeSectors > uint.MaxValue)
            {
                throw new ArgumentException(
                    $"MBR partition '{entry.Name}' uses {entry.StartSector}+{entry.SizeSectors} sectors, beyond the 32-bit LBA limit. 分区超出 MBR 32 位 LBA 上限，请改用 GPT。",
                    nameof(entries));
            }
        }

        var bootable = bootableFlags ?? [];
        bool IsBootable(int index) => index < bootable.Count && bootable[index];

        // 超过 3 个分区时，第 4 槽让给扩展分区条目（承载逻辑分区），主分区数上限为 3。
        // When there are more than 3 partitions, slot 4 holds the extended-partition entry (carrying
        // logical partitions), so the primary-partition count caps at 3.
        var useExtended = entries.Count > MaxPrimaryPartitions - 1;
        var primaryCount = useExtended ? MaxPrimaryPartitions - 1 : entries.Count;
        var logicalCount = entries.Count - primaryCount;
        ulong extendedStart = 0;
        ulong extendedEnd = 0;
        if (logicalCount > 0)
        {
            // 逻辑分区需要额外一个扇区作为扩展分区起点前的 EBR；这里把扩展区起点定为第一个逻辑分区前。
            // Logical partitions need one spare sector as the first EBR; the extended region starts there.
            extendedStart = checked(entries[primaryCount].StartSector - 1);
            var lastLogical = entries[^1];
            extendedEnd = checked(lastLogical.StartSector + lastLogical.SizeSectors - 1);
            if (extendedEnd > uint.MaxValue)
            {
                throw new ArgumentException(
                    $"The extended partition ends at sector {extendedEnd}, beyond the 32-bit LBA limit. 扩展分区超出 MBR 32 位 LBA 上限，请改用 GPT。",
                    nameof(entries));
            }
            for (var i = 0; i < logicalCount; i++)
            {
                var entry = entries[primaryCount + i];
                if (entry.StartSector == 0)
                {
                    throw new ArgumentException($"Logical partition '{entry.Name}' must start at LBA ≥ 1. 逻辑分区起始 LBA 必须 ≥ 1。", nameof(entries));
                }

                if (i + 1 < logicalCount)
                {
                    var next = entries[primaryCount + i + 1];
                    if (checked(next.StartSector) < checked(entry.StartSector + entry.SizeSectors + 1))
                    {
                        throw new ArgumentException(
                            $"Logical partitions '{entry.Name}' and '{next.Name}' leave no EBR gap. 相邻逻辑分区之间必须至少间隔 1 个扇区。", nameof(entries));
                    }
                }
            }
        }

        // 镜像只承载表结构：扇区 0 的 MBR 与每个逻辑分区之前的 EBR。分区数据范围不参与镜像大小，
        // 否则覆盖整盘的保护性 0xEE 条目会把镜像推到磁盘容量。
        // The image carries only table structures: the MBR sector and the EBR in front of each logical
        // partition. Partition data extents must not size the image, or a protective 0xEE entry spanning
        // the whole disk would inflate it to the disk capacity.
        var endLba = logicalCount > 0 ? entries[^1].StartSector : 1UL;
        if (endLba > MaxImageSectors)
        {
            throw new ArgumentException(
                $"The MBR EBR chain would need {endLba} sectors, above the {MaxImageSectors} limit. 逻辑分区 EBR 链超出镜像上限。",
                nameof(entries));
        }

        var image = new byte[checked((long)endLba * SectorSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(DiskSignatureOffset), diskSignature);

        // 主分区槽位。
        // Primary slots.
        for (var i = 0; i < primaryCount; i++)
        {
            WriteSlot(image, PartitionTableOffset + i * PartitionEntrySize, entries[i], IsBootable(i), 0);
        }

        if (logicalCount > 0)
        {
            // 第 4 槽：扩展分区。
            // Slot 4: the extended partition.
            var extendedSize = checked(extendedEnd - extendedStart + 1);
            WriteExtendedSlot(image, PartitionTableOffset + 3 * PartitionEntrySize, extendedStart, extendedSize);

            // EBR 链：每个逻辑分区之前一个扇区放置 EBR。
            // EBR chain: one EBR sector precedes each logical partition.
            for (var i = 0; i < logicalCount; i++)
            {
                var entry = entries[primaryCount + i];
                var ebrLba = checked(entry.StartSector - 1);
                var ebr = image.AsSpan(checked((int)(ebrLba * SectorSize)), SectorSize);
                var relative = checked((uint)(entry.StartSector - ebrLba));
                WriteEbrEntry(ebr, 0, entry, IsBootable(primaryCount + i), relative);
                if (i + 1 < logicalCount)
                {
                    var nextEbr = checked(entries[primaryCount + i + 1].StartSector - 1);
                    var relativeNext = checked((uint)(nextEbr - extendedStart));
                    WriteEbrNext(ebr, relativeNext);
                }

                ebr[510] = SignatureLo;
                ebr[511] = SignatureHi;
            }
        }

        image[510] = SignatureLo;
        image[511] = SignatureHi;
        return image;
    }

    /// <summary>
    /// <para>解析 MBR 镜像：校验签名，读取磁盘签名与主分区槽位，若存在扩展分区则沿 EBR 链
    /// 收集逻辑分区（深度受限防环），主分区与逻辑分区按顺序合并返回。</para>
    /// Parses an MBR image: validates the signature, reads the disk signature and primary slots,
    /// walks the EBR chain for logical partitions when an extended partition exists (depth-bounded),
    /// and returns primaries then logicals in order.
    /// </summary>
    public static MbrTableInfo Parse(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length < SectorSize || !HasSignature(image))
        {
            throw new ArgumentException("Missing MBR signature (0x55AA). 缺少 MBR 签名。", nameof(image));
        }

        var diskSignature = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(DiskSignatureOffset));
        var partitions = new List<MbrPartition>();
        ulong? extendedStart = null;

        for (var i = 0; i < MaxPrimaryPartitions; i++)
        {
            var slot = image.AsSpan(PartitionTableOffset + i * PartitionEntrySize, PartitionEntrySize);
            var type = slot[4];
            var start = BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(slot[12..]);
            if (start == 0 || size == 0)
            {
                continue;
            }

            if (IsExtendedType(type))
            {
                extendedStart = start;
                continue;
            }

            partitions.Add(NewPartition(slot, start, size));
        }

        if (extendedStart is { } extStart)
        {
            var ebr = extStart;
            for (var depth = 0; depth < MaxEbrDepth; depth++)
            {
                var ebrOffset = checked((long)ebr * SectorSize);
                if (ebrOffset < 0 || (ulong)ebrOffset + SectorSize > (ulong)image.Length)
                {
                    break;
                }

                var ebrSpan = image.AsSpan((int)ebrOffset, SectorSize);
                if (ebrSpan[510] != SignatureLo || ebrSpan[511] != SignatureHi)
                {
                    break;
                }

                var entry0 = ebrSpan.Slice(PartitionTableOffset, PartitionEntrySize);
                var type0 = entry0[4];
                var rel0 = BinaryPrimitives.ReadUInt32LittleEndian(entry0[8..]);
                var size0 = BinaryPrimitives.ReadUInt32LittleEndian(entry0[12..]);
                if (type0 != 0 && rel0 != 0 && size0 != 0)
                {
                    partitions.Add(new MbrPartition
                    {
                        Bootable = (entry0[0] & BootableFlag) != 0,
                        Type = type0,
                        StartLba = checked(ebr + rel0),
                        SizeSectors = size0
                    });
                }

                var entry1 = ebrSpan.Slice(PartitionTableOffset + PartitionEntrySize, PartitionEntrySize);
                var type1 = entry1[4];
                var rel1 = BinaryPrimitives.ReadUInt32LittleEndian(entry1[8..]);
                if (!IsExtendedType(type1) || rel1 == 0)
                {
                    break;
                }

                ebr = checked(extStart + rel1);
            }
        }

        return new MbrTableInfo { DiskSignature = diskSignature, Partitions = partitions };
    }

    private static bool IsExtendedType(byte type) => ExtendedTypes.IndexOf(type) >= 0;

    private static MbrPartition NewPartition(ReadOnlySpan<byte> slot, uint start, uint size) => new()
    {
        Bootable = (slot[0] & BootableFlag) != 0,
        Type = slot[4],
        StartLba = start,
        SizeSectors = size
    };

    private static void WriteSlot(Span<byte> image, int offset, PartitionEntry entry, bool bootable, uint relativeStart)
    {
        var slot = image.Slice(offset, PartitionEntrySize);
        var absolute = checked(entry.StartSector);
        WriteChs(slot, absolute);
        slot[0] = bootable ? BootableFlag : (byte)0;
        slot[4] = TryParseType(entry.Type, out var type) ? type : DefaultLinuxType;
        WriteChs(slot[6..], checked(absolute + entry.SizeSectors - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], checked((uint)(absolute - relativeStart)));
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], checked((uint)entry.SizeSectors));
    }

    private static void WriteExtendedSlot(Span<byte> image, int offset, ulong start, ulong size)
    {
        var slot = image.Slice(offset, PartitionEntrySize);
        WriteChs(slot, start);
        slot[0] = 0;
        slot[4] = 0x0F;
        WriteChs(slot[6..], checked(start + size - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], checked((uint)start));
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], checked((uint)size));
    }

    private static void WriteEbrEntry(Span<byte> ebr, int entryIndex, PartitionEntry entry, bool bootable, uint relativeStart)
    {
        var slot = ebr.Slice(PartitionTableOffset + entryIndex * PartitionEntrySize, PartitionEntrySize);
        var absolute = checked(entry.StartSector);
        WriteChs(slot, absolute);
        slot[0] = bootable ? BootableFlag : (byte)0;
        slot[4] = TryParseType(entry.Type, out var type) ? type : DefaultLinuxType;
        WriteChs(slot[6..], checked(absolute + entry.SizeSectors - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], relativeStart);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], checked((uint)entry.SizeSectors));
    }

    private static void WriteEbrNext(Span<byte> ebr, uint relativeNext)
    {
        var slot = ebr.Slice(PartitionTableOffset + PartitionEntrySize, PartitionEntrySize);
        slot[0] = 0;
        slot[4] = 0x0F;
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], relativeNext);
    }

    private static void WriteChs(Span<byte> target, ulong lba)
    {
        var cylinder = lba / ((ulong)Heads * SectorsPerTrack);
        if (cylinder > 1023)
        {
            target[0] = 254;
            target[1] = 63;
            target[2] = 0xC0 | 0x03; // cylinder 1023 的高 2 位 + 低 8 位。
            return;
        }

        var head = (int)((lba / SectorsPerTrack) % Heads);
        var sector = (int)(lba % SectorsPerTrack) + 1;
        target[0] = (byte)head;
        target[1] = (byte)(sector | (int)((cylinder >> 8) & 0xC0));
        target[2] = (byte)(cylinder & 0xFF);
    }
}