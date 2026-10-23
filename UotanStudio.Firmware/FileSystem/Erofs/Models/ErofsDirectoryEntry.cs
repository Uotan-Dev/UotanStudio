using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

internal sealed class ErofsDirectoryEntry
{
    public const int DirentSize = 12;

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

    /// <summary>Parses one compact directory block whose size is the block length itself.
    /// <para>解析一个 compact 目录块，块大小取该块自身的字节长度。</para></summary>
    /// <param name="block">Directory block bytes; a span shorter than <see cref="DirentSize"/> bytes parses as an empty list.
    /// <para>目录块字节；长度不足 <see cref="DirentSize"/> 字节时结果为空列表。</para></param>
    /// <returns>The decoded entries in on-disk order.<para>按磁盘顺序解码出的条目。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The block is internally inconsistent, as described by
    /// <see cref="ParseBlock(ReadOnlySpan{byte}, int)"/>.
    /// <para>块内部不一致，具体条件见 <see cref="ParseBlock(ReadOnlySpan{byte}, int)"/>。</para></exception>
    public static List<ErofsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block)
        => ParseBlock(block, block.Length);

    /// <summary>Parses one compact directory block and reports on-disk ordering and corruption.
    /// <para>解析一个 compact 目录块，并按磁盘顺序返回条目、对损坏输入上报。</para></summary>
    /// <param name="block">Directory block bytes whose first dirent nameoff selects both the start of the name area and the byte size of the dirent array; a block shorter than <see cref="DirentSize"/> bytes parses as an empty list.
    /// <para>目录块字节；首条 dirent 的 nameoff 同时决定名称区起点与 dirent 数组的字节长度。块长不足 <see cref="DirentSize"/> 字节时结果为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes that bounds every nameoff; values at or above it are rejected, so a 64 KiB block accepts the ushort nameoff range the format allows.
    /// <para>目录块字节大小，作为每条 nameoff 的上界；达到或超过它即被拒绝，因此 64 KiB 块可以使用格式允许的 ushort nameoff 范围。</para></param>
    /// <returns>The decoded entries in on-disk order.<para>按磁盘顺序解码出的条目。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The first nameoff is below <see cref="DirentSize"/>, reaches the block size, is past the block end or is not a multiple of <see cref="DirentSize"/>; some later nameoff is not the running name end or reaches the block size; or a name length is zero, above 255, or runs past the block.
    /// <para>首条 nameoff 小于 <see cref="DirentSize"/>、达到块大小、超出块尾或不是 <see cref="DirentSize"/> 的整数倍；后续某条 nameoff 不等于运行中的名称结束位置或达到块大小；或名称长度为零、超过 255、或越出块外。</para></exception>
    public static List<ErofsDirectoryEntry> ParseBlock(ReadOnlySpan<byte> block, int blockSize)
    {
        var result = new List<ErofsDirectoryEntry>();
        if (block.Length < DirentSize)
        {
            return result;
        }

        if (blockSize < DirentSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                "A directory block must hold at least one dirent.");
        }

        var nameOff0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
        if (nameOff0 < DirentSize || nameOff0 >= blockSize || nameOff0 > block.Length || nameOff0 % DirentSize != 0)
        {
            throw new FileSystemCorruptedException(
                $"Invalid EROFS dirent array size {nameOff0} for a {blockSize}-byte directory block.");
        }

        var expectedNameOff = (int)nameOff0;
        for (var position = 0; position < nameOff0; position += DirentSize)
        {
            var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(position + 8, 2));
            if (nameOff >= blockSize || nameOff != expectedNameOff)
            {
                throw new FileSystemCorruptedException(
                    $"Non-contiguous EROFS dirent nameoff {nameOff}, expected {expectedNameOff}.");
            }

            int nameLength;
            if (position + DirentSize < nameOff0)
            {
                var nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(position + DirentSize + 8, 2));
                nameLength = nameEnd - nameOff;
            }
            else
            {
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
                throw new FileSystemCorruptedException(
                    $"Invalid EROFS dirent name length {nameLength} at nameoff {nameOff}.");
            }

            result.Add(new ErofsDirectoryEntry
            {
                Nid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(position)),
                NameOff = nameOff,
                FileType = block[position + 10],
                Reserved = block[position + 11],
                Name = Extraction.PortableExtractionPaths.NameEncoding.GetString(block.Slice(nameOff, nameLength)),
            });

            expectedNameOff = nameOff + nameLength;
        }

        return result;
    }

    /// <summary>
    /// Parses a whole directory data area block by block with compact semantics.
    /// <para>按块迭代解析整个目录数据区，每块按 compact 语义解析。</para>
    /// </summary>
    /// <param name="data">Directory data area of the inode; a span shorter than <see cref="DirentSize"/>
    /// bytes yields an empty list.
    /// <para>inode 的目录数据区；跨度小于 <see cref="DirentSize"/> 字节时结果为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes, used both as the step between blocks and
    /// as the per-block parse size; the final block is parsed with the remaining bytes.
    /// <para>目录块大小（字节），既是块间的步进也是每块的解析长度；最后一块按剩余字节解析。</para></param>
    /// <returns>The entries of every block concatenated in block order.
    /// <para>按块顺序拼接得到的全部条目。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is smaller than
    /// <see cref="DirentSize"/>. <para><paramref name="blockSize"/> 小于 <see cref="DirentSize"/>。</para></exception>
    public static List<ErofsDirectoryEntry> ParseDirectory(ReadOnlySpan<byte> data, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, DirentSize);
        var result = new List<ErofsDirectoryEntry>();
        for (var offset = 0; offset + DirentSize <= data.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, data.Length - offset);
            result.AddRange(ParseBlock(data.Slice(offset, length), blockSize));
        }

        return result;
    }
}