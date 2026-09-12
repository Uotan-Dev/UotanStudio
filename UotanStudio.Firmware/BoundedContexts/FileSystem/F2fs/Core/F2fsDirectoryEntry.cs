using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

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
    /// Tests whether bit index is set in a f2fs bitmap (LSB-first within each byte, matching the kernel's test_bit_le).
    /// <para>判断位图中第 index 位是否置位（f2fs 位图为每字节 LSB-first， 对齐内核 test_bit_le）。</para>
    /// </summary>
    public static bool TestBit(ReadOnlySpan<byte> bitmap, int index)
        => index >= 0 && index < bitmap.Length * 8
           && (bitmap[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>
    /// Sets bit index in a f2fs bitmap.
    /// <para>设置位图第 index 位。</para>
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
    /// Parses a single 11-byte entry (without the name).
    /// <para>解析单个 11 字节条目（不含名称）。</para>
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
    /// Parses a directory data area (flat layout of bitmap + reserved + dentry + filename) using the kernel's slot-skip iteration: each bitmap bit maps to an 8-byte slot; a name may span several slots and the cursor advances by the slot count of name_len after each entry.
    /// <para>解析一个目录数据区域（位图 + 保留 + dentry + filename 的扁平布局），按内核 f2fs_readdir 的槽位推进算法遍历：每位对应一个 8 字节槽；条目名可跨多个槽，读取后 前进 name_len 对应的槽数。</para>
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

                break;
            }

            var nameLength = Math.Min(nameLen, slots * SlotLength);
            var name = SharedKernel.PortableExtractionPaths.NameEncoding.GetString(filenames.Slice(position * SlotLength, nameLength));
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
    public static List<F2fsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block)
        => ParseArea(block, BitmapSize, ReservedSize, SlotsPerBlock, F2fsNode.NodeSize);

    /// <summary>
    /// Builds a dentry block by appending entries like mkfs.f2fs add_dentry (the dentry struct is written at the first slot, the name spans slots, and every slot bit is set).
    /// <para>构建一个目录块：仿 mkfs.f2fs add_dentry 逐项写入（首槽写 dentry 结构，名称跨槽存储， 每槽置位），生成哈希码与文件类型。</para>
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
