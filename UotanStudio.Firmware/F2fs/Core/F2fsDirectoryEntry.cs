using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 目录条目（struct f2fs_dir_entry，11 字节）以及目录块/内联目录的
/// bitmap + reserved + dentry + filename 布局。
/// <para>F2FS directory entry (struct f2fs_dir_entry, 11 bytes) plus the dentry-block and inline
/// directory layout of bitmap + reserved + dentry + filename.</para>
/// </summary>
internal sealed record F2fsDirectoryEntry
{
    /// <summary>条目固定大小（SIZE_OF_DIR_ENTRY = 11）。Fixed entry size.</summary>
    public const int DirectoryEntrySize = 11;

    /// <summary>文件名字段长度（F2FS_SLOT_LEN = 8）。Slot length for a file name.</summary>
    public const int SlotLength = 8;

    /// <summary>每目录块条目数（NR_DENTRY_IN_BLOCK = 214，块大小 4096）。Entries per dentry block.</summary>
    public const int SlotsPerBlock = 214;

    /// <summary>目录块位图大小（SIZE_OF_DENTRY_BITMAP = 27）。Dentry bitmap size in bytes.</summary>
    public const int BitmapSize = 27;

    /// <summary>目录块保留区大小（SIZE_OF_RESERVED = 3）。Reserved size in bytes.</summary>
    public const int ReservedSize = 3;

    /// <summary>dentry 数组在目录块内的字节偏移（30 = 27 + 3）。Byte offset of the dentry array.</summary>
    public const int DentriesOffset = BitmapSize + ReservedSize;

    /// <summary>filename 数组在目录块内的字节偏移（2384 = 30 + 214*11）。Byte offset of the filename array.</summary>
    public const int FilenamesOffset = DentriesOffset + SlotsPerBlock * DirectoryEntrySize;

    /// <summary>dirent 文件类型：未知。Unknown.</summary>
    public const byte TypeUnknown = 0;

    /// <summary>dirent 文件类型：普通文件。Regular file.</summary>
    public const byte TypeRegularFile = 1;

    /// <summary>dirent 文件类型：目录。Directory.</summary>
    public const byte TypeDirectory = 2;

    /// <summary>dirent 文件类型：符号链接。Symbolic link.</summary>
    public const byte TypeSymlink = 7;

    /// <summary>哈希码。Hash code of the file name.</summary>
    public uint HashCode { get; init; }

    /// <summary>inode 号。Inode number.</summary>
    public uint Ino { get; init; }

    /// <summary>名称长度（字节）。Name length in bytes.</summary>
    public ushort NameLen { get; init; }

    /// <summary>文件类型。File type.</summary>
    public byte FileType { get; init; }

    /// <summary>条目名称。Entry name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>名称占用的槽位数（GET_DENTRY_SLOTS(name_len)）。Slots occupied by the name.</summary>
    public int SlotCount => (NameLen + SlotLength - 1) / SlotLength;

    /// <summary>
    /// 判断位图中第 <paramref name="index"/> 位是否置位（f2fs 位图为每字节 LSB-first，
    /// 对齐内核 test_bit_le）。
    /// <para>Tests whether bit <paramref name="index"/> is set in a f2fs bitmap (LSB-first within
    /// each byte, matching the kernel's test_bit_le).</para>
    /// </summary>
    public static bool TestBit(ReadOnlySpan<byte> bitmap, int index)
        => index >= 0 && index < bitmap.Length * 8
           && (bitmap[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>
    /// 设置位图第 <paramref name="index"/> 位。
    /// <para>Sets bit <paramref name="index"/> in a f2fs bitmap.</para>
    /// </summary>
    public static void SetBit(Span<byte> bitmap, int index)
    {
        if (index < 0 || index >= bitmap.Length * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        bitmap[index >> 3] |= (byte)(1 << (index & 7));
    }

    /// <summary>
    /// 解析单个 11 字节条目（不含名称）。
    /// <para>Parses a single 11-byte entry (without the name).</para>
    /// </summary>
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
    /// 解析一个目录数据区域（位图 + 保留 + dentry + filename 的扁平布局），按内核
    /// f2fs_readdir 的槽位推进算法遍历：每位对应一个 8 字节槽；条目名可跨多个槽，读取后
    /// 前进 name_len 对应的槽数。适用于普通目录块与内联目录（仅尺寸参数不同）。
    /// <para>Parses a directory data area (flat layout of bitmap + reserved + dentry + filename)
    /// using the kernel's slot-skip iteration: each bitmap bit maps to an 8-byte slot; a name may
    /// span several slots and the cursor advances by the slot count of name_len after each entry.
    /// Works for both regular dentry blocks and inline directories (they differ only in sizes).</para>
    /// </summary>
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
                // 损坏的条目：终止遍历以防越界。Corrupt entry: stop the walk to stay in bounds.
                break;
            }

            var nameLength = Math.Min(nameLen, slots * SlotLength);
            var name = Encoding.UTF8.GetString(filenames.Slice(position * SlotLength, nameLength));
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
    /// 解析一个完整目录块（块大小 4096）。
    /// <para>Parses one full dentry block (4096 bytes).</para>
    /// </summary>
    public static List<F2fsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block)
        => ParseArea(block, BitmapSize, ReservedSize, SlotsPerBlock, F2fsNode.NodeSize);

    /// <summary>
    /// 构建一个目录块：仿 mkfs.f2fs add_dentry 逐项写入（首槽写 dentry 结构，名称跨槽存储，
    /// 每槽置位），生成哈希码与文件类型。
    /// <para>Builds a dentry block by appending entries like mkfs.f2fs add_dentry (the dentry struct
    /// is written at the first slot, the name spans slots, and every slot bit is set).</para>
    /// </summary>
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
            var slots = entry.SlotCount;
            if (used + slots > SlotsPerBlock)
            {
                throw new InvalidOperationException(
                    $"Directory does not fit into one dentry block (needs slot {used + slots} of {SlotsPerBlock}).");
            }

            var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
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