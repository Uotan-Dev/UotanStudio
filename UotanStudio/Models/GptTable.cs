using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Models;

/// <summary>
/// A UEFI GPT partition-table entry (128-byte partition entry).
/// <para>UEFI GPT 分区表条目（128 字节分区表项）。</para>
/// </summary>
public sealed class GptEntry
{
    /// <summary>
    /// Partition type GUID.
    /// <para>分区类型 GUID。</para>
    /// </summary>
    /// <value>The GUID identifying the partition type.<para>标识分区类型的 GUID。</para></value>
    public Guid TypeGuid { get; init; }

    /// <summary>
    /// Unique partition GUID.
    /// <para>分区唯一 GUID。</para>
    /// </summary>
    /// <value>The GUID unique to this partition.<para>本分区独有的 GUID。</para></value>
    public Guid UniqueGuid { get; init; }

    /// <summary>
    /// First LBA.
    /// <para>起始 LBA。</para>
    /// </summary>
    /// <value>The first LBA of the partition.<para>分区的起始 LBA。</para></value>
    public ulong FirstLba { get; init; }

    /// <summary>
    /// Last LBA (inclusive).
    /// <para>结束 LBA（含）。</para>
    /// </summary>
    /// <value>The last LBA of the partition, inclusive.<para>分区的结束 LBA（含）。</para></value>
    public ulong LastLba { get; init; }

    /// <summary>
    /// Partition attributes.
    /// <para>分区属性位。</para>
    /// </summary>
    /// <value>The 64-bit partition attribute flags.<para>64 位分区属性标志。</para></value>
    public ulong Attributes { get; init; }

    /// <summary>
    /// Partition name (up to 36 UTF-16 units).
    /// <para>分区名称（最多 36 个 UTF-16 单元）。</para>
    /// </summary>
    /// <value>The partition name stored in the entry.<para>存储于表项中的分区名称。</para></value>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Parsed GPT partition-table information.
/// <para>解析出的 GPT 分区表信息。</para>
/// </summary>
public sealed class GptTableInfo
{
    /// <summary>
    /// Disk size in bytes (derived from the backup header LBA).
    /// <para>磁盘大小（字节，由备份头 LBA 推算）。</para>
    /// </summary>
    /// <value>The disk size derived from the backup header LBA.<para>由备份头 LBA 推算出的磁盘大小。</para></value>
    public ulong DiskSizeBytes { get; init; }

    /// <summary>
    /// Disk GUID.
    /// <para>磁盘 GUID。</para>
    /// </summary>
    /// <value>The GUID identifying the disk.<para>标识磁盘的 GUID。</para></value>
    public Guid DiskGuid { get; init; }

    /// <summary>
    /// Partition entries.
    /// <para>分区表项。</para>
    /// </summary>
    /// <value>The partition entries of the table.<para>分区表中的分区表项。</para></value>
    public IReadOnlyList<GptEntry> Entries { get; init; } = [];
}

/// <summary>
/// UEFI GPT partition-table builder and parser (protective MBR + primary GPT header +
/// partition entries; integers are little-endian, GUIDs are stored big-endian per EFI spec).
/// <para>UEFI GPT 分区表构建与解析（保护性 MBR + 主 GPT 头 + 分区表项；整数均为小端，
/// GUID 按 EFI 规范大端存储）。</para>
/// </summary>
public static class GptTable
{
    /// <summary>Sector size in bytes.<para>扇区大小（字节）。</para></summary>
    public const int SectorSize = 512;

    /// <summary>GPT header byte offset on disk.<para>GPT 头在磁盘中的字节偏移。</para></summary>
    public const int HeaderOffset = 512;

    /// <summary>Single partition-entry size in bytes.<para>单个分区表项大小（字节）。</para></summary>
    public const int EntrySize = 128;

    /// <summary>GPT header size in bytes.<para>GPT 头大小（字节）。</para></summary>
    public const int HeaderSize = 92;

    /// <summary>Maximum partition-name UTF-16 units.<para>分区名最大 UTF-16 单元数。</para></summary>
    public const int MaxNameUnits = 36;

    /// <summary>Header CRC-32 field offset within the header.<para>头 CRC-32 字段在头内的偏移。</para></summary>
    public const int HeaderCrcOffset = 16;

    /// <summary>Partition-entry-array CRC-32 field offset within the header.<para>分区表项 CRC-32 字段在头内的偏移。</para></summary>
    public const int EntriesCrcOffset = 88;

    /// <summary>Partition-entry-array start LBA.<para>分区表项起始 LBA。</para></summary>
    public const ulong EntriesStartLba = 2;

    /// <summary>First usable LBA, the header and entries occupying 34 sectors.<para>首个可用 LBA，头与分区表占 34 个扇区。</para></summary>
    public const ulong FirstUsableLba = 34;

    /// <summary>Primary GPT header LBA.<para>主 GPT 头所在 LBA。</para></summary>
    public const ulong PrimaryHeaderLba = 1;

    /// <summary>GPT header revision number.<para>GPT 头修订号。</para></summary>
    public const uint Revision = 0x0001_0000;

    /// <summary>Protective MBR partition type.<para>保护性 MBR 分区类型。</para></summary>
    public const byte ProtectiveMbrType = 0xEE;

    /// <summary>Maximum partition-entry count checked while parsing.<para>解析时校验的分区表项数量上限。</para></summary>
    public const int MaxEntryCount = 1024;

