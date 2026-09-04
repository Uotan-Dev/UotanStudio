using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Ext4.Core;

/// <summary>
/// ext4 htree（哈希 B 树）目录索引支持：Half-MD4 目录名哈希、dx_root/dx_node 块构建与解析。
/// 磁盘布局与 e2fsprogs/libext2fs 一致：dx_root 位于目录逻辑块 0，含 "."、".." 两个真实目录项，
/// 偏移 24 起为 dx_root_info，偏移 32 起为 {limit, count} 计数区与 dx_entry 数组
/// （entry[0] 的 hash 区被 limit/count 覆盖，仅保留 block 指向第一个子块）。
/// <para>Half-MD4 hash, dx_root/dx_node construction and parsing for ext4 htree (hashed B-tree)
/// directory indexing. The on-disk layout matches e2fsprogs/libext2fs: dx_root lives in directory
/// logical block 0 holding real "." and ".." entries, dx_root_info starts at offset 24, and the
/// {limit, count} area plus the dx_entry array start at offset 32 (entry[0]'s hash area is overlaid
/// by limit/count so it only carries a block that points at the first child).</para>
/// </summary>
internal static class Ext4Htree
{
    /// <summary>HALF_MD4 哈希版本号（EXT2_HASH_HALF_MD4 = 1）。<para>HALF_MD4 hash version (EXT2_HASH_HALF_MD4 = 1).</para></summary>
    public const byte HashVersionHalfMd4 = 1;

    /// <summary>EXT4_INDEX_FL 标志：目录使用 htree 索引。<para>EXT4_INDEX_FL flag: directory uses htree index.</para></summary>
    public const uint FlagIndex = 0x0000_1000;

    /// <summary>dx_entry 大小（字节：4 字节 hash LE + 4 字节 block LE）。<para>dx_entry size (4-byte LE hash + 4-byte LE block).</para></summary>
    public const int DxEntrySize = 8;

    /// <summary>dx_root_info 结构大小（字节）。<para>dx_root_info structure size in bytes.</para></summary>
    public const int DxRootInfoSize = 8;

    /// <summary>"."（或 ".."）目录项记录大小（8 字节头 + 名字 + 4 字节对齐）。<para>Record size of a "." (or "..") directory entry (8-byte header + name + 4-byte alignment).</para></summary>
    public const int DotEntrySize = 12;

    /// <summary>dx_root 中 limit/count 区的起始偏移（"." 12 + ".." 12 + dx_root_info 8 = 32）。
    /// <para>dx_root limit/count area offset (12 + 12 + 8 = 32).</para></summary>
    public const int DxRootCountOffset = DotEntrySize + DotEntrySize + DxRootInfoSize;

    /// <summary>dx_node 中 limit/count 区的起始偏移（8 字节伪目录项之后）。
    /// <para>dx_node limit/count offset (right after the 8-byte fake dirent).</para></summary>
    public const int DxNodeCountOffset = 8;

