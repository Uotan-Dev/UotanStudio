using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Models;

/// <summary>
/// <para>Amlogic 厂商私有分区表条目（MBR 风格槽位 + 名称表名称）。</para>
/// An Amlogic vendor partition-table entry (MBR-style slot plus a name-table name).
/// </summary>
public sealed class AmlogicPartition
{
    /// <summary>
    /// <para>是否可引导（0x80）。</para>
    /// Whether the partition is bootable (0x80).
    /// </summary>
    public bool Bootable { get; init; }

    /// <summary>
    /// <para>分区类型字节。</para>
    /// Partition type byte.
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

    /// <summary>
    /// <para>分区名称（来自名称表）。</para>
    /// Partition name (from the name table).
    /// </summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// <para>解析出的 Amlogic 分区表信息。</para>
/// Parsed Amlogic partition-table information.
/// </summary>
public sealed class AmlogicTableInfo
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
    public IReadOnlyList<AmlogicPartition> Partitions { get; init; } = [];
}

/// <summary>
/// <para>Amlogic 厂商私有分区表构建与解析：在 MBR 风格布局（446 偏移 4×16 字节槽位 + 0x55AA 签名 +
/// 扩展分区 EBR 链）基础上，于镜像起始写入 <c>AMLPART!</c> 魔数，并把分区名称存放于镜像中
/// 最后一个分区起始扇区处的名称表（每记录 128 字节，名称 36 个 UTF-16LE 单元）。
/// 说明：Amlogic 老平台（meson8 等）的分区表承载于引导区/定制 MBR 布局，此实现是遵循该
/// 约定的可往返表示（构建与解析自洽），非 AOSP 标准格式。</para>
/// Amlogic vendor partition-table builder and parser: on top of the MBR-style layout (4×16-byte
/// slots at 446 + 0x55AA signature + extended EBR chain) it writes an <c>AMLPART!</c> magic at the
/// image start and stores partition names in a name table at the start sector of the last partition
/// (128-byte records, names of 36 UTF-16LE units). Note: Amlogic legacy platforms (meson8 etc.)
/// carried the partition table in a boot area / customized MBR layout; this is a round-trippable
/// representation following that convention (builder and parser are self-consistent), not an AOSP
/// standard format.
/// </summary>
public static class AmlogicTable
{
    /// <summary>扇区大小（字节）。 Sector size in bytes.</summary>
    public const int SectorSize = 512;

    /// <summary>魔数大小（字节）。 Magic size in bytes.</summary>
    public const int MagicSize = 8;

    /// <summary>磁盘签名偏移。 Disk-signature offset.</summary>
    public const int DiskSignatureOffset = 440;

    /// <summary>分区表偏移。 Partition-table offset.</summary>
    public const int PartitionTableOffset = 446;

    /// <summary>单个分区项大小（字节）。 Single partition-entry size in bytes.</summary>
    public const int PartitionEntrySize = 16;

    /// <summary>主分区槽位数量。 Primary partition slot count.</summary>
    public const int MaxPrimaryPartitions = 4;

    /// <summary>名称表单记录大小（字节）。 Name-table record size in bytes.</summary>
    public const int NameRecordSize = 128;

    /// <summary>名称最大 UTF-16 单元数。 Maximum name UTF-16 units.</summary>
    public const int MaxNameUnits = 36;

    /// <summary>EBR 链最大深度（防环）。 Maximum EBR chain depth (cycle guard).</summary>
    public const int MaxEbrDepth = 64;

    /// <summary>魔数（"AMLPART!"）。 Magic ("AMLPART!").</summary>
    public static readonly byte[] Magic = "AMLPART!"u8.ToArray();

    /// <summary>默认分区类型。 Default partition type.</summary>
    public const byte DefaultType = 0x83;

    private const byte SignatureLo = 0x55;
    private const byte SignatureHi = 0xAA;
    private const byte BootableFlag = 0x80;
    private const int Heads = 255;
    private const int SectorsPerTrack = 63;

    /// <summary>
    /// <para>检查给定字节是否携带 Amlogic 魔数（"AMLPART!" @0）。</para>
    /// Checks whether the given bytes carry the Amlogic magic ("AMLPART!" @0).
    /// </summary>
    public static bool HasMagic(ReadOnlySpan<byte> data) =>
        data.Length >= MagicSize && data[..MagicSize].SequenceEqual(Magic);

    /// <summary>
    /// <para>由归一化条目构建 Amlogic 镜像：MBR 风格槽位（≤4 主分区，>4 走 EBR 链）+ 起始魔数 +
    /// 名称表（位于最后一个分区起始扇区）。</para>
    /// Builds an Amlogic image from normalized entries: MBR-style slots (≤4 primaries, EBR chain
    /// above 4) plus the start magic and the name table (at the last partition's start sector).
    /// </summary>
    public static byte[] Build(uint diskSignature, IReadOnlyList<PartitionEntry> entries, IReadOnlyList<bool>? bootableFlags = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("Amlogic table requires at least one partition. 至少需要一个分区。", nameof(entries));
        }

