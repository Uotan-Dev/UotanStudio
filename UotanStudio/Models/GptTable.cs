using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Models;

/// <summary>
/// <para>UEFI GPT 分区表条目（128 字节分区表项）。</para>
/// A UEFI GPT partition-table entry (128-byte partition entry).
/// </summary>
public sealed class GptEntry
{
    /// <summary>
    /// <para>分区类型 GUID。</para>
    /// Partition type GUID.
    /// </summary>
    public Guid TypeGuid { get; init; }

    /// <summary>
    /// <para>分区唯一 GUID。</para>
    /// Unique partition GUID.
    /// </summary>
    public Guid UniqueGuid { get; init; }

    /// <summary>
    /// <para>起始 LBA。</para>
    /// First LBA.
    /// </summary>
    public ulong FirstLba { get; init; }

    /// <summary>
    /// <para>结束 LBA（含）。</para>
    /// Last LBA (inclusive).
    /// </summary>
    public ulong LastLba { get; init; }

    /// <summary>
    /// <para>分区属性位。</para>
    /// Partition attributes.
    /// </summary>
    public ulong Attributes { get; init; }

    /// <summary>
    /// <para>分区名称（最多 36 个 UTF-16 单元）。</para>
    /// Partition name (up to 36 UTF-16 units).
    /// </summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// <para>解析出的 GPT 分区表信息。</para>
/// Parsed GPT partition-table information.
/// </summary>
public sealed class GptTableInfo
{
    /// <summary>
    /// <para>磁盘大小（字节，由备份头 LBA 推算）。</para>
    /// Disk size in bytes (derived from the backup header LBA).
    /// </summary>
    public ulong DiskSizeBytes { get; init; }

    /// <summary>
    /// <para>磁盘 GUID。</para>
    /// Disk GUID.
    /// </summary>
    public Guid DiskGuid { get; init; }

    /// <summary>
    /// <para>分区表项。</para>
    /// Partition entries.
    /// </summary>
    public IReadOnlyList<GptEntry> Entries { get; init; } = [];
}

/// <summary>
/// <para>UEFI GPT 分区表构建与解析（保护性 MBR + 主 GPT 头 + 分区表项；整数均为小端，
/// GUID 按 EFI 规范大端存储）。</para>
/// UEFI GPT partition-table builder and parser (protective MBR + primary GPT header +
/// partition entries; integers are little-endian, GUIDs are stored big-endian per EFI spec).
/// </summary>
public static class GptTable
{
    /// <summary>扇区大小（字节）。 Sector size in bytes.</summary>
    public const int SectorSize = 512;

    /// <summary>GPT 头在磁盘中的字节偏移。 GPT header byte offset on disk.</summary>
    public const int HeaderOffset = 512;

    /// <summary>单个分区表项大小（字节）。 Single partition-entry size in bytes.</summary>
    public const int EntrySize = 128;

    /// <summary>GPT 头大小（字节）。 GPT header size in bytes.</summary>
    public const int HeaderSize = 92;

    /// <summary>分区名最大 UTF-16 单元数。 Maximum partition-name UTF-16 units.</summary>
    public const int MaxNameUnits = 36;

    /// <summary>头 CRC-32 字段在头内的偏移。 Header CRC-32 field offset within the header.</summary>
    public const int HeaderCrcOffset = 16;

    /// <summary>分区表项 CRC-32 字段在头内的偏移。 Partition-entry-array CRC-32 field offset.</summary>
    public const int EntriesCrcOffset = 88;

    /// <summary>分区表项起始 LBA。 Partition-entry-array start LBA.</summary>
    public const ulong EntriesStartLba = 2;

    /// <summary>首个可用 LBA（头+分区表占 34 个扇区）。 First usable LBA (header + entries occupy 34 sectors).</summary>
    public const ulong FirstUsableLba = 34;

    /// <summary>主 GPT 头所在 LBA。 Primary GPT header LBA.</summary>
    public const ulong PrimaryHeaderLba = 1;

    /// <summary>GPT 头修订号（1.0）。 GPT header revision (1.0).</summary>
    public const uint Revision = 0x0001_0000;

    /// <summary>保护性 MBR 分区类型。 Protective MBR partition type.</summary>
    public const byte ProtectiveMbrType = 0xEE;

    /// <summary>分区表项数量上限（解析时校验）。 Maximum partition-entry count (parse bound).</summary>
    public const int MaxEntryCount = 1024;

