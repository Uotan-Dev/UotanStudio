using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Mbr;

namespace UotanStudio.Firmware.VendorFirmware.Amlogic;

/// <summary>
/// An Amlogic vendor partition-table entry (MBR-style slot plus a name-table name).
/// <para>Amlogic 厂商私有分区表条目（MBR 风格槽位 + 名称表名称）。</para>
/// </summary>
public sealed class AmlogicPartitionEntry
{
    /// <summary>
    /// Whether the partition is bootable (0x80).
    /// <para>是否可引导（0x80）。</para>
    /// </summary>
    /// <value>True when the bootable byte is set.<para>可引导字节被置位时为真。</para></value>
    public bool Bootable { get; init; }

    /// <summary>
    /// Partition type byte.
    /// <para>分区类型字节。</para>
    /// </summary>
    /// <value>The partition type byte of the slot.<para>槽位中的分区类型字节。</para></value>
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
    /// <value>The partition size measured in sectors.<para>以扇区数计量的分区大小。</para></value>
    public ulong SizeSectors { get; init; }

    /// <summary>
    /// Partition name (from the name table).
    /// <para>分区名称（来自名称表）。</para>
    /// </summary>
    /// <value>The name taken from the name table.<para>取自名称表的分区名称。</para></value>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Parsed Amlogic partition-table information.
/// <para>解析出的 Amlogic 分区表信息。</para>
/// </summary>
public sealed class AmlogicPartitionTableInfo
{
    /// <summary>
    /// Disk signature (offset 440, little-endian).
    /// <para>磁盘签名（偏移 440，小端）。</para>
    /// </summary>
    /// <value>The little-endian signature read at the disk-signature offset.<para>在磁盘签名偏移处读取的小端签名。</para></value>
    public uint DiskSignature { get; init; }

    /// <summary>
    /// Partition list.
    /// <para>分区列表。</para>
    /// </summary>
    /// <value>The partitions recovered from the image.<para>从镜像中恢复出的分区。</para></value>
    public IReadOnlyList<AmlogicPartitionEntry> Partitions { get; init; } = [];
}

/// <summary>
/// Amlogic vendor partition-table builder and parser: MBR-style slots, an extended EBR chain, an
/// <c>AMLPART!</c> magic at the image start and a name table for the partition names.
/// <para>Amlogic 厂商分区表构建与解析：MBR 风格槽位、扩展 EBR 链、镜像起始的 <c>AMLPART!</c> 魔数，
/// 以及存放分区名称的名称表。</para>
/// </summary>
public sealed class AmlogicPartitionTable
{
    /// <summary>Sector size in bytes.<para>扇区大小（字节）。</para></summary>
    public const int SectorSize = 512;

    /// <summary>Magic size in bytes.<para>魔数大小（字节）。</para></summary>
    public const int MagicSize = 8;

    /// <summary>Disk-signature offset.<para>磁盘签名偏移。</para></summary>
    public const int DiskSignatureOffset = 440;

    /// <summary>Partition-table offset.<para>分区表偏移。</para></summary>
    public const int PartitionTableOffset = 446;

    /// <summary>Single partition-entry size in bytes.<para>单个分区项大小（字节）。</para></summary>
    public const int PartitionEntrySize = 16;

    /// <summary>Primary partition slot count.<para>主分区槽位数量。</para></summary>
    public const int MaxPrimaryPartitions = 4;

    /// <summary>Name-table record size in bytes.<para>名称表单记录大小（字节）。</para></summary>
    public const int NameRecordSize = 128;

    /// <summary>Maximum name UTF-16 units.<para>名称最大 UTF-16 单元数。</para></summary>
    public const int MaxNameUnits = 36;

    /// <summary>Maximum EBR chain depth used as a cycle guard.<para>EBR 链最大深度（防环）。</para></summary>
    public const int MaxEbrDepth = 64;

    /// <summary>Magic bytes written at the image start.<para>写入镜像起始的魔数字节。</para></summary>
    public static ReadOnlySpan<byte> Magic => PartitionTableSignatures.AmlogicMagic;

    /// <summary>Default partition type.<para>默认分区类型。</para></summary>
    public const byte DefaultType = 0x83;

    private const byte SignatureLo = 0x55;
    private const byte SignatureHi = 0xAA;
    private const byte BootableFlag = 0x80;
    private const int Heads = 255;
    private const int SectorsPerTrack = 63;

    /// <summary>
    /// Checks whether the given bytes carry the Amlogic magic ("AMLPART!" @0).
    /// <para>检查给定字节是否携带 Amlogic 魔数（"AMLPART!" @0）。</para>
    /// </summary>
    /// <param name="data">The bytes to inspect.<para>待检查的字节。</para></param>
    /// <returns>True when the magic is present at the start of the bytes.<para>字节起始处存在魔数时为真。</para></returns>
    public static bool HasMagic(ReadOnlySpan<byte> data) =>
        data.Length >= MagicSize && data[..MagicSize].SequenceEqual(Magic);

    /// <summary>
    /// Builds an Amlogic image from normalized entries: MBR-style slots (≤4 primaries, EBR chain
    /// above 4) plus the start magic and the name table (at the last partition's start sector).
    /// <para>由归一化条目构建 Amlogic 镜像：MBR 风格槽位（≤4 主分区，>4 走 EBR 链）+ 起始魔数 +
    /// 名称表（位于最后一个分区起始扇区）。</para>
    /// </summary>
    /// <param name="diskSignature">Disk signature written at the disk-signature offset.<para>写入磁盘签名偏移处的磁盘签名。</para></param>
    /// <param name="entries">Normalized partition entries to lay out.<para>待排布的归一化分区条目。</para></param>
    /// <param name="bootableFlags">Bootable flag for each entry, or <see langword="null"/> when no entry is bootable.<para>逐项可引导标记，为空表示均不可引导。</para></param>
    /// <returns>The image bytes holding the slots, magic and name table.<para>包含槽位、魔数与名称表的镜像字节。</para></returns>
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
    /// Parses an Amlogic image: validates the magic and MBR signature, reads the primary slots,
    /// walks the EBR chain, and recovers names from the name table.
    /// <para>解析 Amlogic 镜像：校验魔数与 MBR 签名，读取主分区槽位，沿 EBR 链收集逻辑分区，
    /// 并从名称表取回分区名称。</para>
    /// </summary>
    /// <param name="image">The image bytes to parse.<para>待解析的镜像字节。</para></param>
    /// <returns>The disk signature and the recovered partition list.<para>磁盘签名与恢复出的分区列表。</para></returns>
    public static AmlogicPartitionTableInfo Parse(byte[] image)
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
        List<AmlogicPartitionEntry> slots = [];
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
                    slots.Add(new AmlogicPartitionEntry
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

        var partitions = new List<AmlogicPartitionEntry>(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            partitions.Add(new AmlogicPartitionEntry
            {
                Bootable = slot.Bootable,
                Type = slot.Type,
                StartLba = slot.StartLba,
                SizeSectors = slot.SizeSectors,
                Name = i < names.Count ? names[i] : slot.Name
            });
        }

        return new AmlogicPartitionTableInfo { DiskSignature = diskSignature, Partitions = partitions };
    }

    private static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    private static AmlogicPartitionEntry NewPartition(ReadOnlySpan<byte> slot, uint start, uint size) => new()
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