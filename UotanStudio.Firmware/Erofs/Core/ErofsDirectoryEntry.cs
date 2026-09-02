using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 目录条目（compact_dirent，12 字节）及基于 nameoff 名称表的目录块解析。
/// 布局与解析语义严格对齐 Linux 内核主线 <c>fs/erofs/erofs_fs.h</c> 与
/// <c>fs/erofs/dir.c</c>（<c>erofs_fill_dentries</c> / <c>erofs_readdir</c>）。
/// <para>EROFS directory entry (compact_dirent, 12 bytes) and directory-block parsing based on the
/// nameoff name table. The layout and parsing semantics strictly follow Linux mainline
/// <c>fs/erofs/erofs_fs.h</c> and <c>fs/erofs/dir.c</c> (<c>erofs_fill_dentries</c> /
/// <c>erofs_readdir</c>).</para>
/// </summary>
internal sealed class ErofsDirectoryEntry
{
    /// <summary>dirent 固定大小（<c>sizeof(struct erofs_dirent)</c> = 12，含 <c>BUILD_BUG_ON</c> 校验）。
    /// Fixed size of a dirent (<c>sizeof(struct erofs_dirent)</c> = 12, enforced by BUILD_BUG_ON).</summary>
    public const int DirentSize = 12;

    /// <summary>dense 目录块末尾为长名称头部预留的 dirent 槽位数（块尾 <c>DenseHeadSlots * DirentSize</c> 字节）。
    /// Head slots reserved at the end of a dense directory block for long names
    /// (the trailing <c>DenseHeadSlots * DirentSize</c> bytes of the block).</summary>
    public const int DenseHeadSlots = 4;

    /// <summary>dense 目录中表示名称头部存储在预留槽位的 nameoff 高位标记（0x8000，bit 15）。
    /// High nameoff bit marking a head stored in a reserved slot (dense, bit 15 = 0x8000).</summary>
    public const ushort NameOffHeadFlag = 0x8000;

    /// <summary>目录项 nid（<c>__le64 nid</c>）。Entry nid.</summary>
    public ulong Nid { get; set; }

    /// <summary>名称起始偏移（块内；dense 长名项为磁盘原值，含 <see cref="NameOffHeadFlag"/> 与头槽索引）。
    /// Start offset of the name within the block (for dense long-name entries this holds the raw
    /// on-disk value, including <see cref="NameOffHeadFlag"/> and the head-slot index).</summary>
    public ushort NameOff { get; set; }

    /// <summary>文件类型（与 EROFS 文件类型一致：1=普通文件 / 2=目录 / 7=符号链接，复用 <see cref="ErofsInode.FileTypeRegularFile"/> 等）。
    /// File type (matches EROFS file types: 1=regular / 2=directory / 7=symlink; reuse
    /// <see cref="ErofsInode.FileTypeRegularFile"/> etc.).</summary>
    public byte FileType { get; set; }

    /// <summary>保留字节（<c>__u8 reserved</c>）。Reserved byte.</summary>
    public byte Reserved { get; set; }