    /// <summary>哈希种子全零时使用的默认 MD4 初始化向量。<para>Default MD4 init vector used when the hash seed is all zeros.</para></summary>
    private static readonly uint[] DefaultHashInit = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];

    /// <summary>Half-MD4 第 2/3 轮常量（= MD4 的 K2/K3）。<para>Half-MD4 round 2/3 constants (= MD4 K2/K3).</para></summary>
    private const uint Md4K2 = 0x5A82_7999;
    private const uint Md4K3 = 0x6ED9_EBA1;

    /// <summary>
    /// 计算目录名的 ext4 Half-MD4 哈希（返回 buf[1] &amp; ~1）。与 e2fsprogs ext2fs_dirhash 的
    /// EXT2_HASH_HALF_MD4 分支一致：种子全零时改用默认 MD4 初始化向量，名字按 32 字节分块做 halfMD4 变换。
    /// <para>Computes the ext4 Half-MD4 hash of a directory name (buf[1] &amp; ~1), matching the
    /// EXT2_HASH_HALF_MD4 branch of e2fsprogs ext2fs_dirhash: an all-zero seed selects the default MD4
    /// init vector and the name is transformed in 32-byte chunks.</para>
    /// </summary>
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
    /// 将名字字节转为最多 8 个 uint32 词（小端累积 + 长度填充），与 e2fsprogs str2hashbuf(num=8) 一致。
    /// <para>Converts name bytes into up to 8 uint32 words (little-endian accumulation + length padding),
    /// matching e2fsprogs str2hashbuf(num=8).</para>
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
    /// 截断 MD4 变换，与 e2fsprogs halfMD4Transform 逐行一致。
    /// <para>Truncated MD4 transform, line-for-line equivalent to e2fsprogs halfMD4Transform.</para>
    /// </summary>
    private static void HalfMd4Transform(uint[] buf, uint[] input)
    {
        var a = buf[0];
        var b = buf[1];
        var c = buf[2];
        var d = buf[3];

        // Round 1 (F).
        a = RotateLeft(a + F(b, c, d) + input[0], 3);
        d = RotateLeft(d + F(a, b, c) + input[1], 7);
        c = RotateLeft(c + F(d, a, b) + input[2], 11);
        b = RotateLeft(b + F(c, d, a) + input[3], 19);
        a = RotateLeft(a + F(b, c, d) + input[4], 3);
        d = RotateLeft(d + F(a, b, c) + input[5], 7);
        c = RotateLeft(c + F(d, a, b) + input[6], 11);
        b = RotateLeft(b + F(c, d, a) + input[7], 19);

        // Round 2 (G).
        a = RotateLeft(a + G(b, c, d) + input[1] + Md4K2, 3);
        d = RotateLeft(d + G(a, b, c) + input[3] + Md4K2, 5);
        c = RotateLeft(c + G(d, a, b) + input[5] + Md4K2, 9);
        b = RotateLeft(b + G(c, d, a) + input[7] + Md4K2, 13);
        a = RotateLeft(a + G(b, c, d) + input[0] + Md4K2, 3);
        d = RotateLeft(d + G(a, b, c) + input[2] + Md4K2, 5);
        c = RotateLeft(c + G(d, a, b) + input[4] + Md4K2, 9);
        b = RotateLeft(b + G(c, d, a) + input[6] + Md4K2, 13);

        // Round 3 (H).
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
    /// 从目录块 0 解析 dx_root 的 dx_entry 列表：返回每个子块（叶块/索引块）的 (hash, block)。
    /// entry[0] 的 hash 未存盘（该区域被 limit/count 占用），故以 0 占位；其 block 指向第一个子块。
    /// 不能识别 dx_root 结构时返回空列表。
    /// <para>Parses the dx_entry list from the dx_root in directory block 0, returning (hash, block)
    /// for every child block. entry[0]'s hash is not stored on disk (its area holds limit/count), so 0
    /// is reported; its block points at the first child. Returns an empty list when the block is not a
    /// dx_root.</para>
    /// </summary>
    public static List<(uint Hash, uint Block)> ParseDxEntries(ReadOnlySpan<byte> block0, int blockSize)
    {
        if (block0.Length < DxRootCountOffset + DxEntrySize)
        {
            return [];
        }

        // 校验前置 "."（rec_len=12）与 ".."（rec_len=blockSize-12）目录项。
        // Validate the leading "." (rec_len 12) and ".." (rec_len blockSize - 12) entries.
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

            // entry[0] 的 hash 区被 limit/count 覆盖，磁盘上不存哈希，故返回占位值 0；
            // 其 block 指向第一个叶块。子项 i ≥ 1 的 hash/block 为真实 dx_entry。
            // entry[0]'s hash area is occupied by limit/count so no hash is stored on disk; report 0.
            // Its block points at the first leaf. Children i ≥ 1 carry the real dx_entry hash/block.
            var hash = i == 0 ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(block0[pos..]);
            var childBlock = BinaryPrimitives.ReadUInt32LittleEndian(block0[(pos + 4)..]);
            entries.Add((hash, childBlock));
        }

        return entries;
    }

    /// <summary>
    /// 构建 htree 索引的重载：不提供 "."/".." 的 inode 号时以 0 填充（仅用于结构/解析测试）。
    /// <para>Overload of the htree builder that fills "." and ".." with inode 0 (structure/parse tests
    /// only). Use the overload taking explicit directory/parent inodes when building real directories.</para>
    /// </summary>
    public static HtreePlan BuildHtree(
        List<(string Name, uint Inode, byte FileType)> entries,
        int blockSize,
        uint[] hashSeed)
    {
        return BuildHtree(0, 0, entries, blockSize, hashSeed);
    }

    /// <summary>
    /// 为目录构建 htree 索引。输入为全部子目录项（不含 "."、".."；二者由 dx_root 块 0 承载），
    /// 输出为按哈希升序打包的叶块、可选的 dx_node 索引块与 dx_root 块。
    /// 约定叶块逻辑块号从 1 开始；两层树时索引块紧跟叶块（逻辑块 L+1..L+N），
    /// dx_entry 中的 block 一律引用目录文件内的全局逻辑块号。
    /// <para>Builds an htree index for a directory. The input is the full child list (without "." and
    /// "..", both of which live in dx_root block 0). Returns hash-ordered leaf blocks, optional dx_node
    /// index blocks and the dx_root block. Leaf logical block numbers always start at 1; in a two-level
    /// tree the index blocks follow the leaves (blocks L+1..L+N). All dx_entry block numbers reference
    /// absolute logical block numbers inside the directory file.</para>
    /// </summary>
    public static HtreePlan BuildHtree(
        uint directoryInode,
        uint parentInode,
        List<(string Name, uint Inode, byte FileType)> entries,
        int blockSize,
        uint[] hashSeed)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(hashSeed);

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

        var (leafBlocks, leafFirstHashes, leafLastHashes) = PackLeafBlocks(hashed, blockSize);
        if (leafBlocks.Count == 0)
        {
            // 空目录由 Builder 走线性单块路径，这里只作防御性返回一个空根。
            // Empty directories are kept linear by the Builder; return an empty root defensively.
            return new HtreePlan(BuildDxRootBlock(blockSize, directoryInode, parentInode, 0, 1, 0, []), [], [], 0);
        }

        var rootLimit = (blockSize - DotEntrySize - DotEntrySize - DxRootInfoSize) / DxEntrySize;
        var nodeLimit = (blockSize - DxNodeCountOffset) / DxEntrySize;
        byte[] rootBlock;
        List<byte[]> indexBlocks;

        if (leafBlocks.Count <= rootLimit)
        {
            // 单层：dx_root 直接指向叶块（逻辑块 1..L），indirect_levels = 0。
            // Single level: dx_root points directly at the leaves (logical blocks 1..L).
            var children = BuildChildEntryList(leafFirstHashes, leafLastHashes, 0, leafBlocks.Count, leafBlockBase: 1);
            rootBlock = BuildDxRootBlock(blockSize, directoryInode, parentInode, children.Count, rootLimit, 0, children);
            indexBlocks = [];
        }
        else
        {
            if (leafBlocks.Count > (long)rootLimit * nodeLimit)
            {
                throw new InvalidOperationException(
                    $"Directory requires more than two htree levels ({leafBlocks.Count} leaf blocks), which is not supported.");
            }

            // 两层：dx_root（块 0）→ dx_node（块 L+1..L+N）→ 叶块（块 1..L），indirect_levels = 1。
            // Two levels: dx_root (block 0) → dx_node (blocks L+1..L+N) → leaves (blocks 1..L).
            var leafCount = leafBlocks.Count;
            var nodeCount = (leafCount + nodeLimit - 1) / nodeLimit;
            var nodeBlocks = new List<byte[]>(nodeCount);
            var rootChildren = new List<(uint Hash, uint Block)>(nodeCount);
            for (var n = 0; n < nodeCount; n++)
            {
                var firstLeaf = n * nodeLimit;
                var nodeLeafCount = Math.Min(nodeLimit, leafCount - firstLeaf);
                var nodeChildren = BuildChildEntryList(leafFirstHashes, leafLastHashes, firstLeaf, nodeLeafCount, leafBlockBase: 1);
                nodeBlocks.Add(BuildDxNodeBlock(blockSize, nodeChildren.Count, nodeLimit, nodeChildren));

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

            rootBlock = BuildDxRootBlock(blockSize, directoryInode, parentInode, rootChildren.Count, rootLimit, 1, rootChildren);
            indexBlocks = nodeBlocks;
        }

        return new HtreePlan(rootBlock, leafBlocks, indexBlocks, indexBlocks.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// 按哈希顺序将目录项打包进叶块，返回叶块字节数组及各叶块首/末条目的哈希。
    /// <para>Packs hash-ordered directory entries into leaf blocks, returning the leaf byte arrays and
    /// the first/last-entry hash of every leaf.</para>
    /// </summary>
    private static (List<byte[]> Blocks, List<uint> FirstHashes, List<uint> LastHashes) PackLeafBlocks(
        List<(uint Hash, string Name, uint Inode, byte FileType)> sorted,
        int blockSize)
    {
        var blocks = new List<byte[]>();
        var firstHashes = new List<uint>();
        var lastHashes = new List<uint>();

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

            // 块内最后一条目录项的 rec_len 延伸到块尾。
            // The final entry's rec_len stretches to the end of the block.
            WriteU16(block, lastEntryOffset + 4, (ushort)(blockSize - lastEntryOffset));
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
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var nameLen = Math.Min(nameBytes.Length, 255);
            var naturalLength = (8 + nameLen + 3) & ~3;
            if (entriesInBlock > 0 && offset + naturalLength > blockSize)
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
    /// 生成某个 dx 块的子项列表：子项 i 对应第 (start + i) 个叶块，block = leafBlockBase + start + i；
    /// 子项 0 的 hash 未使用，子项 i ≥ 1 的 hash 为对应叶块首条目的哈希（与上一叶末条同值时 +1，见 e2fsprogs）。
    /// <para>Builds the child list of a dx block: child i maps to leaf (start + i) at block
    /// leafBlockBase + start + i. Child 0's hash is unused; child i ≥ 1 hashes to its leaf's first entry
    /// hash (bumped by 1 when it equals the previous leaf's last hash, as e2fsprogs does).</para>
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
    /// 构建 dx_root 块（目录逻辑块 0）："."、".." 目录项 + dx_root_info + count/limit + dx_entry 数组。
    /// <para>Builds the dx_root block (directory logical block 0): "." and ".." entries, dx_root_info,
    /// the count/limit area and the dx_entry array.</para>
    /// </summary>
    private static byte[] BuildDxRootBlock(
        int blockSize,
        uint directoryInode,
        uint parentInode,
        int count,
        int limit,
        int indirectLevels,
        List<(uint Hash, uint Block)> children)
    {
        var block = new byte[blockSize];

        // "." 目录项。
        WriteU32(block, 0, directoryInode);
        WriteU16(block, 4, DotEntrySize);
        block[6] = 1;  // name_len
        block[7] = 2;  // file_type = directory
        block[8] = (byte)'.';

        // ".." 目录项，rec_len 覆盖块内剩余空间（dx 结构与索引数据藏于其尾部空闲区）。
        // ".." entry whose rec_len covers the rest of the block (the dx data lives in its slack).
        WriteU32(block, 12, parentInode);
        WriteU16(block, 16, (ushort)(blockSize - DotEntrySize));
        block[18] = 2;
        block[19] = 2;
        block[20] = (byte)'.';
        block[21] = (byte)'.';

        // dx_root_info（偏移 24 起）：reserved_zero + hash_version + info_length + indirect_levels + unused。
        // dx_root_info starting at offset 24.
        block[DotEntrySize + DotEntrySize + 4] = HashVersionHalfMd4;
        block[DotEntrySize + DotEntrySize + 5] = DxRootInfoSize;
        block[DotEntrySize + DotEntrySize + 6] = (byte)indirectLevels;

        WriteDxEntries(block, DxRootCountOffset, count, limit, children);
        return block;
    }

    /// <summary>
    /// 构建 dx_node 索引块：8 字节伪目录项（inode=0, rec_len=blockSize, name_len=0）+ count/limit + dx_entry 数组。
    /// <para>Builds a dx_node index block: an 8-byte fake dirent (inode 0, rec_len blockSize, name_len 0),
    /// the count/limit area and the dx_entry array.</para>
    /// </summary>
    private static byte[] BuildDxNodeBlock(
        int blockSize,
        int count,
        int limit,
        List<(uint Hash, uint Block)> children)
    {
        var block = new byte[blockSize];
        WriteU32(block, 0, 0);                       // inode = 0
        WriteU16(block, 4, (ushort)blockSize);       // rec_len = blockSize
        // name_len = 0, file_type = 0 (zeroed).

        WriteDxEntries(block, DxNodeCountOffset, count, limit, children);
        return block;
    }

    /// <summary>
    /// 写入 dx 块的 limit/count 区与 dx_entry 数组（entry[0] 只写 block，hash 区已被 limit/count 占用）。
    /// <para>Writes the limit/count area and the dx_entry array of a dx block (entry[0] only stores its
    /// block because its hash area is occupied by limit/count).</para>
    /// </summary>
    private static void WriteDxEntries(byte[] block, int countOffset, int count, int limit, List<(uint Hash, uint Block)> children)
    {
        WriteU16(block, countOffset, (ushort)limit);
        WriteU16(block, countOffset + 2, (ushort)count);

        // entry[0]：仅 block 有效（指向第一个子块）。
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

    private static void WriteU16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteU32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    /// <summary>
    /// htree 构建计划：RootBlock（目录逻辑块 0）、叶块（逻辑块 1..L）、索引块（两层树时位于叶块之后），
    /// 以及树的层数（indirect_levels）。
    /// <para>Htree construction plan: RootBlock (logical block 0), leaf blocks (logical blocks 1..L),
    /// index blocks (following the leaves in a two-level tree) and the indirect level count.</para>
    /// </summary>
    public sealed record HtreePlan(byte[] RootBlock, List<byte[]> LeafBlocks, List<byte[]> IndexBlocks, int IndirectLevels);
}
