using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

internal sealed record F2fsDirectoryEntry
{
    public const int DirectoryEntrySize = 11;

    public const int SlotLength = 8;

    public const int SlotsPerBlock = 214;

    public const int BitmapSize = 27;

    public const int ReservedSize = 3;

    public const int DentriesOffset = BitmapSize + ReservedSize;

    public const int FilenamesOffset = DentriesOffset + SlotsPerBlock * DirectoryEntrySize;

    public const byte TypeUnknown = 0;

    public const byte TypeRegularFile = 1;

    public const byte TypeDirectory = 2;

    public const byte TypeSymlink = 7;

    public uint HashCode { get; init; }

    public uint Ino { get; init; }

    public ushort NameLen { get; init; }

    public byte FileType { get; init; }

    public string Name { get; init; } = string.Empty;

    public int SlotCount => (NameLen + SlotLength - 1) / SlotLength;

    /// <summary>
    /// Tests whether bit index is set in a f2fs bitmap (LSB-first within each byte).
    /// <para>判断位图中第 index 位是否置位（f2fs 位图为每字节 LSB-first）。</para>
    /// </summary>
    /// <param name="bitmap">Bitmap bytes to inspect; the addressable width is eight times its length.
    /// <para>待检查的位图字节，可寻址宽度为其字节长度的 8 倍。</para></param>
    /// <param name="index">Zero-based bit index, least significant bit first inside each byte; an index outside the bitmap reports a cleared bit instead of throwing.
    /// <para>从 0 开始的位序号，每个字节内按低位在前；超出位图范围时按未置位返回，不会抛出异常。</para></param>
    /// <returns><see langword="true"/> when the addressed bit is set.
    /// <para>被寻址的位已置位时返回 <see langword="true"/>。</para></returns>
    public static bool TestBit(ReadOnlySpan<byte> bitmap, int index)
        => index >= 0 && index < bitmap.Length * 8
           && (bitmap[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>
    /// Sets bit index in a f2fs bitmap.
    /// <para>设置位图第 index 位。</para>
    /// </summary>
    /// <param name="bitmap">Writable bitmap bytes; the bit is OR-ed into the byte holding it and no other byte changes.
    /// <para>可写的位图字节；该位按或运算写入所属字节，其他字节不会变动。</para></param>
    /// <param name="index">Zero-based bit index, least significant bit first inside each byte, which must fall inside the first <c>bitmap.Length * 8</c> bits.
    /// <para>从 0 开始的位序号，每个字节内按低位在前，必须落在前 <c>bitmap.Length * 8</c> 位之内。</para></param>
    public static void SetBit(Span<byte> bitmap, int index)
    {
        if (index < 0 || index >= bitmap.Length * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        bitmap[index >> 3] |= (byte)(1 << (index & 7));
    }

    /// <summary>
    /// Parses a single 11-byte entry (without the name).
    /// <para>解析单个 11 字节条目（不含名称）。</para>
    /// </summary>
    /// <param name="data">Entry bytes holding the little-endian hash code at 0, inode number at 4, name length at 8 and file-type byte at 10; at least <see cref="DirectoryEntrySize"/> bytes must be present.
    /// <para>条目字节依次为偏移 0 的小端哈希码、偏移 4 的 inode 号、偏移 8 的名称长度与偏移 10 的文件类型字节；至少要有 <see cref="DirectoryEntrySize"/> 字节。</para></param>
    /// <param name="name">Name text already decoded from the filename area and stored verbatim; an empty string produces an entry with no name.
    /// <para>已从文件名区域解码并原样保存的名称文本；空字符串会产生一条没有名称的条目。</para></param>
    /// <returns>Entry record whose slot demand derives from the on-disk name length rather than from <paramref name="name"/>.
    /// <para>返回的条目记录，其槽位需求取自磁盘上的名称长度，而不是 <paramref name="name"/> 的长度。</para></returns>
    public static F2fsDirectoryEntry FromBytes(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length < DirectoryEntrySize)
        {
            throw new FormatException("F2FS directory entry region too short.");
        }

        return new F2fsDirectoryEntry
        {
            HashCode = BinaryPrimitives.ReadUInt32LittleEndian(data),
            Ino = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)),
            NameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)),
            FileType = data[10],
            Name = name,
        };
    }

    /// <summary>
    /// Parses a flat F2FS directory data area into entries in slot order.
    /// <para>把扁平的 F2FS 目录数据区域按槽顺序解析为条目。</para>
    /// </summary>
    /// <param name="area">Flat directory data area; the bitmap occupies its first <paramref name="bitmapSize"/> bytes, then <paramref name="reservedSize"/> bytes, then <paramref name="slotCount"/> 11-byte dentry records and finally <paramref name="slotCount"/> 8-byte name slots.
    /// <para>扁平的目录数据区域：前 <paramref name="bitmapSize"/> 字节是位图，随后 <paramref name="reservedSize"/> 字节保留，接着是 <paramref name="slotCount"/> 条 11 字节 dentry 记录，最后是 <paramref name="slotCount"/> 个 8 字节名称槽。</para></param>
    /// <param name="bitmapSize">Bytes of the leading allocation bitmap, each bit marking one slot as in use.
    /// <para>前导分配位图的字节数，每一位标记一个槽是否在用。</para></param>
    /// <param name="reservedSize">Bytes skipped between the bitmap and the dentry array.
    /// <para>位图与 dentry 数组之间跳过的字节数。</para></param>
    /// <param name="slotCount">Number of 8-byte slots the area provides, bounding both arrays and the iteration.
    /// <para>该区域提供的 8 字节槽数量，同时限定两个数组范围与遍历上限。</para></param>
    /// <param name="blockLength">Minimum bytes required in <paramref name="area"/> before any field is read; a shorter area is reported as truncated.
    /// <para>读取任何字段前 <paramref name="area"/> 必须具有的最少字节数，不足时按截断上报。</para></param>
    /// <returns>Entries whose slot bit is set and whose inode is non-zero, in slot order; the walk stops early when a record claims no slot or its name would extend past the last slot.
    /// <para>返回槽位已置位且 inode 非零的条目，按槽顺序排列；某条记录声明的槽数为 0，或其名称越过最后一个槽时提前结束遍历。</para></returns>
    public static List<F2fsDirectoryEntry> ParseArea(
        ReadOnlySpan<byte> area,
        int bitmapSize,
        int reservedSize,
        int slotCount,
        int blockLength)
    {
        if (area.Length < blockLength)
        {
            throw new FormatException("F2FS directory area is truncated.");
        }

        var bitmap = area[..bitmapSize];
        var dentries = area.Slice(bitmapSize + reservedSize, slotCount * DirectoryEntrySize);
        var filenames = area.Slice(bitmapSize + reservedSize + slotCount * DirectoryEntrySize, slotCount * SlotLength);
        var result = new List<F2fsDirectoryEntry>();
        var position = 0;
        while (position < slotCount)
        {
            if (!TestBit(bitmap, position))
            {
                position++;
                continue;
            }

            var dentry = dentries.Slice(position * DirectoryEntrySize, DirectoryEntrySize);
            var ino = BinaryPrimitives.ReadUInt32LittleEndian(dentry.Slice(4));
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dentry.Slice(8));
            var fileType = dentry[10];
            var slots = (nameLen + SlotLength - 1) / SlotLength;
            if (slots <= 0 || position + slots > slotCount)
            {

                break;
            }

            var nameLength = Math.Min(nameLen, slots * SlotLength);
            var name = Extraction.PortableExtractionPaths.NameEncoding.GetString(filenames.Slice(position * SlotLength, nameLength));
            if (ino != 0)
            {
                result.Add(new F2fsDirectoryEntry
                {
                    HashCode = BinaryPrimitives.ReadUInt32LittleEndian(dentry),
                    Ino = ino,
                    NameLen = nameLen,
                    FileType = fileType,
                    Name = name,
                });
            }

            position += slots;
        }

        return result;
    }

    /// <summary>
    /// Parses one full dentry block (4096 bytes).
    /// <para>解析一个完整目录块（块大小 4096）。</para>
    /// </summary>
    /// <param name="block">Bytes of one dentry block in the standard layout: <see cref="BitmapSize"/> bitmap bytes, <see cref="ReservedSize"/> reserved bytes, then <see cref="SlotsPerBlock"/> dentry records and name slots; shorter input is reported as a truncated area.
    /// <para>单个 dentry 块的字节，采用标准布局：<see cref="BitmapSize"/> 字节位图、<see cref="ReservedSize"/> 字节保留区，随后 <see cref="SlotsPerBlock"/> 条 dentry 记录与名称槽；输入过短会按区域截断上报。</para></param>
    /// <returns>Live entries decoded from the block, in slot order.
    /// <para>按槽顺序返回该块中仍然有效的条目。</para></returns>
    public static List<F2fsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block)
        => ParseArea(block, BitmapSize, ReservedSize, SlotsPerBlock, F2fsNode.NodeSize);

    /// <summary>
    /// Builds a dentry block by appending entries in order: the dentry struct is written at the first slot, the name spans slots, and every slot bit is set.
    /// <para>按顺序逐项写入构建目录块：首槽写 dentry 结构，名称跨槽存储，每槽置位，并生成哈希码与文件类型。</para>
    /// </summary>
    /// <param name="block">Destination buffer with at least <paramref name="blockSize"/> writable bytes; the whole buffer is cleared before the first entry is written and a shorter buffer is rejected.
    /// <para>目标缓冲区，可写字节至少为 <paramref name="blockSize"/>；写入首条 entry 前会清空整个缓冲区，长度不足时被拒绝。</para></param>
    /// <param name="entries">Entries appended in order, each starting at the next free slot: the 11-byte record at <see cref="DentriesOffset"/> carries the hash code, inode, the length of the UTF-8 name bytes and the file type, the name is split across <see cref="SlotLength"/>-byte slots from <see cref="FilenamesOffset"/>, and one bitmap bit is set per slot. The slot reservation is derived from the same UTF-8 byte count that is written into the record, so <c>NameLen</c> must match it, and every name is checked against the upstream <c>F2FS_NAME_LEN</c> bound before a byte is written. An empty list leaves the block zeroed.
    /// <para>按顺序追加的条目，每条从下一个空闲槽开始：<see cref="DentriesOffset"/> 处的 11 字节记录写入哈希码、inode、UTF-8 名称字节长度与文件类型；名称按 <see cref="SlotLength"/> 字节从 <see cref="FilenamesOffset"/> 起跨槽存放，每个槽都置一位。槽位占用与写入记录的 UTF-8 字节数同源，因此 <c>NameLen</c> 必须与它相等，且每个名称在写入任何字节前都会按上游 <c>F2FS_NAME_LEN</c> 上限校验。空列表只会留下全零块。</para></param>
    /// <param name="blockSize">Bytes the caller treats as one dentry block, checked against the buffer length; slot capacity is always <see cref="SlotsPerBlock"/> regardless of it.
    /// <para>调用方认定的单个 dentry 块字节数，只用于校验缓冲区长度；槽位容量始终为 <see cref="SlotsPerBlock"/>。</para></param>
    /// <exception cref="InvalidOperationException">A name exceeds the on-disk name limit, its <c>NameLen</c> disagrees with the UTF-8 encoding of <c>Name</c>, or the reserved slots run past the block.
    /// <para>名称超出磁盘名称上限、<c>NameLen</c> 与 <c>Name</c> 的 UTF-8 编码不一致，或占用的槽数超出该块。</para></exception>
    public static void BuildBlock(Span<byte> block, IReadOnlyList<F2fsDirectoryEntry> entries, int blockSize)
    {
        if (block.Length < blockSize)
        {
            throw new ArgumentException("Dentry block buffer is too small.", nameof(block));
        }

        block.Clear();
        var used = 0;
        foreach (var entry in entries)
        {
            FileSystemNameLimits.Validate(entry.Name, "F2FS");
            var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            if (entry.NameLen != nameBytes.Length)
            {
                throw new InvalidOperationException(
                    $"Directory entry '{entry.Name}' has an inconsistent name length ({entry.NameLen} stored, {nameBytes.Length} encoded).");
            }

            var slots = (nameBytes.Length + SlotLength - 1) / SlotLength;
            if (used + slots > SlotsPerBlock)
            {
                throw new InvalidOperationException(
                    $"Directory does not fit into one dentry block (needs slot {used + slots} of {SlotsPerBlock}).");
            }

            var dentry = block.Slice(DentriesOffset + used * DirectoryEntrySize, DirectoryEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(dentry, entry.HashCode);
            BinaryPrimitives.WriteUInt32LittleEndian(dentry.Slice(4), entry.Ino);
            BinaryPrimitives.WriteUInt16LittleEndian(dentry.Slice(8), (ushort)nameBytes.Length);
            dentry[10] = entry.FileType;
            for (var slot = 0; slot < slots; slot++)
            {
                SetBit(block, used + slot);
                var slotOffset = (used + slot) * SlotLength;
                var nameOffset = slot * SlotLength;
                var copyLength = Math.Min(nameBytes.Length - nameOffset, SlotLength);
                if (copyLength > 0)
                {
                    nameBytes.AsSpan(nameOffset, copyLength).CopyTo(block.Slice(FilenamesOffset + slotOffset, copyLength));
                }
            }

            used += slots;
        }
    }
}