    /// <summary>条目名称。Entry name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 从 12 字节解析单个 dirent（不含名称）。
    /// <para>Parses a single dirent (without the name) from 12 bytes.</para>
    /// </summary>
    public static ErofsDirectoryEntry FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < DirentSize)
        {
            throw new FormatException("Dirent region too short.");
        }

        return new ErofsDirectoryEntry
        {
            Nid = BinaryPrimitives.ReadUInt64LittleEndian(data),
            NameOff = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)),
            FileType = data[10],
            Reserved = data[11],
        };
    }

    /// <summary>
    /// 解析一个 compact 目录块，语义与 Linux 内核 <c>erofs_fill_dentries</c> 一致：
    /// dirent 自块首顺序排列，<c>de[0].nameoff</c> 同时给出名称区起点与 dirent 数组字节数
    /// （内核 <c>erofs_readdir</c> 校验其 ≥ 12、小于块长且为 12 的整数倍）；名称紧随 dirent
    /// 数组、按 dirent 顺序连续，第 i 项名称占 <c>[nameoff[i], nameoff[i+1])</c>；末项以块尾为界，
    /// 并按内核 <c>strnlen</c> 语义截断到首个 NUL。nameoff 单调递增，名称长度受限
    /// <c>[1, EROFS_NAME_LEN=255]</c>。遇到损坏项即停止并返回已解析的部分结果。
    /// <para>Parses one compact directory block matching the Linux kernel
    /// <c>erofs_fill_dentries</c> semantics. Dirents are laid out from the block start;
    /// <c>de[0].nameoff</c> is both the name-area start and the dirent-array size (the kernel
    /// <c>erofs_readdir</c> validates it to be ≥ 12, smaller than the block, and a multiple of 12).
    /// Names follow the dirent array contiguously in dirent order; entry i's name occupies
    /// <c>[nameoff[i], nameoff[i+1])</c>, the last entry being bounded by the block end and truncated
    /// at the first NUL per the kernel <c>strnlen</c> semantics. nameoff is monotonically
    /// increasing and name lengths are limited to <c>[1, EROFS_NAME_LEN=255]</c>. On a corrupted
    /// entry the walk stops and the entries parsed so far are returned.</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block)
    {
        var result = new List<ErofsDirectoryEntry>();
        if (block.Length < DirentSize)
        {
            return result;
        }

        var nameOff0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
        if (nameOff0 < DirentSize || nameOff0 > block.Length || nameOff0 % DirentSize != 0)
        {
            return result;
        }

        var position = 0;
        while (position < nameOff0)
        {
            var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(position + 8, 2));

            int nameLength;
            if (position + DirentSize < nameOff0)
            {
                // 非末项：名称占 [nameoff[i], nameoff[i+1])；nameoff 递减或越界时长度为负/超限。
                // Non-trailing entry: the name occupies [nameoff[i], nameoff[i+1]); a decreasing or
                // out-of-range nameoff yields a negative / oversized length.
                var nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(position + DirentSize + 8, 2));
                nameLength = nameEnd - nameOff;
            }
            else
            {
                // 末项：以块尾为界，按内核 strnlen 语义截断到首个 NUL。
                // Trailing entry: bounded by the block end, truncated at the first NUL (kernel strnlen).
                if (nameOff >= block.Length)
                {
                    break;
                }

                var nameEnd = block.Length;
                for (var p = nameOff; p < block.Length; p++)
                {
                    if (block[p] == 0)
                    {
                        nameEnd = p;
                        break;
                    }
                }

                nameLength = nameEnd - nameOff;
            }

            // 内核校验：1 <= namelen <= EROFS_NAME_LEN 且 nameoff + namelen <= maxsize。
            // Kernel validation: 1 <= namelen <= EROFS_NAME_LEN and nameoff + namelen <= maxsize.
            if (nameLength is < 1 or > 255 || nameOff + nameLength > block.Length)
            {
                break;
            }

            result.Add(new ErofsDirectoryEntry
            {
                Nid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(position)),
                NameOff = nameOff,
                FileType = block[position + 10],
                Reserved = block[position + 11],
                Name = Encoding.UTF8.GetString(block.Slice(nameOff, nameLength)),
            });

            position += DirentSize;
        }

        return result;
    }

    /// <summary>
    /// 解析一个 dense 目录块；块大小取 <paramref name="block"/> 的实际长度（整块解析时即目录块大小）。
    /// <para>Parses one dense directory block using the actual length of <paramref name="block"/> as
    /// the directory block size (for a full block this is the directory block size).</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDenseBlock(ReadOnlySpan<byte> block)
        => ParseDenseBlock(block, block.Length);

    /// <summary>
    /// 完整解析一个 dense 目录块（EROFS_FEATURE_INCOMPAT_DIRENT_DENSE 语义）。dirent 自块首顺序
    /// 排列，<c>de[0].nameoff</c>（必须为普通偏移）决定 dirent 数组字节数；块尾预留
    /// <see cref="DenseHeadSlots"/> 个 12 字节头槽，第 k 个槽位于 <c>[blockSize-(k+1)*12, blockSize-k*12)</c>。
    /// 无 0x8000 标志的项与 compact 相同（名称整体位于名称区 <c>[D, nameAreaEnd)</c>）；带 0x8000
    /// 标志的项编码为 <c>nameoff = 0x8000 | slot | (tailOffset &lt;&lt; 2)</c>：低位 2 位为头槽索引
    /// （0..3），其余位为名称尾部在名称区内的偏移；头槽内容是名称的前 12 字节，其余部分（尾部）
    /// 从该偏移起尾随在普通名称区。每项贡献起点 = 普通项 nameoff 或长名项 tailOffset，贡献终点 =
    /// 下一项贡献起点或名称区尾 <c>nameAreaEnd = blockSize - DenseHeadSlots * 12</c>，从而连续长名
    /// 项也能精确定界。头 + 尾按同一 UTF-8 序列拼接解码（12 字节边界可能截断多字节字符）。
    /// 损坏项即停止并返回已解析的部分结果。
    /// <para>Fully parses one dense directory block (EROFS_FEATURE_INCOMPAT_DIRENT_DENSE semantics).
    /// Dirents are laid out from the block start; <c>de[0].nameoff</c> (which must be a plain offset)
    /// determines the dirent-array size. The block tail reserves <see cref="DenseHeadSlots"/> 12-byte
    /// head slots, slot k occupying <c>[blockSize-(k+1)*12, blockSize-k*12)</c>. Entries without the
    /// 0x8000 flag behave exactly as compact (the whole name lives in the name area
    /// <c>[D, nameAreaEnd)</c>). Entries with the 0x8000 flag are encoded as
    /// <c>nameoff = 0x8000 | slot | (tailOffset &lt;&lt; 2)</c>: the low 2 bits are the head-slot index
    /// (0..3) and the remaining bits are the tail offset within the name area; the head slot holds
    /// the first 12 name bytes and the remainder (the tail) follows in the ordinary name area at
    /// that offset. Each contribution starts at its nameoff (plain) or tailOffset (long-name) and
    /// ends at the next contribution's start or the name-area end
    /// <c>nameAreaEnd = blockSize - DenseHeadSlots * 12</c>, so consecutive long-name entries are
    /// bounded exactly. Head and tail are concatenated and decoded as one UTF-8 sequence (the
    /// 12-byte boundary may split a multi-byte character). On a corrupted entry the walk stops and
    /// the entries parsed so far are returned.</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDenseBlock(ReadOnlySpan<byte> block, int blockSize)
    {
        var result = new List<ErofsDirectoryEntry>();
        if (block.Length < DirentSize || blockSize <= DenseHeadSlots * DirentSize)
        {
            return result;
        }

        var de0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
        if ((de0 & NameOffHeadFlag) != 0 || de0 < DirentSize || de0 % DirentSize != 0 || de0 > block.Length)
        {
            return result;
        }

        var ndirents = de0 / DirentSize;

        // 无任何 0x8000 头标志项时，dense 与 compact 完全一致。
        // When no entry carries the 0x8000 head flag, dense is identical to compact.
        var anyHead = false;
        for (var i = 0; i < ndirents; i++)
        {
            if ((BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(i * DirentSize + 8, 2)) & NameOffHeadFlag) != 0)
            {
                anyHead = true;
                break;
            }
        }

        if (!anyHead)
        {
            return ParseBlock(block);
        }

        // 有头标志项：名称区止于块尾预留头槽之前；末块不足整块时以实际数据长度为准。
        // With head flags present, the name area ends right before the reserved head slots; for a
        // trailing partial block the actual data length bounds it.
        var nameAreaEnd = blockSize - DenseHeadSlots * DirentSize;
        var namesEnd = Math.Min(nameAreaEnd, block.Length);
        if (namesEnd < de0)
        {
            return result;
        }

        // 第 index 项贡献起点：普通项为 nameoff，长名项为低位 2 位头槽索引之上的尾部偏移。
        // Contribution start of entry index: nameoff for plain entries, the tail offset above the
        // low 2 slot-index bits for long-name entries.
        static int ContributionStart(ReadOnlySpan<byte> block, int index)
        {
            var r = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(index * DirentSize + 8, 2));
            return (r & NameOffHeadFlag) == 0 ? r : (r & 0x7FFF) >> 2;
        }

        int pos = de0;
        for (var i = 0; i < ndirents; i++)
        {
            var entryOffset = i * DirentSize;
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(entryOffset + 8, 2));

            // 每项贡献起点：普通项为 nameoff，长名项为尾部偏移。
            // Contribution start of each entry: nameoff for plain, the tail offset for long names.
            var isHead = (raw & NameOffHeadFlag) != 0;
            int contributionStart;
            if (isHead)
            {
                var slot = raw & 0x3;
                if (slot >= DenseHeadSlots)
                {
                    break;
                }

                contributionStart = (raw & 0x7FFF) >> 2;
            }
            else
            {
                contributionStart = raw;
            }

            if (contributionStart < pos || contributionStart >= namesEnd)
            {
                break;
            }

            // 贡献终点 = 下一项贡献起点；末项以名称区尾为界并按 strnlen 语义截断到首个 NUL。
            // Contribution end = the next contribution's start; the last entry is bounded by the
            // name-area end and truncated at the first NUL (strnlen semantics).
            int end = i + 1 < ndirents ? ContributionStart(block, i + 1) : namesEnd;
            if (end < contributionStart || end > namesEnd)
            {
                break;
            }

            if (i + 1 == ndirents)
            {
                var scan = contributionStart;
                while (scan < end && block[scan] != 0)
                {
                    scan++;
                }

                end = scan;
            }

            if (isHead)
            {
                // 长名项：低位 2 位为头槽索引，其余位为名称尾部在名称区的偏移；头槽存前 12 字节。
                // Long-name entry: the low 2 bits are the head-slot index and the remaining bits are
                // the tail offset within the name area; the head slot stores the first 12 bytes.
                var slot = raw & 0x3;
                var headOffset = blockSize - (slot + 1) * DirentSize;
                if (headOffset < 0 || headOffset + DirentSize > block.Length)
                {
                    break;
                }

                // 头 + 尾按同一 UTF-8 序列解码（12 字节边界可能截断多字节字符）。
                // Head and tail are decoded as a single UTF-8 sequence (the 12-byte boundary may
                // split a multi-byte character).
                var tailLength = end - contributionStart;
                var nameBytes = new byte[DirentSize + tailLength];
                block.Slice(headOffset, DirentSize).CopyTo(nameBytes);
                block.Slice(contributionStart, tailLength).CopyTo(nameBytes.AsSpan(DirentSize));

                result.Add(new ErofsDirectoryEntry
                {
                    Nid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(entryOffset)),
                    NameOff = raw,
                    FileType = block[entryOffset + 10],
                    Reserved = block[entryOffset + 11],
                    Name = Encoding.UTF8.GetString(nameBytes),
                });
            }
            else
            {
                // 普通项：名称整体位于名称区 [nameoff, 下一项贡献起点)。
                // Plain entry: the whole name lives in the name area [nameoff, next contribution start).
                result.Add(new ErofsDirectoryEntry
                {
                    Nid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(entryOffset)),
                    NameOff = raw,
                    FileType = block[entryOffset + 10],
                    Reserved = block[entryOffset + 11],
                    Name = Encoding.UTF8.GetString(block.Slice(contributionStart, end - contributionStart)),
                });
            }

            pos = end;
        }

        return result;
    }

    /// <summary>
    /// 按块大小迭代解析整个目录数据区（i_size 可能跨多个目录块），每块按 compact 语义解析。
    /// <paramref name="blockSize"/> 应为目录块大小：默认取块大小 <c>blksz</c>，若超级块
    /// <c>DirBlkBits</c> 非 0 则目录块大小 = <c>1 &lt;&lt; DirBlkBits</c>。
    /// <para>Parses a whole directory data area block by block (i_size may span several directory
    /// blocks), each block parsed with compact semantics. <paramref name="blockSize"/> should be the
    /// directory block size: by default the block size <c>blksz</c>, or <c>1 &lt;&lt; DirBlkBits</c>
    /// when the superblock's <c>DirBlkBits</c> is non-zero.</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDirectory(ReadOnlySpan<byte> data, int blockSize)
    {
        var result = new List<ErofsDirectoryEntry>();
        for (var offset = 0; offset + DirentSize <= data.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, data.Length - offset);
            result.AddRange(ParseBlock(data.Slice(offset, length)));
        }

        return result;
    }

    /// <summary>
    /// 按块大小迭代解析整个目录数据区；<paramref name="dense"/> 为 true 时每块按 dense 语义解析
    /// （<see cref="ParseDenseBlock(ReadOnlySpan{byte}, int)"/>），否则按 compact 语义解析
    /// （<see cref="ParseBlock(ReadOnlySpan{byte})"/>）。
    /// <para>Parses a whole directory data area block by block; when <paramref name="dense"/> is true
    /// each block is parsed with dense semantics
    /// (<see cref="ParseDenseBlock(ReadOnlySpan{byte}, int)"/>), otherwise with compact semantics
    /// (<see cref="ParseBlock(ReadOnlySpan{byte})"/>).</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDirectory(ReadOnlySpan<byte> data, int blockSize, bool dense)
    {
        var result = new List<ErofsDirectoryEntry>();
        for (var offset = 0; offset + DirentSize <= data.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, data.Length - offset);
            result.AddRange(dense
                ? ParseDenseBlock(data.Slice(offset, length), blockSize)
                : ParseBlock(data.Slice(offset, length)));
        }

        return result;
    }

    /// <summary>
    /// 按块大小迭代解析整个目录数据区（dense 语义），每块调用
    /// <see cref="ParseDenseBlock(ReadOnlySpan{byte}, int)"/>。
    /// <para>Parses a whole directory data area block by block with dense semantics, delegating each
    /// block to <see cref="ParseDenseBlock(ReadOnlySpan{byte}, int)"/>.</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDenseDirectory(ReadOnlySpan<byte> data, int blockSize)
        => ParseDirectory(data, blockSize, dense: true);
}
