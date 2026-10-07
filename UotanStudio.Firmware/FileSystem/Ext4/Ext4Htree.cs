using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Checksums;
using UotanStudio.Firmware.FileSystem.Ext4.Models;

namespace UotanStudio.Firmware.FileSystem.Ext4;

internal static class Ext4Htree
{
    public const byte HashVersionHalfMd4 = 1;

    public const uint FlagIndex = 0x0000_1000;

    public const int DxEntrySize = 8;

    public const int DxRootInfoSize = 8;

    public const int DotEntrySize = 12;

    public const int DxRootCountOffset = DotEntrySize + DotEntrySize + DxRootInfoSize;

    public const int DxNodeCountOffset = 8;

    public const int DxTailSize = 8;

    public const int DirentTailSize = 12;

    private static readonly uint[] DefaultHashInit = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];

    private const uint Md4K2 = 0x5A82_7999;
    private const uint Md4K3 = 0x6ED9_EBA1;

    /// <summary>
    /// Computes the ext4 Half-MD4 hash of a directory name (buf[1] &amp; ~1), matching the EXT2_HASH_HALF_MD4 branch of e2fsprogs ext2fs_dirhash: an all-zero seed selects the default MD4 init vector and the name is transformed in 32-byte chunks.
    /// <para>计算目录名的 ext4 Half-MD4 哈希（返回 buf[1] &amp; ~1）。</para>
    /// </summary>
    /// <param name="name">Directory entry name; it is UTF-8 encoded and consumed in 32-byte chunks.
    /// <para>目录项名称；先按 UTF-8 编码，再以 32 字节分块消费。</para></param>
    /// <param name="seed">At least four seed words from the superblock hash seed; an all-zero vector selects the default MD4 initial state.
    /// <para>超级块哈希种子中至少四个种子词；全零向量改用默认 MD4 初始状态。</para></param>
    /// <returns>The directory hash, which is state word 1 with its least significant bit cleared.
    /// <para>目录哈希值，即清除最低有效位后的状态字 1。</para></returns>
    public static uint HalfMd4Hash(string name, uint[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length < 4)
        {
            throw new ArgumentException("Hash seed must have at least 4 elements.", nameof(seed));
        }

        var buf = new uint[4];
        var seedIsZero = true;
        for (var i = 0; i < 4; i++)
        {
            buf[i] = seed[i];
            seedIsZero &= seed[i] == 0;
        }

        if (seedIsZero)
        {
            Array.Copy(DefaultHashInit, buf, 4);
        }

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var offset = 0;
        var remaining = nameBytes.Length;
        while (remaining > 0)
        {
            var words = Str2HashBuf(nameBytes, offset, remaining);
            HalfMd4Transform(buf, words);
            offset += 32;
            remaining -= 32;
        }

        return buf[1] & 0xFFFF_FFFE;
    }

    /// <summary>
    /// Converts name bytes into up to 8 uint32 words (little-endian accumulation + length padding), matching e2fsprogs str2hashbuf(num=8).
    /// <para>将名字字节转为最多 8 个 uint32 词（小端累积 + 长度填充），与 e2fsprogs str2hashbuf(num=8) 一致。</para>
    /// </summary>
    private static uint[] Str2HashBuf(byte[] name, int offset, int len)
    {
        var pad = (uint)(len | (len << 8) | (len << 16) | (len << 24));
        var words = new uint[8];
        var val = pad;
        var count = 0;
        var limit = Math.Min(len, 32);
        for (var i = 0; i < limit; i++)
        {
            val = name[offset + i] + (val << 8);
            if ((i & 3) == 3)
            {
                words[count++] = val;
                val = pad;
            }
        }

        if ((limit & 3) != 0)
        {
            words[count++] = val;
        }

        while (count < 8)
        {
            words[count++] = pad;
        }

        return words;
    }

    /// <summary>
    /// Truncated MD4 transform, line-for-line equivalent to e2fsprogs halfMD4Transform.
    /// <para>截断 MD4 变换，与 e2fsprogs halfMD4Transform 逐行一致。</para>
    /// </summary>
    private static void HalfMd4Transform(uint[] buf, uint[] input)
    {
        var a = buf[0];
        var b = buf[1];
        var c = buf[2];
        var d = buf[3];

        a = RotateLeft(a + F(b, c, d) + input[0], 3);
        d = RotateLeft(d + F(a, b, c) + input[1], 7);
        c = RotateLeft(c + F(d, a, b) + input[2], 11);
        b = RotateLeft(b + F(c, d, a) + input[3], 19);
        a = RotateLeft(a + F(b, c, d) + input[4], 3);
        d = RotateLeft(d + F(a, b, c) + input[5], 7);
        c = RotateLeft(c + F(d, a, b) + input[6], 11);
        b = RotateLeft(b + F(c, d, a) + input[7], 19);

        a = RotateLeft(a + G(b, c, d) + input[1] + Md4K2, 3);
        d = RotateLeft(d + G(a, b, c) + input[3] + Md4K2, 5);
        c = RotateLeft(c + G(d, a, b) + input[5] + Md4K2, 9);
        b = RotateLeft(b + G(c, d, a) + input[7] + Md4K2, 13);
        a = RotateLeft(a + G(b, c, d) + input[0] + Md4K2, 3);
        d = RotateLeft(d + G(a, b, c) + input[2] + Md4K2, 5);
        c = RotateLeft(c + G(d, a, b) + input[4] + Md4K2, 9);
        b = RotateLeft(b + G(c, d, a) + input[6] + Md4K2, 13);

        a = RotateLeft(a + H(b, c, d) + input[3] + Md4K3, 3);
        d = RotateLeft(d + H(a, b, c) + input[7] + Md4K3, 9);
        c = RotateLeft(c + H(d, a, b) + input[2] + Md4K3, 11);
        b = RotateLeft(b + H(c, d, a) + input[6] + Md4K3, 15);
        a = RotateLeft(a + H(b, c, d) + input[1] + Md4K3, 3);
        d = RotateLeft(d + H(a, b, c) + input[5] + Md4K3, 9);
        c = RotateLeft(c + H(d, a, b) + input[0] + Md4K3, 11);
        b = RotateLeft(b + H(c, d, a) + input[4] + Md4K3, 15);

        buf[0] += a;
        buf[1] += b;
        buf[2] += c;
        buf[3] += d;
    }

    private static uint F(uint x, uint y, uint z) => z ^ (x & (y ^ z));

    private static uint G(uint x, uint y, uint z) => (x & y) + ((x ^ y) & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    /// <summary>
    /// Parses the dx_entry list from the dx_root in directory block 0, returning (hash, block) for every child block. entry[0]'s hash is not stored on disk (its area holds limit/count), so 0 is reported; its block points at the first child.
    /// <para>从目录块 0 解析 dx_root 的 dx_entry 列表：返回每个子块（叶块/索引块）的 (hash, block)。</para>
    /// </summary>
    /// <param name="block0">Bytes of directory logical block 0; the leading "." and ".." records, the dx_root_info and the count/limit area are validated before any entry is read.
    /// <para>目录逻辑块 0 的字节；读取条目前先校验开头的 "." 与 ".." 记录、dx_root_info 以及 count/limit 区。</para></param>
    /// <param name="blockSize">Directory block size in bytes, used to check the ".." record length and to cap the entry count at the space left after the dx_root header.
    /// <para>目录块大小（字节），用于检查 ".." 记录长度，并将条目数限制在 dx_root 头之后的剩余空间内。</para></param>
    /// <returns>Hash and child block number of every dx_entry, empty when the bytes are shorter than the dx_root header, are not shaped like "."/".." plus dx_root_info, or use a hash version other than Half-MD4.
    /// <para>每个 dx_entry 的哈希与子块号；字节短于 dx_root 头、不符合 "."/".." 加 dx_root_info 的结构，或哈希版本不是 Half-MD4 时为空。</para></returns>
    public static List<(uint Hash, uint Block)> ParseDxEntries(ReadOnlySpan<byte> block0, int blockSize)
    {
        if (block0.Length < DxRootCountOffset + DxEntrySize)
        {
            return [];
        }

        var dotRecLen = BinaryPrimitives.ReadUInt16LittleEndian(block0[4..6]);
        var dotDotNameLen = block0[12 + 6];
        var dotDotRecLen = BinaryPrimitives.ReadUInt16LittleEndian(block0[(12 + 4)..(12 + 6)]);
        if (dotRecLen != DotEntrySize || block0[6] != 1 || block0[8] != (byte)'.' ||
            dotDotNameLen != 2 || dotDotRecLen != blockSize - DotEntrySize ||
            block0[12 + 8] != (byte)'.' || block0[12 + 9] != (byte)'.')
        {
            return [];
        }

        var info = block0[DotEntrySize + DotEntrySize + 4];
        if (info != HashVersionHalfMd4)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(block0[(DxRootCountOffset + 2)..]);
        var maxSane = (blockSize - DxRootCountOffset) / DxEntrySize;
        if (count > maxSane)
        {
            count = (ushort)maxSane;
        }

        var entries = new List<(uint Hash, uint Block)>(count);
        for (var i = 0; i < count; i++)
        {
            var pos = DxRootCountOffset + i * DxEntrySize;
            if (pos + DxEntrySize > block0.Length)
            {
                break;
            }

            var hash = i == 0 ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(block0[pos..]);
            var childBlock = BinaryPrimitives.ReadUInt32LittleEndian(block0[(pos + 4)..]);
            entries.Add((hash, childBlock));
        }

        return entries;
    }

    /// <summary>
    /// Overload of the htree builder that fills "." and ".." with inode 0 (structure/parse tests only).
    /// <para>构建 htree 索引的重载：不提供 "."/".." 的 inode 号时以 0 填充（仅用于结构/解析测试）。</para>
    /// </summary>
    /// <param name="entries">Directory records to index as (name, inode, file type) triples; the list is
    /// sorted by hash and may be empty. <para>待索引的目录项三元组（名称、inode、文件类型）；按哈希排序，可以为空列表。</para></param>
    /// <param name="blockSize">Directory block size in bytes for the root, leaf and index blocks.
    /// <para>根块、叶块与索引块使用的目录块大小（字节）。</para></param>
    /// <param name="hashSeed">At least four seed words from the superblock hash seed used by Half-MD4.
    /// <para>Half-MD4 使用的超级块哈希种子，至少包含四个种子词。</para></param>
    /// <returns>The plan holding the dx_root block, the leaf blocks, any dx_node index blocks and the
    /// indirect level count. <para>包含 dx_root 块、叶块、可选的 dx_node 索引块与间接层级数的方案。</para></returns>
    public static HtreePlan BuildHtree(
        List<(string Name, uint Inode, byte FileType)> entries,
        int blockSize,
        uint[] hashSeed)
    {
        return BuildHtree(0, 0, entries, blockSize, hashSeed);
    }

    /// <summary>
    /// Builds an htree index for a directory.
    /// <para>为目录构建 htree 索引。</para>
    /// </summary>
    /// <param name="directoryInode">Inode number written into the "." record and mixed into every
    /// metadata checksum. <para>写入 "." 记录并参与所有元数据校验和的 inode 号。</para></param>
    /// <param name="parentInode">Inode number written into the ".." record.
    /// <para>写入 ".." 记录的 inode 号。</para></param>
    /// <param name="entries">Directory records to index as (name, inode, file type) triples, sorted by
    /// Half-MD4 hash and then by ordinal name; names longer than 255 UTF-8 bytes are truncated.
    /// <para>待索引的目录项三元组（名称、inode、文件类型），先按 Half-MD4 哈希再按名称序号排序；超过 255 个
    /// UTF-8 字节的名称会被截断。</para></param>
    /// <param name="blockSize">Directory block size in bytes for the root, leaf and index blocks.
    /// <para>根块、叶块与索引块使用的目录块大小（字节）。</para></param>
    /// <param name="hashSeed">At least four seed words from the superblock hash seed used by Half-MD4.
    /// <para>Half-MD4 使用的超级块哈希种子，至少包含四个种子词。</para></param>
    /// <param name="enableMetadataCsum">Reserve the dirent/dx tail space and write CRC32C checksums.
    /// <para>预留 dirent/dx 尾部空间并写入 CRC32C 校验和。</para></param>
    /// <param name="uuid">Superblock UUID bytes seeding the checksums; <see langword="null"/> seeds them
    /// with sixteen zero bytes. <para>作为校验和种子的超级块 UUID 字节；为 <see langword="null"/> 时使用十六个零字节。</para></param>
    /// <param name="generation">Inode generation mixed into the checksums alongside <paramref name="directoryInode"/>.
    /// <para>与 <paramref name="directoryInode"/> 一同参与校验和的 inode 世代号。</para></param>
    /// <returns>The plan holding the dx_root block, the leaf blocks, any dx_node index blocks and the
    /// indirect level count. <para>包含 dx_root 块、叶块、可选的 dx_node 索引块与间接层级数的方案。</para></returns>
    public static HtreePlan BuildHtree(
        uint directoryInode,
        uint parentInode,
        List<(string Name, uint Inode, byte FileType)> entries,
        int blockSize,
        uint[] hashSeed,
        bool enableMetadataCsum = false,
        byte[]? uuid = null,
        uint generation = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(hashSeed);

        var csumOverhead = enableMetadataCsum ? DxTailSize : 0;

        var hashed = new List<(uint Hash, string Name, uint Inode, byte FileType)>(entries.Count);
        foreach (var (name, inode, fileType) in entries)
        {
            hashed.Add((HalfMd4Hash(name, hashSeed), name, inode, fileType));
        }

        hashed.Sort(static (x, y) =>
        {
            var byHash = x.Hash.CompareTo(y.Hash);
            return byHash != 0 ? byHash : string.CompareOrdinal(x.Name, y.Name);
        });

        var (leafBlocks, leafFirstHashes, leafLastHashes) = PackLeafBlocks(hashed, blockSize, enableMetadataCsum, directoryInode, uuid, generation);
        if (leafBlocks.Count == 0)
        {

            return new HtreePlan(BuildDxRootBlock(blockSize, directoryInode, parentInode, 0, 1, 0, [], enableMetadataCsum, uuid, generation), [], [], 0);
        }

        var rootLimit = (blockSize - DotEntrySize - DotEntrySize - DxRootInfoSize - csumOverhead) / DxEntrySize;
        var nodeLimit = (blockSize - DxNodeCountOffset - csumOverhead) / DxEntrySize;
        byte[] rootBlock;
        List<byte[]> indexBlocks;

        if (leafBlocks.Count <= rootLimit)
        {

            var children = BuildChildEntryList(leafFirstHashes, leafLastHashes, 0, leafBlocks.Count, leafBlockBase: 1);
            rootBlock = BuildDxRootBlock(blockSize, directoryInode, parentInode, children.Count, rootLimit, 0, children, enableMetadataCsum, uuid, generation);
            indexBlocks = [];
        }
        else
        {
            if (leafBlocks.Count > (long)rootLimit * nodeLimit)
            {
                throw new InvalidOperationException(
                    $"Directory requires more than two htree levels ({leafBlocks.Count} leaf blocks), which is not supported.");
            }

            var leafCount = leafBlocks.Count;
            var nodeCount = (leafCount + nodeLimit - 1) / nodeLimit;
            var nodeBlocks = new List<byte[]>(nodeCount);
            var rootChildren = new List<(uint Hash, uint Block)>(nodeCount);
            for (var n = 0; n < nodeCount; n++)
            {
                var firstLeaf = n * nodeLimit;
                var nodeLeafCount = Math.Min(nodeLimit, leafCount - firstLeaf);
                var nodeChildren = BuildChildEntryList(leafFirstHashes, leafLastHashes, firstLeaf, nodeLeafCount, leafBlockBase: 1);
                nodeBlocks.Add(BuildDxNodeBlock(blockSize, nodeChildren.Count, nodeLimit, nodeChildren, directoryInode, enableMetadataCsum, uuid, generation));

                var nodeBlock = (uint)(leafCount + 1 + n);
                if (n == 0)
                {
                    rootChildren.Add((0, nodeBlock));
                }
                else
                {
                    var hash = leafFirstHashes[firstLeaf];
                    if (hash == leafLastHashes[firstLeaf - 1])
                    {
                        hash++;
                    }

                    rootChildren.Add((hash, nodeBlock));
                }
            }

            rootBlock = BuildDxRootBlock(blockSize, directoryInode, parentInode, rootChildren.Count, rootLimit, 1, rootChildren, enableMetadataCsum, uuid, generation);
            indexBlocks = nodeBlocks;
        }

        return new HtreePlan(rootBlock, leafBlocks, indexBlocks, indexBlocks.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// Packs hash-ordered directory entries into leaf blocks, returning the leaf byte arrays and the first/last-entry hash of every leaf. The names packed here are child names only — the "." and ".." records are written by the dx_root block — so every one of them goes through the dirent name rule.
    /// <para>按哈希顺序将目录项打包进叶块，返回叶块字节数组及各叶块首/末条目的哈希。这里打包的名称只是子项名称——“.” 与 “..” 记录由 dx_root 块写入——因此全部要经过目录项名称规则校验。</para>
    /// </summary>
    private static (List<byte[]> Blocks, List<uint> FirstHashes, List<uint> LastHashes) PackLeafBlocks(
        List<(uint Hash, string Name, uint Inode, byte FileType)> sorted,
        int blockSize,
        bool enableMetadataCsum = false,
        uint directoryInode = 0,
        byte[]? uuid = null,
        uint generation = 0)
    {
        var blocks = new List<byte[]>();
        var firstHashes = new List<uint>();
        var lastHashes = new List<uint>();

        var endCap = blockSize - (enableMetadataCsum ? DirentTailSize : 0);

        var block = new byte[blockSize];
        var offset = 0;
        var entriesInBlock = 0;
        uint firstHashOfBlock = 0;
        uint lastHashOfBlock = 0;
        var lastEntryOffset = 0;

        void Flush()
        {
            if (entriesInBlock == 0)
            {
                return;
            }

            WriteU16(block, lastEntryOffset + 4, (ushort)(endCap - lastEntryOffset));
            if (enableMetadataCsum)
            {
                WriteDirentTail(block, endCap, directoryInode, uuid, generation);
            }

            blocks.Add(block);
            firstHashes.Add(firstHashOfBlock);
            lastHashes.Add(lastHashOfBlock);
            block = new byte[blockSize];
            offset = 0;
            entriesInBlock = 0;
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            var (hash, name, inode, fileType) = sorted[i];
            FileSystemNameLimits.ValidateEntryName(name, "EXT4");
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var nameLen = nameBytes.Length;
            var naturalLength = (8 + nameLen + 3) & ~3;
            if (entriesInBlock > 0 && offset + naturalLength > endCap)
            {
                Flush();
            }

            if (entriesInBlock == 0)
            {
                firstHashOfBlock = hash;
            }

            WriteU32(block, offset, inode);
            WriteU16(block, offset + 4, (ushort)naturalLength);
            block[offset + 6] = (byte)nameLen;
            block[offset + 7] = fileType;
            nameBytes.AsSpan(0, nameLen).CopyTo(block.AsSpan(offset + 8));

            lastHashOfBlock = hash;
            lastEntryOffset = offset;
            offset += naturalLength;
            entriesInBlock++;
        }

        Flush();
        return (blocks, firstHashes, lastHashes);
    }

    /// <summary>
    /// Builds the child list of a dx block: child i maps to leaf (start + i) at block leafBlockBase + start + i.
    /// <para>生成某个 dx 块的子项列表：子项 i 对应第 (start + i) 个叶块，block = leafBlockBase + start + i； 子项 0 的 hash 未使用，子项 i ≥ 1 的 hash 为对应叶块首条目的哈希（与上一叶末条同值时 +1，见 e2fsprogs）。</para>
    /// </summary>
    private static List<(uint Hash, uint Block)> BuildChildEntryList(
        List<uint> leafFirstHashes,
        List<uint> leafLastHashes,
        int start,
        int count,
        int leafBlockBase)
    {
        var children = new List<(uint Hash, uint Block)>(count);
        for (var i = 0; i < count; i++)
        {
            var leafIndex = start + i;
            var hash = i == 0 ? 0u : leafFirstHashes[leafIndex];
            if (i > 0 && hash == leafLastHashes[leafIndex - 1])
            {
                hash++;
            }

            children.Add((hash, (uint)(leafBlockBase + leafIndex)));
        }

        return children;
    }

    /// <summary>
    /// Builds the dx_root block (directory logical block 0): "." and ".." entries, dx_root_info, the count/limit area and the dx_entry array.
    /// <para>构建 dx_root 块（目录逻辑块 0）："."、".." 目录项 + dx_root_info + count/limit + dx_entry 数组。</para>
    /// </summary>
    private static byte[] BuildDxRootBlock(
        int blockSize,
        uint directoryInode,
        uint parentInode,
        int count,
        int limit,
        int indirectLevels,
        List<(uint Hash, uint Block)> children,
        bool enableMetadataCsum = false,
        byte[]? uuid = null,
        uint generation = 0)
    {
        var block = new byte[blockSize];

        WriteU32(block, 0, directoryInode);
        WriteU16(block, 4, DotEntrySize);
        block[6] = 1;
        block[7] = Ext4DirectoryEntry.TypeDirectory;
        block[8] = (byte)'.';

        WriteU32(block, 12, parentInode);
        WriteU16(block, 16, (ushort)(blockSize - DotEntrySize));
        block[18] = 2;
        block[19] = Ext4DirectoryEntry.TypeDirectory;
        block[20] = (byte)'.';
        block[21] = (byte)'.';

        block[DotEntrySize + DotEntrySize + 4] = HashVersionHalfMd4;
        block[DotEntrySize + DotEntrySize + 5] = DxRootInfoSize;
        block[DotEntrySize + DotEntrySize + 6] = (byte)indirectLevels;

        WriteDxEntries(block, DxRootCountOffset, count, limit, children);

        if (enableMetadataCsum)
        {
            WriteDxTail(block, DxRootCountOffset, count, limit, directoryInode, uuid, generation);
        }

        return block;
    }

    /// <summary>
    /// Builds a dx_node index block: an 8-byte fake dirent (inode 0, rec_len blockSize, name_len 0), the count/limit area and the dx_entry array.
    /// <para>构建 dx_node 索引块：8 字节伪目录项（inode=0, rec_len=blockSize, name_len=0）+ count/limit + dx_entry 数组。</para>
    /// </summary>
    private static byte[] BuildDxNodeBlock(
        int blockSize,
        int count,
        int limit,
        List<(uint Hash, uint Block)> children,
        uint directoryInode = 0,
        bool enableMetadataCsum = false,
        byte[]? uuid = null,
        uint generation = 0)
    {
        var block = new byte[blockSize];
        WriteU32(block, 0, 0);
        WriteU16(block, 4, (ushort)blockSize);

        WriteDxEntries(block, DxNodeCountOffset, count, limit, children);

        if (enableMetadataCsum)
        {

            WriteDxTail(block, DxNodeCountOffset, count, limit, directoryInode, uuid, generation);
        }

        return block;
    }

    /// <summary>
    /// Writes the limit/count area and the dx_entry array of a dx block (entry[0] only stores its block because its hash area is occupied by limit/count).
    /// <para>写入 dx 块的 limit/count 区与 dx_entry 数组（entry[0] 只写 block，hash 区已被 limit/count 占用）。</para>
    /// </summary>
    private static void WriteDxEntries(byte[] block, int countOffset, int count, int limit, List<(uint Hash, uint Block)> children)
    {
        WriteU16(block, countOffset, (ushort)limit);
        WriteU16(block, countOffset + 2, (ushort)count);

        if (count > 0)
        {
            WriteU32(block, countOffset + 4, children[0].Block);
        }

        for (var i = 1; i < count; i++)
        {
            var pos = countOffset + i * DxEntrySize;
            WriteU32(block, pos, children[i].Hash);
            WriteU32(block, pos + 4, children[i].Block);
        }
    }

    /// <summary>
    /// Writes the 12-byte struct ext2_dir_entry_tail at the end of a directory leaf block and computes the dirent checksum matching e2fsprogs ext2fs_dirent_csum: seed = crc32c(~0, uuid), then the LE32 inode number, LE32 i_generation and the block bytes from its start up to (excluding) the tail.
    /// <para>在目录叶块尾部写入 struct ext2_dir_entry_tail（12 字节）并计算 dirent 校验和。</para>
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static void WriteDirentTail(byte[] block, int endCap, uint directoryInode, byte[]? uuid, uint generation)
    {
        WriteU32(block, endCap, 0);
        WriteU16(block, endCap + 4, DirentTailSize);
        block[endCap + 6] = 0x00;
        block[endCap + 7] = 0xDE;

        var crc = HtreeCrc32c(uuid ?? new byte[16], Crc32c.InitialSeed);
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, directoryInode);
        crc = HtreeCrc32c(le, crc);
        BinaryPrimitives.WriteUInt32LittleEndian(le, generation);
        crc = HtreeCrc32c(le, crc);
        crc = HtreeCrc32c(block.AsSpan(0, endCap), crc);
        WriteU32(block, endCap + 8, crc);
    }

    private static void WriteU16(byte[] bytes, int offset, ushort value) => ByteWriter.WriteUInt16LE(bytes, offset, value);

    private static void WriteU32(byte[] bytes, int offset, uint value) => ByteWriter.WriteUInt32LE(bytes, offset, value);

    /// <summary>
    /// Writes dx_tail and computes checksum matching e2fsprogs 1.47.4 ext2fs_dx_csum. dx_tail is at countOffset + limit * DxEntrySize.
    /// <para>写入 dx_tail 结构并计算校验和。</para>
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static void WriteDxTail(byte[] block, int countOffset, int count, int limit, uint directoryInode, byte[]? uuid, uint generation)
    {

        var tailOffset = countOffset + limit * DxEntrySize;

        block.AsSpan(tailOffset, DxTailSize).Clear();

        var crc = HtreeCrc32c(uuid ?? new byte[16], Crc32c.InitialSeed);

        Span<byte> le4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le4, directoryInode);
        crc = HtreeCrc32c(le4, crc);

        BinaryPrimitives.WriteUInt32LittleEndian(le4, generation);
        crc = HtreeCrc32c(le4, crc);

        crc = HtreeCrc32c(block.AsSpan(0, countOffset + count * DxEntrySize), crc);
        crc = HtreeCrc32c(block.AsSpan(tailOffset, DxTailSize), crc);

        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(tailOffset + 4, 4), crc);
    }

    private static uint HtreeCrc32c(ReadOnlySpan<byte> data, uint seed) => Crc32c.Compute(data, seed);

    public sealed record HtreePlan(byte[] RootBlock, List<byte[]> LeafBlocks, List<byte[]> IndexBlocks, int IndirectLevels);
}