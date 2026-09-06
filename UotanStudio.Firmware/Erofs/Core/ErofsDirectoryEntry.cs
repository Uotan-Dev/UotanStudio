using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

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
                Name = Encoding.UTF8.GetString(block.Slice(nameOff, nameLength)),
            });

            position += DirentSize;
        }

        return result;
    }

    /// <summary>
    /// Parses one dense directory block using the actual length of block as the directory block size (for a full block this is the directory block size).
    /// <para>解析一个 dense 目录块；块大小取 block 的实际长度（整块解析时即目录块大小）。</para>
    /// </summary>
    public static List<ErofsDirectoryEntry> ParseDenseBlock(ReadOnlySpan<byte> block)
        => ParseDenseBlock(block, block.Length);

    /// <summary>
    /// Fully parses one dense directory block (EROFS_FEATURE_INCOMPAT_DIRENT_DENSE semantics).
    /// <para>完整解析一个 dense 目录块（EROFS_FEATURE_INCOMPAT_DIRENT_DENSE 语义）。</para>
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
                    Name = Encoding.UTF8.GetString(nameBytes),
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
                    Name = Encoding.UTF8.GetString(block.Slice(contributionStart, end - contributionStart)),
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
    public static List<ErofsDirectoryEntry> ParseDenseDirectory(ReadOnlySpan<byte> data, int blockSize)
        => ParseDirectory(data, blockSize, dense: true);
}