    /// <summary>
    /// Maximum byte distance searched around a header for its entry array.
    /// <para>在头部附近搜索条目数组的最大字节距离。</para>
    /// </summary>
    private const long MaxMetadataDistance = 1024 * 1024;

    /// <summary>GPT header signature ("EFI PART").<para>GPT 头签名（"EFI PART"）。</para></summary>
    public static readonly byte[] Signature = "EFI PART"u8.ToArray();

    /// <summary>Default Linux filesystem type GUID.<para>默认 Linux 文件系统类型 GUID。</para></summary>
    public static readonly Guid DefaultTypeGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    /// <summary>
    /// Checks whether the given bytes carry the GPT signature (<c>EFI PART</c>) at a sector-aligned offset.
    /// <para>检查给定字节是否在扇区对齐偏移上携带 GPT 签名（<c>EFI PART</c>）。</para>
    /// </summary>
    /// <param name="data">The bytes to inspect.<para>待检查的字节。</para></param>
    /// <returns>True when a sector-aligned signature is present.<para>存在扇区对齐签名时为真。</para></returns>
    public static bool HasSignature(ReadOnlySpan<byte> data) => FindHeaderOffset(data) >= 0;

    /// <summary>
    /// Returns the first sector-aligned <c>EFI PART</c> signature offset, or <c>-1</c> when there is none.
    /// <para>返回首个扇区对齐的 <c>EFI PART</c> 签名偏移，未找到时返回 <c>-1</c>。</para>
    /// </summary>
    /// <param name="data">The bytes to scan.<para>待扫描的字节。</para></param>
    /// <returns>The byte offset of the first aligned signature, or <c>-1</c>.<para>首个对齐签名的字节偏移，未找到时为 <c>-1</c>。</para></returns>
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
    /// Parses a GPT image: locates every sector-aligned header, validates the header CRC, resolves
    /// each entry array, and takes the first valid copy as the partition-table source.
    /// <para>解析 GPT 镜像：定位每个扇区对齐的头部，校验头 CRC，解析每个条目数组，
    /// 并以首个有效副本作为分区表来源。</para>
    /// </summary>
    /// <param name="image">The image bytes to parse.<para>待解析的镜像字节。</para></param>
    /// <returns>The disk size, disk GUID and parsed partition entries.<para>磁盘大小、磁盘 GUID 与解析出的分区表项。</para></returns>
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
    /// Writes partition entries back in place: every located entry array is rewritten compactly,
    /// geometry fields and surrounding bytes are preserved, and the count and CRC-32 values update.
    /// <para>原位回写分区表项：紧凑重写每个已定位的条目数组，保留几何字段与周边字节，并更新条目数量与 CRC-32。</para>
    /// </summary>
    /// <param name="image">The source image providing the headers and entry arrays.<para>提供头部与条目数组的源镜像。</para></param>
    /// <param name="entries">The partition entries to write.<para>待写入的分区表项。</para></param>
    /// <returns>A copy of the image with the rewritten entry arrays.<para>改写条目数组后的镜像副本。</para></returns>
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
            BinaryPrimitives.WriteUInt32LittleEndian(header[EntriesCrcOffset..], Crc32Ieee.Compute(array));
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)slotCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], Crc32Ieee.Compute(header));
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
        return stored == Crc32Ieee.Compute(scratch[..headerSize]);
    }

    /// <summary>
    /// Locates the entry array: the offset derived from the declared LBA is verified first, then a
    /// bounded sector-aligned window around the header is searched and the closest CRC match wins.
    /// <para>定位条目数组：先校验由声明 LBA 换算出的偏移，失败后在头部附近的有界扇区对齐窗口内搜索，取距离最近的 CRC 匹配。</para>
    /// </summary>
    /// <param name="image">The source image bytes.<para>源镜像字节。</para></param>
    /// <param name="headerOffset">Byte offset of the header being paired with an array.<para>待与数组配对的头部字节偏移。</para></param>
    /// <param name="declared">Entry offset derived from the declared entry-array LBA.<para>由声明的条目数组 LBA 换算出的偏移。</para></param>
    /// <param name="arrayBytes">Size in bytes of the entry array.<para>条目数组的字节大小。</para></param>
    /// <param name="expectedCrc">Stored CRC-32 of the entry array.<para>条目数组已存储的 CRC-32。</para></param>
    /// <returns>The byte offset of a matching array, or <c>-1</c> when none is found.<para>匹配数组的字节偏移，未找到时为 <c>-1</c>。</para></returns>
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
        && Crc32Ieee.Compute(image.AsSpan((int)offset, arrayBytes)) == expectedCrc;

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
    /// A header and its entry array located inside the source image.
    /// <para>头部与其条目数组在源镜像中的定位。</para>
    /// </summary>
    /// <param name="HeaderOffset">Byte offset of the header within the image.<para>头部在镜像内的字节偏移。</para></param>
    /// <param name="HeaderSize">Header size in bytes as recorded by the header.<para>头部自记的字节大小。</para></param>
    /// <param name="EntriesOffset">Byte offset of the entry array within the image.<para>条目数组在镜像内的字节偏移。</para></param>
    /// <param name="EntryCount">Declared number of partition entries.<para>声明的分区表项数量。</para></param>
    private readonly record struct GptCopy(long HeaderOffset, int HeaderSize, long EntriesOffset, uint EntryCount);

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