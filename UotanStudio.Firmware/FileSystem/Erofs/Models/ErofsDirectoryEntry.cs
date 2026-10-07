using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

internal sealed class ErofsDirectoryEntry
{
    public const int DirentSize = 12;

    public const int DenseHeadSlots = 4;

    public const ushort NameOffHeadFlag = 0x8000;

    public ulong Nid { get; set; }

    public ushort NameOff { get; set; }

    public byte FileType { get; set; }

    public byte Reserved { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Parses a single dirent (without the name) from 12 bytes.
    /// <para>从 12 字节解析单个 dirent（不含名称）。</para>
    /// </summary>
    /// <param name="data">Raw dirent bytes starting with an eight-byte little-endian nid, a two-byte nameoff,
    /// the file type and the reserved byte; bytes past offset <c>11</c> are ignored.
    /// <para>原始 dirent 字节，依次为 8 字节小端 nid、2 字节 nameoff、文件类型与保留字节；偏移 <c>11</c> 之后的字节被忽略。</para></param>
    /// <returns>The decoded entry with <see cref="Name"/> left empty, because a bare dirent holds no name bytes.
    /// <para>解码得到的条目，其 <see cref="Name"/> 保持为空，因为单条 dirent 不含名称字节。</para></returns>
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
    /// Parses one compact directory block matching the Linux kernel erofs_fill_dentries semantics.
    /// <para>解析一个 compact 目录块，语义与 Linux 内核 erofs_fill_dentries 一致： dirent 自块首顺序排列，de[0].nameoff 同时给出名称区起点与 dirent 数组字节数 （内核 erofs_readdir 校验其 ≥ 12、小于块长且为 12 的整数倍）；名称紧随 dirent 数组、按 dirent 顺序连续，第 i 项名称占 [nameoff[i], nameoff[i+1])；末项以块尾为界， 并按内核 strnlen 语义截断到首个 NUL。</para>
    /// </summary>
    /// <param name="block">Directory block bytes whose first dirent nameoff selects both the start of the name
    /// area and the byte size of the dirent array; a block shorter than <see cref="DirentSize"/> bytes, or a
    /// nameoff below <see cref="DirentSize"/>, past the block end or not a multiple of
    /// <see cref="DirentSize"/>, parses as an empty list.
    /// <para>目录块字节；首条 dirent 的 nameoff 同时决定名称区起点与 dirent 数组的字节长度。块长不足
    /// <see cref="DirentSize"/>，或 nameoff 小于 <see cref="DirentSize"/>、超出块尾、不是
    /// <see cref="DirentSize"/> 的整数倍时，结果为空列表。</para></param>
    /// <returns>The decoded entries in on-disk order, stopping at the first malformed name range.
    /// <para>按磁盘顺序解码出的条目；遇到首个非法名称范围时停止。</para></returns>
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


                var nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(position + DirentSize + 8, 2));
                nameLength = nameEnd - nameOff;
            }
            else
            {


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
                Name = UotanStudio.Firmware.FileSystem.Extraction.PortableExtractionPaths.NameEncoding.GetString(block.Slice(nameOff, nameLength)),
            });

            position += DirentSize;
        }

        return result;
    }

    /// <summary>
    /// Parses one dense directory block using the actual length of block as the directory block size (for a full block this is the directory block size).
    /// <para>解析一个 dense 目录块；块大小取 block 的实际长度（整块解析时即目录块大小）。</para>
    /// </summary>
    /// <param name="block">Bytes of the directory block; fewer than <see cref="DirentSize"/> bytes parse as an empty list.
    /// <para>目录块的字节；少于 <see cref="DirentSize"/> 字节时解析结果为空列表。</para></param>
    /// <returns>The decoded entries in on-disk order, empty when the block header is rejected.
    /// <para>按磁盘顺序解码出的条目；块头被拒绝时为空列表。</para></returns>
    public static List<ErofsDirectoryEntry> ParseDenseBlock(ReadOnlySpan<byte> block)
        => ParseDenseBlock(block, block.Length);

    /// <summary>
    /// Fully parses one dense directory block (EROFS_FEATURE_INCOMPAT_DIRENT_DENSE semantics).
    /// <para>完整解析一个 dense 目录块（EROFS_FEATURE_INCOMPAT_DIRENT_DENSE 语义）。</para>
    /// </summary>
    /// <param name="block">Bytes of the directory block; fewer than <see cref="DirentSize"/> bytes parse
    /// as an empty list. <para>目录块的字节；少于 <see cref="DirentSize"/> 字节时解析结果为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes that locates the trailing head-slot
    /// dirents; it may exceed <paramref name="block"/> for a truncated final block, and a value at or
    /// below <see cref="DenseHeadSlots"/> * <see cref="DirentSize"/> parses as an empty list.
    /// <para>用于定位块尾 head-slot dirent 的目录块大小（字节）；末尾块被截断时可大于 <paramref name="block"/>；
    /// 不大于 <see cref="DenseHeadSlots"/> * <see cref="DirentSize"/> 时解析结果为空列表。</para></param>
    /// <returns>The decoded entries in on-disk order, empty when the block header is rejected.
    /// <para>按磁盘顺序解码出的条目；块头被拒绝时为空列表。</para></returns>
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


        var nameAreaEnd = blockSize - DenseHeadSlots * DirentSize;
        var namesEnd = Math.Min(nameAreaEnd, block.Length);
        if (namesEnd < de0)
        {
            return result;
        }


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


                var slot = raw & 0x3;
                var headOffset = blockSize - (slot + 1) * DirentSize;
                if (headOffset < 0 || headOffset + DirentSize > block.Length)
                {
                    break;
                }


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
                    Name = UotanStudio.Firmware.FileSystem.Extraction.PortableExtractionPaths.NameEncoding.GetString(nameBytes),
                });
            }
            else
            {


                result.Add(new ErofsDirectoryEntry
                {
                    Nid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(entryOffset)),
                    NameOff = raw,
                    FileType = block[entryOffset + 10],
                    Reserved = block[entryOffset + 11],
                    Name = UotanStudio.Firmware.FileSystem.Extraction.PortableExtractionPaths.NameEncoding.GetString(block.Slice(contributionStart, end - contributionStart)),
                });
            }

            pos = end;
        }

        return result;
    }

    /// <summary>
    /// Parses a whole directory data area block by block (i_size may span several directory blocks), each block parsed with compact semantics. blockSize should be the directory block size: by default the block size blksz, or 1 &lt;&lt; DirBlkBits when the superblock's DirBlkBits is non-zero.
    /// <para>按块大小迭代解析整个目录数据区（i_size 可能跨多个目录块），每块按 compact 语义解析。</para>
    /// </summary>
    /// <param name="data">Directory data area of the inode; a span shorter than <see cref="DirentSize"/>
    /// bytes yields an empty list.
    /// <para>inode 的目录数据区；跨度小于 <see cref="DirentSize"/> 字节时结果为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes, used both as the step between blocks and
    /// as the per-block parse size; the final block is parsed with the remaining bytes.
    /// <para>目录块大小（字节），既是块间的步进也是每块的解析长度；最后一块按剩余字节解析。</para></param>
    /// <returns>The entries of every block concatenated in block order.
    /// <para>按块顺序拼接得到的全部条目。</para></returns>
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
    /// Parses a whole directory data area block by block; when dense is true each block is parsed with dense semantics (ParseDenseBlock(ReadOnlySpan{byte}, int)), otherwise with compact semantics (ParseBlock(ReadOnlySpan{byte})).
    /// <para>按块大小迭代解析整个目录数据区；dense 为 true 时每块按 dense 语义解析 （ParseDenseBlock(ReadOnlySpan{byte}, int)），否则按 compact 语义解析 （ParseBlock(ReadOnlySpan{byte})）。</para>
    /// </summary>
    /// <param name="data">Directory data area of the inode; a span shorter than <see cref="DirentSize"/>
    /// bytes yields an empty list.
    /// <para>inode 的目录数据区；跨度小于 <see cref="DirentSize"/> 字节时结果为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes, used both as the step between blocks and
    /// as the dense head-slot layout size; the final block is parsed with the remaining bytes.
    /// <para>目录块大小（字节），既是块间的步进，也是 dense 语义下 head-slot 的布局基准；最后一块按剩余字节解析。</para></param>
    /// <param name="dense">When <see langword="true"/>, blocks carrying the EROFS_FEATURE_INCOMPAT_DIRENT_DENSE
    /// layout are decoded; otherwise the compact layout is used.
    /// <para>为 <see langword="true"/> 时解析携带 EROFS_FEATURE_INCOMPAT_DIRENT_DENSE 布局的块；
    /// 否则按 compact 布局解析。</para></param>
    /// <returns>The entries of every block concatenated in block order.
    /// <para>按块顺序拼接得到的全部条目。</para></returns>
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
    /// Parses a whole directory data area block by block with dense semantics, delegating each block to ParseDenseBlock(ReadOnlySpan{byte}, int).
    /// <para>按块大小迭代解析整个目录数据区（dense 语义），每块调用 ParseDenseBlock(ReadOnlySpan{byte}, int)。</para>
    /// </summary>
    /// <param name="data">Directory data area of the inode; a span shorter than <see cref="DirentSize"/> bytes
    /// yields an empty list. <para>inode 的目录数据区；跨度小于 <see cref="DirentSize"/> 字节时结果为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes, used both as the step between blocks and as the
    /// dense head-slot layout size; the final block is parsed with the remaining bytes.
    /// <para>目录块大小（字节），既是块间的步进，也是 dense head-slot 的布局基准；最后一块按剩余字节解析。</para></param>
    /// <returns>The entries of every block concatenated in block order.
    /// <para>按块顺序拼接得到的全部条目。</para></returns>
    public static List<ErofsDirectoryEntry> ParseDenseDirectory(ReadOnlySpan<byte> data, int blockSize)
        => ParseDirectory(data, blockSize, dense: true);
}