    /// <summary>
    /// <para>在头部附近搜索条目数组的最大字节距离。标准磁盘上数组紧随头部或由头部声明的 LBA
    /// 给出；EDL 合并 GPT 镜像把头部与数组聚在头部附近的少量扇区内。</para>
    /// Maximum byte distance searched for a header's entry array. On a standard disk the array follows the
    /// header or is given by its declared LBA; EDL combined GPT images keep their headers and arrays
    /// clustered close to each other.
    /// </summary>
    private const long MaxMetadataDistance = 1024 * 1024;

    /// <summary>GPT 头签名（"EFI PART"）。 GPT header signature ("EFI PART").</summary>
    public static readonly byte[] Signature = "EFI PART"u8.ToArray();

    /// <summary>默认 Linux 文件系统类型 GUID。 Default Linux filesystem type GUID.</summary>
    public static readonly Guid DefaultTypeGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    /// <summary>
    /// <para>计算 IEEE CRC-32 校验和（多项式 0xEDB88320，初值/异或输出 0xFFFFFFFF）。</para>
    /// Computes the IEEE CRC-32 checksum (polynomial 0xEDB88320, init/XOR-out 0xFFFFFFFF).
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            var crc = 0xFFFF_FFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
                }
            }
            return ~crc;
        }
    }

    /// <summary>
    /// <para>检查给定字节是否在扇区对齐偏移上携带 GPT 签名（<c>EFI PART</c>）。</para>
    /// Checks whether the given bytes carry the GPT signature (<c>EFI PART</c>) at a sector-aligned offset.
    /// </summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) => FindHeaderOffset(data) >= 0;

    /// <summary>
    /// <para>返回首个扇区对齐的 <c>EFI PART</c> 签名偏移，未找到时返回 <c>-1</c>。EDL 合并 GPT 镜像
    /// （<c>gpt_both*.bin</c>）把头部放在非 LBA 1 的位置，因此不能只检查偏移 512。</para>
    /// Returns the first sector-aligned <c>EFI PART</c> signature offset, or <c>-1</c> when there is none.
    /// EDL combined GPT images (<c>gpt_both*.bin</c>) keep their headers away from LBA 1, so offset 512
    /// alone is not a sufficient probe.
    /// </summary>
    public static long FindHeaderOffset(ReadOnlySpan<byte> data)
    {
        for (var search = 0; search < data.Length;)
        {
            var relative = data[search..].IndexOf(Signature);
            if (relative < 0)
            {
                break;
            }

            var offset = search + relative;
            if (offset % SectorSize == 0)
            {
                return offset;
            }

            search = offset + 1;
        }

        return -1;
    }

    /// <summary>
    /// <para>由归一化条目构建 GPT 镜像（唯一 GUID 自动生成，类型 GUID 无法解析时用 Linux 默认值）。</para>
    /// Builds a GPT image from normalized entries (unique GUIDs are generated; the type GUID
    /// falls back to the Linux default when unparseable).
    /// </summary>
    public static byte[] Build(ulong diskSizeBytes, Guid diskGuid, IReadOnlyList<PartitionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var gptEntries = new List<GptEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.SizeSectors == 0)
            {
                throw new ArgumentException($"GPT partition '{entry.Name}' has zero size. 分区大小不能为零。", nameof(entries));
            }

            var typeGuid = Guid.TryParse(entry.Type, out var parsed) ? parsed : DefaultTypeGuid;
            var first = checked(entry.StartSector);
            var last = checked(entry.StartSector + entry.SizeSectors - 1);
            gptEntries.Add(new GptEntry
            {
                TypeGuid = typeGuid,
                UniqueGuid = Guid.NewGuid(),
                FirstLba = first,
                LastLba = last,
                Attributes = 0,
                Name = entry.Name
            });
        }

        return Build(diskSizeBytes, diskGuid, gptEntries);
    }

    /// <summary>
    /// <para>由 GPT 条目构建完整 GPT 镜像：扇区 0 保护性 MBR、扇区 1 主 GPT 头、扇区 2 起分区表项，
    /// 并正确计算头与分区表的 CRC-32。</para>
    /// Builds a complete GPT image: sector 0 protective MBR, sector 1 primary GPT header,
    /// partition entries from sector 2, with correct header and entry-array CRC-32 values.
    /// </summary>
    public static byte[] Build(ulong diskSizeBytes, Guid diskGuid, IReadOnlyList<GptEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("GPT requires at least one partition. GPT 至少需要一个分区。", nameof(entries));
        }

        if (entries.Count > MaxEntryCount)
        {
            throw new ArgumentException($"GPT partition count {entries.Count} exceeds {MaxEntryCount}. 分区数量超限。", nameof(entries));
        }

        var diskSectors = checked(diskSizeBytes / SectorSize);
        if (diskSectors < FirstUsableLba + FirstUsableLba)
        {
            throw new ArgumentException($"Disk size {diskSizeBytes} is too small for GPT. 磁盘太小，无法容纳 GPT 头。", nameof(diskSizeBytes));
        }

        var lastUsableLba = checked(diskSectors - FirstUsableLba - 1);
        var entryBytes = checked(entries.Count * EntrySize);
        var entrySectors = checked((entryBytes + SectorSize - 1) / SectorSize);
        var imageLength = checked((long)((EntriesStartLba + (ulong)entrySectors) * SectorSize));

        var image = new byte[imageLength];

        WriteProtectiveMbr(image, diskSectors);

        var header = image.AsSpan(HeaderOffset, HeaderSize);
        Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], PrimaryHeaderLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], checked(diskSectors - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], lastUsableLba);
        WriteGuidBigEndian(header[56..], diskGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], EntriesStartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[EntriesCrcOffset..], 0);

        var entriesSpan = image.AsSpan((int)checked(EntriesStartLba * SectorSize), entryBytes);
        for (var i = 0; i < entries.Count; i++)
        {
            WriteEntry(entriesSpan.Slice(i * EntrySize, EntrySize), entries[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[EntriesCrcOffset..], Crc32(entriesSpan));
        BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], Crc32(header));
        return image;
    }

    /// <summary>
    /// <para>解析 GPT 镜像：定位每个扇区对齐的头部，校验头 CRC，并按声明的条目 LBA 或 CRC 搜索定位
    /// 条目数组；以首个有效副本作为分区表来源。</para>
    /// Parses a GPT image: locates every sector-aligned header, validates the header CRC, resolves each
    /// entry array from its declared LBA or a bounded CRC search, and takes the first valid copy as the
    /// partition-table source.
    /// </summary>
    public static GptTableInfo Parse(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!TryDetect(image, out _, out var entries, out var diskSizeBytes, out var diskGuid))
        {
            throw new ArgumentException(
                "No GPT header with a CRC-verified entry array was found. 未找到 CRC 校验通过的 GPT 头与条目数组。",
                nameof(image));
        }

        return new GptTableInfo { DiskSizeBytes = diskSizeBytes, DiskGuid = diskGuid, Entries = entries };
    }

    /// <summary>
    /// <para>原位回写分区表项：把条目按顺序紧凑写入镜像中每个已定位的条目数组，保留文件长度、未建模
    /// 区域与头部几何字段，只更新条目数量与两处 CRC-32。槽位不足时抛出异常，不会改写相邻结构。</para>
    /// Writes partition entries back in place: the entries are stored compactly in every located entry
    /// array while the file length, the unmodelled regions and the header geometry fields are preserved;
    /// only the entry count and the two CRC-32 values change. A full array throws instead of overwriting
    /// a neighbouring structure.
    /// </summary>
    public static byte[] Patch(byte[] image, IReadOnlyList<GptEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(entries);
        if (!TryDetect(image, out var copies, out _, out _, out _))
        {
            throw new ArgumentException(
                "No GPT header with a CRC-verified entry array was found. 未找到 CRC 校验通过的 GPT 头与条目数组。",
                nameof(image));
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException("GPT requires at least one partition. GPT 至少需要一个分区。", nameof(entries));
        }

        var slotCount = Math.Max(copies.Max(copy => (int)copy.EntryCount), entries.Count);
        if (slotCount > MaxEntryCount)
        {
            throw new ArgumentException($"GPT partition count {slotCount} exceeds {MaxEntryCount}. 分区数量超限。", nameof(entries));
        }

        var arrayBytes = slotCount * EntrySize;
        foreach (var copy in copies)
        {
            if (arrayBytes > FreeBytesAfter(image, copies, copy))
            {
                throw new ArgumentException(
                    $"The GPT entry array at offset {copy.EntriesOffset} cannot hold {slotCount} slots. 分区表槽位已满，无法写入更多分区。",
                    nameof(entries));
            }
        }

        var result = (byte[])image.Clone();
        foreach (var copy in copies)
        {
            var array = result.AsSpan((int)copy.EntriesOffset, arrayBytes);
            array.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                WriteEntry(array.Slice(i * EntrySize, EntrySize), entries[i]);
            }

            var header = result.AsSpan((int)copy.HeaderOffset, copy.HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[EntriesCrcOffset..], Crc32(array));
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)slotCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], Crc32(header));
        }

        return result;
    }

    private static bool TryDetect(byte[] image, out List<GptCopy> copies, out List<GptEntry> entries, out ulong diskSizeBytes, out Guid diskGuid)
    {
        copies = [];
        entries = [];
        diskSizeBytes = 0;
        diskGuid = Guid.Empty;

        for (var headerOffset = 0L; headerOffset + HeaderSize <= image.Length; headerOffset += SectorSize)
        {
            if (!image.AsSpan((int)headerOffset, Signature.Length).SequenceEqual(Signature)
                || !TryReadCopy(image, headerOffset, out var copy))
            {
                continue;
            }

            copies.Add(copy);
            if (copies.Count > 1)
            {
                continue;
            }

            var header = image.AsSpan((int)copy.HeaderOffset, HeaderSize);
            entries = ReadEntries(image.AsSpan((int)copy.EntriesOffset, (int)copy.EntryCount * EntrySize), copy.EntryCount);
            diskGuid = ReadGuidBigEndian(header[56..]);
            diskSizeBytes = checked((BinaryPrimitives.ReadUInt64LittleEndian(header[32..]) + 1) * SectorSize);
        }

        return copies.Count > 0;
    }

    private static bool TryReadCopy(byte[] image, long headerOffset, out GptCopy copy)
    {
        copy = default;
        var header = image.AsSpan((int)headerOffset, HeaderSize);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        if (headerSize is < HeaderSize or > SectorSize || entrySize != EntrySize
            || entryCount is 0 or > MaxEntryCount || !HeaderChecksumValid(image, headerOffset, (int)headerSize))
        {
            return false;
        }

        var arrayBytes = (int)(entryCount * entrySize);
        var declared = SectorOffset(BinaryPrimitives.ReadUInt64LittleEndian(header[72..]));
        var entriesOffset = FindEntryArray(image, headerOffset, declared, arrayBytes,
            BinaryPrimitives.ReadUInt32LittleEndian(header[EntriesCrcOffset..]));
        if (entriesOffset < 0)
        {
            return false;
        }

        copy = new GptCopy(headerOffset, (int)headerSize, entriesOffset, entryCount);
        return true;
    }

    private static bool HeaderChecksumValid(byte[] image, long headerOffset, int headerSize)
    {
        var header = image.AsSpan((int)headerOffset, headerSize);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderCrcOffset..]);
        Span<byte> scratch = stackalloc byte[SectorSize];
        header.CopyTo(scratch);
        BinaryPrimitives.WriteUInt32LittleEndian(scratch[HeaderCrcOffset..], 0);
        return stored == Crc32(scratch[..headerSize]);
    }

    /// <summary>
    /// <para>定位条目数组：先按头声明的 LBA 校验 CRC，失败后在头部附近的有界窗口内按扇区对齐搜索，
    /// 取距离该头部最近的匹配。EDL 合并 GPT 镜像里声明 LBA 是打包时的占位值，且主备数组内容相同，
    /// 只有 CRC 加最近距离才能把每个头部配到自己的数组。</para>
    /// Locates the entry array: the declared LBA is verified first, then a bounded sector-aligned window
    /// around the header is searched and the closest match wins. In EDL combined GPT images the declared
    /// LBA is a packaging placeholder and both copies hold identical arrays, so only the CRC plus the
    /// shortest distance pairs each header with its own array.
    /// </summary>
    private static long FindEntryArray(byte[] image, long headerOffset, long declared, int arrayBytes, uint expectedCrc)
    {
        if (arrayBytes > image.Length)
        {
            return -1;
        }

        if (ChecksumMatches(image, declared, arrayBytes, expectedCrc))
        {
            return declared;
        }

        var low = Math.Max(0, headerOffset - MaxMetadataDistance) & ~(SectorSize - 1);
        var high = Math.Min(image.Length - arrayBytes, headerOffset + MaxMetadataDistance);
        var best = -1L;
        var bestDistance = long.MaxValue;
        for (var offset = low; offset <= high; offset += SectorSize)
        {
            if (offset == declared || Math.Abs(offset - headerOffset) >= bestDistance)
            {
                continue;
            }

            if (ChecksumMatches(image, offset, arrayBytes, expectedCrc))
            {
                best = offset;
                bestDistance = Math.Abs(offset - headerOffset);
            }
        }

        return best;
    }

    private static bool ChecksumMatches(byte[] image, long offset, int arrayBytes, uint expectedCrc) =>
        offset >= 0 && offset + arrayBytes <= image.Length
        && Crc32(image.AsSpan((int)offset, arrayBytes)) == expectedCrc;

    private static long FreeBytesAfter(byte[] image, IReadOnlyList<GptCopy> copies, GptCopy copy)
    {
        var boundary = (long)image.Length;
        foreach (var other in copies)
        {
            if (other.EntriesOffset > copy.EntriesOffset)
            {
                boundary = Math.Min(boundary, other.EntriesOffset);
            }

            if (other.HeaderOffset > copy.EntriesOffset)
            {
                boundary = Math.Min(boundary, other.HeaderOffset);
            }
        }

        return boundary - copy.EntriesOffset;
    }

    private static List<GptEntry> ReadEntries(ReadOnlySpan<byte> array, uint entryCount)
    {
        var list = new List<GptEntry>((int)entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var slot = array.Slice(i * EntrySize, EntrySize);
            var typeGuid = ReadGuidBigEndian(slot);
            var first = BinaryPrimitives.ReadUInt64LittleEndian(slot[32..]);
            var last = BinaryPrimitives.ReadUInt64LittleEndian(slot[40..]);
            var name = ReadName(slot[56..]);

            if (typeGuid == Guid.Empty && name.Length == 0 && first == 0 && last == 0)
            {
                continue;
            }

            list.Add(new GptEntry
            {
                TypeGuid = typeGuid,
                UniqueGuid = ReadGuidBigEndian(slot[16..]),
                FirstLba = first,
                LastLba = last,
                Attributes = BinaryPrimitives.ReadUInt64LittleEndian(slot[48..]),
                Name = name
            });
        }

        return list;
    }

    private static void WriteEntry(Span<byte> slot, GptEntry entry)
    {
        WriteGuidBigEndian(slot, entry.TypeGuid);
        WriteGuidBigEndian(slot[16..], entry.UniqueGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], entry.FirstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], entry.LastLba);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[48..], entry.Attributes);
        WriteName(slot[56..], entry.Name);
    }

    private static long SectorOffset(ulong lba) =>
        lba > (ulong)(long.MaxValue / SectorSize) ? -1 : (long)lba * SectorSize;

    /// <summary>
    /// <para>头部与其条目数组在源镜像中的定位。</para>
    /// A header and its entry array located inside the source image.
    /// </summary>
    private readonly record struct GptCopy(long HeaderOffset, int HeaderSize, long EntriesOffset, uint EntryCount);

    private static void WriteProtectiveMbr(Span<byte> image, ulong diskSectors)
    {
        var slot = image.Slice(446, 16);
        slot[0] = 0;
        slot[4] = ProtectiveMbrType;
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], 1);
        var size = Math.Min(diskSectors, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], (uint)size);
        image[510] = 0x55;
        image[511] = 0xAA;
    }

    private static void WriteGuidBigEndian(Span<byte> target, Guid guid)
    {
        var bytes = guid.ToByteArray();
        BinaryPrimitives.WriteUInt32BigEndian(target, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        BinaryPrimitives.WriteUInt16BigEndian(target[4..], BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]));
        BinaryPrimitives.WriteUInt16BigEndian(target[6..], BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
        bytes.AsSpan(8, 8).CopyTo(target[8..]);
    }

    private static Guid ReadGuidBigEndian(ReadOnlySpan<byte> source)
    {
        var a = BinaryPrimitives.ReadUInt32BigEndian(source);
        var b = BinaryPrimitives.ReadUInt16BigEndian(source[4..]);
        var c = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        return new Guid((int)a, (short)b, (short)c, source.Slice(8, 8).ToArray());
    }

    private static void WriteName(Span<byte> target, string name)
    {
        var length = Math.Min(name.Length, MaxNameUnits);
        Encoding.Unicode.GetBytes(name.AsSpan(0, length), target[..(length * 2)]);
    }

    private static string ReadName(ReadOnlySpan<byte> source)
    {
        var units = source[..(MaxNameUnits * 2)];
        var end = 0;
        while (end + 1 < units.Length && (units[end] != 0 || units[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(units[..end]);
    }
}