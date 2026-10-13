using System.Buffers.Binary;
using System.Globalization;

namespace UotanStudio.Models;

/// <summary>
/// An MBR partition-table entry (16-byte primary slot or a logical partition in the EBR chain).
/// <para>MBR 分区表条目（16 字节主分区槽位或 EBR 链中的逻辑分区）。</para>
/// </summary>
public sealed class MbrPartition
{
    /// <summary>
    /// Whether the partition is bootable (0x80).
    /// <para>是否可引导（0x80）。</para>
    /// </summary>
    /// <value>True when the bootable flag is set.<para>可引导标志置位时为 true。</para></value>
    public bool Bootable { get; init; }

    /// <summary>
    /// Partition type byte.
    /// <para>分区类型字节（如 0x83 Linux、0x07 NTFS）。</para>
    /// </summary>
    /// <value>The raw type byte of the slot.<para>槽位的原始类型字节。</para></value>
    public byte Type { get; init; }

    /// <summary>
    /// Start LBA.
    /// <para>起始 LBA。</para>
    /// </summary>
    /// <value>The first LBA of the partition.<para>分区的起始 LBA。</para></value>
    public ulong StartLba { get; init; }

    /// <summary>
    /// Size in sectors.
    /// <para>大小（扇区数）。</para>
    /// </summary>
    /// <value>The partition size counted in sectors.<para>分区大小，以扇区计数。</para></value>
    public ulong SizeSectors { get; init; }
}

/// <summary>
/// Parsed MBR partition-table information (primary and extended logical partitions merged in order).
/// <para>解析出的 MBR 分区表信息（主分区与扩展分区中的逻辑分区按顺序合并）。</para>
/// </summary>
public sealed class MbrTableInfo
{
    /// <summary>
    /// Disk signature (offset 440, little-endian).
    /// <para>磁盘签名（偏移 440，小端）。</para>
    /// </summary>
    /// <value>The 32-bit disk signature.<para>32 位磁盘签名。</para></value>
    public uint DiskSignature { get; init; }

    /// <summary>
    /// Partition list.
    /// <para>分区列表。</para>
    /// </summary>
    /// <value>The merged partition entries in order.<para>按顺序合并后的分区条目。</para></value>
    public IReadOnlyList<MbrPartition> Partitions { get; init; } = [];
}

/// <summary>
/// Classic MBR partition-table builder and parser: 0x55AA signature, four 16-byte primary slots, and
/// logical partitions carried through an extended EBR chain. Integers are little-endian.
/// <para>经典 MBR 分区表构建与解析：0x55AA 签名、4 个 16 字节主分区槽位，逻辑分区经扩展分区 EBR 链承载。整数均为小端。</para>
/// </summary>
public static class MbrTable
{
    /// <summary>Sector size in bytes.<para>扇区大小（字节）。</para></summary>
    public const int SectorSize = 512;

    /// <summary>Byte offset of the partition table within the sector.<para>分区表在扇区内的字节偏移。</para></summary>
    public const int PartitionTableOffset = 446;

    /// <summary>Single partition-entry size in bytes.<para>单个分区项大小（字节）。</para></summary>
    public const int PartitionEntrySize = 16;

    /// <summary>Primary partition slot count.<para>主分区槽位数量。</para></summary>
    public const int MaxPrimaryPartitions = 4;

    /// <summary>Disk-signature offset.<para>磁盘签名偏移。</para></summary>
    public const int DiskSignatureOffset = 440;

    /// <summary>Signature low byte.<para>签名低字节。</para></summary>
    public const byte SignatureLo = 0x55;

    /// <summary>Signature high byte.<para>签名高字节。</para></summary>
    public const byte SignatureHi = 0xAA;

    /// <summary>Extended partition type bytes.<para>扩展分区类型字节集合。</para></summary>
    /// <value>The type bytes that mark an extended partition.<para>标识扩展分区的类型字节。</para></value>
    public static ReadOnlySpan<byte> ExtendedTypes => [0x05, 0x0F, 0x85];

    /// <summary>Default Linux partition type.<para>默认 Linux 分区类型。</para></summary>
    public const byte DefaultLinuxType = 0x83;

    /// <summary>Bootable flag.<para>可引导标志。</para></summary>
    public const byte BootableFlag = 0x80;

    /// <summary>Maximum EBR chain depth, used as a cycle guard.<para>EBR 链最大深度，用于防止环路。</para></summary>
    public const int MaxEbrDepth = 64;

    /// <summary>Maximum built-image sector count, equivalent to 64 MiB.<para>构建镜像的最大扇区数，相当于 64 MiB。</para></summary>
    public const ulong MaxImageSectors = 64UL * 1024 * 1024 / SectorSize;