        if (entries.Count > MaxEbrDepth + MaxPrimaryPartitions)
        {
            throw new ArgumentException($"Amlogic partition count {entries.Count} is too large. 分区数量过多。", nameof(entries));
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
            extendedStart = checked(entries[primaryCount].StartSector - 1);
            var lastLogical = entries[^1];
            extendedEnd = checked(lastLogical.StartSector + lastLogical.SizeSectors - 1);
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

        ulong endLba = 0;
        foreach (var entry in entries)
        {
            endLba = Math.Max(endLba, checked(entry.StartSector + entry.SizeSectors));
        }

        if (logicalCount > 0)
        {
            endLba = Math.Max(endLba, extendedEnd + 1);
        }

        // 名称表位置：最后一个分区起始扇区（构建与解析一致的确定性规则）。
        // Name-table location: the last partition's start sector (deterministic rule shared with Parse).
        var nameTableStart = checked(entries[^1].StartSector);
        var nameTableBytes = checked(entries.Count * NameRecordSize);
        var nameTableSectors = checked((nameTableBytes + SectorSize - 1) / SectorSize);
        endLba = Math.Max(endLba, checked(nameTableStart + (ulong)nameTableSectors));

        var image = new byte[checked((long)endLba * SectorSize)];
        Magic.CopyTo(image);
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
                    WriteEbrNext(ebr, checked((uint)(nextEbr - extendedStart)));
                }

                ebr[510] = SignatureLo;
                ebr[511] = SignatureHi;
            }
        }

        // 名称表。
        // Name table.
        var nameTable = image.AsSpan(checked((int)(nameTableStart * SectorSize)), nameTableBytes);
        for (var i = 0; i < entries.Count; i++)
        {
            var record = nameTable.Slice(i * NameRecordSize, NameRecordSize);
            var name = entries[i].Name;
            var length = Math.Min(name.Length, MaxNameUnits);
            Encoding.Unicode.GetBytes(name.AsSpan(0, length), record[..(length * 2)]);
        }

        image[510] = SignatureLo;
        image[511] = SignatureHi;
        return image;
    }

    /// <summary>
    /// <para>解析 Amlogic 镜像：校验魔数，读取磁盘签名与主分区槽位，沿 EBR 链收集逻辑分区，
    /// 再从名称表（最后一个分区起始扇区）按顺序取回分区名称。</para>
    /// Parses an Amlogic image: validates the magic, reads the disk signature and primary slots,
    /// walks the EBR chain for logical partitions, then reads partition names in order from the
    /// name table (at the last partition's start sector).
    /// </summary>
    public static AmlogicTableInfo Parse(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length < SectorSize || !HasMagic(image))
        {
            throw new ArgumentException("Missing Amlogic magic (\"AMLPART!\"). 缺少 Amlogic 魔数。", nameof(image));
        }

        if (image[510] != SignatureLo || image[511] != SignatureHi)
        {
            throw new ArgumentException("Missing Amlogic MBR signature (0x55AA). 缺少 Amlogic MBR 签名。", nameof(image));
        }

        var diskSignature = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(DiskSignatureOffset));
        var slots = new List<AmlogicPartition>();
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

            slots.Add(NewPartition(slot, start, size));
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
                    slots.Add(new AmlogicPartition
                    {
                        Bootable = (entry0[0] & BootableFlag) != 0,
                        Type = type0,
                        StartLba = checked(ebr + rel0),
                        SizeSectors = size0,
                        Name = string.Empty
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

        // 名称表：最后一个分区起始扇区，每记录 128 字节（名称 36 UTF-16LE 单元）。
        // Name table: last partition's start sector, 128-byte records (36 UTF-16LE units each).
        var names = new List<string>(slots.Count);
        if (slots.Count > 0)
        {
            var nameTableStart = checked(slots[^1].StartLba);
            var nameTableBytes = checked(slots.Count * NameRecordSize);
            var start = checked((int)(nameTableStart * SectorSize));
            if (start >= 0 && (ulong)start + (ulong)nameTableBytes <= (ulong)image.Length)
            {
                var table = image.AsSpan(start, nameTableBytes);
                for (var i = 0; i < slots.Count; i++)
                {
                    names.Add(ReadName(table.Slice(i * NameRecordSize, NameRecordSize)));
                }
            }
        }

        var partitions = new List<AmlogicPartition>(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            partitions.Add(new AmlogicPartition
            {
                Bootable = slot.Bootable,
                Type = slot.Type,
                StartLba = slot.StartLba,
                SizeSectors = slot.SizeSectors,
                Name = i < names.Count ? names[i] : slot.Name
            });
        }

        return new AmlogicTableInfo { DiskSignature = diskSignature, Partitions = partitions };
    }

    private static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    private static AmlogicPartition NewPartition(ReadOnlySpan<byte> slot, uint start, uint size) => new()
    {
        Bootable = (slot[0] & BootableFlag) != 0,
        Type = slot[4],
        StartLba = start,
        SizeSectors = size,
        Name = string.Empty
    };

    private static void WriteSlot(Span<byte> image, int offset, PartitionEntry entry, bool bootable, uint relativeStart)
    {
        var slot = image.Slice(offset, PartitionEntrySize);
        var absolute = checked(entry.StartSector);
        WriteChs(slot, absolute);
        slot[0] = bootable ? BootableFlag : (byte)0;
        slot[4] = MbrTable.TryParseType(entry.Type, out var type) ? type : DefaultType;
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
        slot[4] = MbrTable.TryParseType(entry.Type, out var type) ? type : DefaultType;
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
            target[2] = 0xC3;
            return;
        }

        var head = (int)((lba / SectorsPerTrack) % Heads);
        var sector = (int)(lba % SectorsPerTrack) + 1;
        target[0] = (byte)head;
        target[1] = (byte)(sector | (int)((cylinder >> 8) & 0xC0));
        target[2] = (byte)(cylinder & 0xFF);
    }

    private static string ReadName(ReadOnlySpan<byte> record)
    {
        var units = record[..(MaxNameUnits * 2)];
        var end = 0;
        while (end + 1 < units.Length && (units[end] != 0 || units[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(units[..end]);
    }
}