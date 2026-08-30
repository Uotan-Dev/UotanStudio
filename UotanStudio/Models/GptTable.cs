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
    /// <para>检查给定字节是否携带 GPT 签名（"EFI PART" @512）。</para>
    /// Checks whether the given bytes carry the GPT signature ("EFI PART" @512).
    /// </summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderOffset + Signature.Length &&
        data.Slice(HeaderOffset, Signature.Length).SequenceEqual(Signature);

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

        // 扇区 0：保护性 MBR（仅第一个槽位）。
        // Sector 0: protective MBR (first slot only).
        WriteProtectiveMbr(image, diskSectors);

        // 扇区 1：主 GPT 头。
        // Sector 1: primary GPT header.
        var header = image.AsSpan(HeaderOffset, HeaderSize);
        Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], 0); // CRC 先置 0，稍后回填。
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0); // reserved
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], PrimaryHeaderLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], checked(diskSectors - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], lastUsableLba);
        WriteGuidBigEndian(header[56..], diskGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], EntriesStartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[EntriesCrcOffset..], 0); // 稍后回填。

        // 扇区 2 起：分区表项。
        // Partition entries from sector 2.
        var entriesSpan = image.AsSpan((int)checked(EntriesStartLba * SectorSize), entryBytes);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var slot = entriesSpan.Slice(i * EntrySize, EntrySize);
            WriteGuidBigEndian(slot, entry.TypeGuid);
            WriteGuidBigEndian(slot[16..], entry.UniqueGuid);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], entry.FirstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], entry.LastLba);
            BinaryPrimitives.WriteUInt64LittleEndian(slot[48..], entry.Attributes);
            WriteName(slot[56..], entry.Name);
        }

        // 回填 CRC：先分区表项数组，再头（头 CRC 覆盖分区表 CRC 字段）。
        // Backfill CRCs: entry array first, then header (the header CRC covers the entries CRC field).
        BinaryPrimitives.WriteUInt32LittleEndian(header[EntriesCrcOffset..], Crc32(entriesSpan));
        BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], Crc32(header));
        return image;
    }

    /// <summary>
    /// <para>解析 GPT 镜像：校验签名、头大小、条目范围与 CRC-32，返回分区表信息。</para>
    /// Parses a GPT image: validates the signature, header size, entry ranges and CRC-32 values,
    /// returning the partition-table information.
    /// </summary>
    public static GptTableInfo Parse(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length < HeaderOffset + HeaderSize)
        {
            throw new ArgumentException("Image too small for a GPT header. 镜像太小，不包含 GPT 头。", nameof(image));
        }

        if (!HasSignature(image))
        {
            throw new ArgumentException("Missing GPT signature (\"EFI PART\"). 缺少 GPT 签名。", nameof(image));
        }

        var header = image.AsSpan(HeaderOffset, HeaderSize);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (headerSize < HeaderSize)
        {
            throw new ArgumentException($"Invalid GPT header size {headerSize}. GPT 头大小无效。");
        }

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        if (entrySize != EntrySize)
        {
            throw new ArgumentException($"Unsupported GPT entry size {entrySize}. GPT 分区表项大小不受支持。");
        }

        if (entryCount == 0 || entryCount > MaxEntryCount)
        {
            throw new ArgumentException($"Invalid GPT entry count {entryCount}. GPT 分区表项数量无效。");
        }

        var entriesStartLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        var entriesStart = checked(entriesStartLba * SectorSize);
        var entryBytes = checked(entryCount * EntrySize);
        if (entriesStart > (ulong)image.Length || entryBytes > (ulong)image.Length - entriesStart)
        {
            throw new ArgumentException("GPT partition entries exceed the image size. GPT 分区表项超出镜像大小。");
        }

        // 校验分区表项数组 CRC 与头 CRC（头 CRC 计算时 CRC 字段置 0）。
        // Verify the entry-array CRC and the header CRC (the header CRC field is zeroed when recomputing).
        var entriesSpan = image.AsSpan((int)entriesStart, (int)entryBytes);
        var storedEntriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[EntriesCrcOffset..]);
        if (storedEntriesCrc != Crc32(entriesSpan))
        {
            throw new ArgumentException("GPT partition-entry CRC mismatch. GPT 分区表项 CRC 校验失败。");
        }

        Span<byte> headerCopy = stackalloc byte[HeaderSize];
        header.CopyTo(headerCopy);
        BinaryPrimitives.WriteUInt32LittleEndian(headerCopy[HeaderCrcOffset..], 0);
        var storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderCrcOffset..]);
        if (storedHeaderCrc != Crc32(headerCopy))
        {
            throw new ArgumentException("GPT header CRC mismatch. GPT 头 CRC 校验失败。");
        }

        var diskGuid = ReadGuidBigEndian(header[56..]);
        var backupLba = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);

        var list = new List<GptEntry>((int)entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var slot = entriesSpan.Slice(i * EntrySize, EntrySize);
            var first = BinaryPrimitives.ReadUInt64LittleEndian(slot[32..]);
            var last = BinaryPrimitives.ReadUInt64LittleEndian(slot[40..]);
            if (last < first)
            {
                continue;
            }

            list.Add(new GptEntry
            {
                TypeGuid = ReadGuidBigEndian(slot),
                UniqueGuid = ReadGuidBigEndian(slot[16..]),
                FirstLba = first,
                LastLba = last,
                Attributes = BinaryPrimitives.ReadUInt64LittleEndian(slot[48..]),
                Name = ReadName(slot[56..])
            });
        }

        return new GptTableInfo
        {
            DiskSizeBytes = checked((backupLba + 1) * SectorSize),
            DiskGuid = diskGuid,
            Entries = list
        };
    }

    private static void WriteProtectiveMbr(Span<byte> image, ulong diskSectors)
    {
        var slot = image.Slice(446, 16);
        slot[0] = 0; // 不可引导。
        slot[4] = ProtectiveMbrType;
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], 1); // 起始 LBA 1。
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