    private const int Heads = 255;
    private const int SectorsPerTrack = 63;

    /// <summary>
    /// Checks whether the given bytes carry the MBR signature (0x55AA @510).
    /// <para>检查给定字节是否携带 MBR 签名（0x55AA @510）。</para>
    /// </summary>
    /// <param name="data">The sector bytes to inspect. <para>要检查的扇区字节。</para></param>
    /// <returns>True when the signature is present.<para>签名存在时为 true。</para></returns>
    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= SectorSize && data[510] == SignatureLo && data[511] == SignatureHi;

    /// <summary>
    /// Identifies a populated MBR partition table without mistaking a FAT volume boot record for a disk.
    /// <para>检查有效的非空 MBR 分区项，排除同样带有 55 AA 签名的 FAT 卷引导扇区。</para>
    /// </summary>
    /// <param name="data">The sector bytes to inspect. <para>要检查的扇区字节。</para></param>
    /// <returns>True when at least one usable partition entry is present.<para>存在至少一个可用分区项时为 true。</para></returns>
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
    /// Parses a partition-type text: 0x-hex or decimal are supported; returns <c>false</c> on failure.
    /// <para>解析分区类型文本：支持 0x 十六进制与十进制，失败返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="text">The partition-type text. <para>分区类型文本。</para></param>
    /// <param name="type">The parsed type byte, zero on failure. <para>解析出的类型字节，失败时为 0。</para></param>
    /// <returns>True when the text parsed; otherwise, false.<para>解析成功时为 true，否则为 false。</para></returns>
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
    /// Builds an MBR image from normalized entries: up to three primary slots, or three primaries plus
    /// an extended EBR chain when more entries are supplied.
    /// <para>由归一化条目构建 MBR 镜像：不超过 3 个条目时全部写入主分区槽位，超过 3 个时前 3 个为主分区，其余经扩展分区 EBR 链承载。</para>
    /// </summary>
    /// <param name="diskSignature">The disk signature to store. <para>要写入的磁盘签名。</para></param>
    /// <param name="entries">The partition entries to lay out. <para>要布局的分区条目。</para></param>
    /// <param name="bootableFlags">Optional per-entry bootable flags matched by index. <para>可选的逐项可引导标志，按下标与条目对应。</para></param>
    /// <returns>The MBR image bytes.<para>MBR 镜像字节。</para></returns>
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

        var useExtended = entries.Count > MaxPrimaryPartitions - 1;
        var primaryCount = useExtended ? MaxPrimaryPartitions - 1 : entries.Count;
        var logicalCount = entries.Count - primaryCount;
        ulong extendedStart = 0;
        ulong extendedEnd = 0;
        if (logicalCount > 0)
        {
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

        var endLba = logicalCount > 0 ? entries[^1].StartSector : 1UL;
        if (endLba > MaxImageSectors)
        {
            throw new ArgumentException(
                $"The MBR EBR chain would need {endLba} sectors, above the {MaxImageSectors} limit. 逻辑分区 EBR 链超出镜像上限。",
                nameof(entries));
        }

        var image = new byte[checked((long)endLba * SectorSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(DiskSignatureOffset), diskSignature);

        for (var i = 0; i < primaryCount; i++)
        {
            WriteSlot(image, PartitionTableOffset + i * PartitionEntrySize, entries[i], IsBootable(i), 0);
        }

        if (logicalCount > 0)
        {
            var extendedSize = checked(extendedEnd - extendedStart + 1);
            WriteExtendedSlot(image, PartitionTableOffset + 3 * PartitionEntrySize, extendedStart, extendedSize);

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
    /// Parses an MBR image through a depth-bounded EBR walk. Primaries are returned before logical
    /// partitions.
    /// <para>解析 MBR 镜像，沿 EBR 链的遍历受深度限制。主分区先于逻辑分区返回。</para>
    /// </summary>
    /// <param name="image">The MBR image bytes. <para>MBR 镜像字节。</para></param>
    /// <returns>The parsed disk signature and partition list.<para>解析出的磁盘签名与分区列表。</para></returns>
    public static MbrTableInfo Parse(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length < SectorSize || !HasSignature(image))
        {
            throw new ArgumentException("Missing MBR signature (0x55AA). 缺少 MBR 签名。", nameof(image));
        }

        var diskSignature = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(DiskSignatureOffset));
        List<MbrPartition> partitions = [];
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
            target[2] = 0xC0 | 0x03;
            return;
        }

        var head = (int)((lba / SectorsPerTrack) % Heads);
        var sector = (int)(lba % SectorsPerTrack) + 1;
        target[0] = (byte)head;
        target[1] = (byte)(sector | (int)((cylinder >> 8) & 0xC0));
        target[2] = (byte)(cylinder & 0xFF);
    }
}