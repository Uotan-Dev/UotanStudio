using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Core;

/// <summary>
/// ext4 extent tree (header + index entries + leaf extents) parsing and
/// logical-block → physical-block mapping. Sparse holes return <see langword="null"/>.
/// <para>ext4 extent 树（头部 + 索引项 + 叶 extent）解析与逻辑块→物理块映射，
/// 稀疏洞返回 <see langword="null"/>。</para>
/// </summary>
internal static class Ext4Extent
{
    /// <summary>
    /// Extent header magic (0xF30A).
    /// <para>extent 头部魔数（0xF30A）。</para>
    /// </summary>
    public const ushort MagicValue = 0xF30A;

    /// <summary>
    /// Maximum number of blocks covered by a single initialized extent.
    /// <para>单个已初始化 extent 覆盖的最大块数。</para>
    /// </summary>
    public const uint MaxExtentLength = 32768;

    /// <summary>
    /// Maximum extent tree depth (EXT4_EXT_MAX_DEPTH); guards against malicious index
    /// blocks that would otherwise drive unbounded recursion.
    /// <para>extent 树最大深度（EXT4_EXT_MAX_DEPTH）；防止恶意索引块导致无界递归。</para>
    /// </summary>
    public const int MaxExtentDepth = 5;

    /// <summary>
    /// Resolves a logical block number to a physical block number, or <see langword="null"/>
    /// for a sparse hole (including unwritten extents, whose contents read as zeros).
    /// <paramref name="readBlock"/> reads a whole block by physical number
    /// and is used to fetch index-tree leaf blocks.
    /// <para>将逻辑块号解析为物理块号；稀疏洞（包括内容按零读取的未写 extent）返回
    /// <see langword="null"/>。<paramref name="readBlock"/> 按物理块号读取整个块，
    /// 用于获取索引树叶块。</para>
    /// </summary>
    public static long? Resolve(ReadOnlySpan<byte> extentBytes, uint logicalBlock, Func<ulong, byte[]> readBlock)
        => ResolveAtDepth(extentBytes, logicalBlock, readBlock, MaxExtentDepth);

    private static long? ResolveAtDepth(
        ReadOnlySpan<byte> extentBytes, uint logicalBlock, Func<ulong, byte[]> readBlock, int remainingDepth)
    {
        if (extentBytes.Length < 12)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(extentBytes[0..2]);
        if (magic != MagicValue)
        {
            return null;
        }

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(extentBytes[2..4]);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(extentBytes[6..8]);
        if (depth > remainingDepth)
        {
            throw new InvalidDataException(
                $"ext4 extent tree depth {depth} exceeds the supported maximum of {MaxExtentDepth}.");
        }

        return depth == 0
            ? ResolveLeaf(extentBytes, logicalBlock, entryCount)
            : ResolveIndex(extentBytes, logicalBlock, entryCount, remainingDepth - 1, readBlock);
    }

    private static long? ResolveLeaf(ReadOnlySpan<byte> block, uint logicalBlock, int entryCount)
    {
        var offset = 12;
        for (var i = 0; i < entryCount; i++)
        {
            if (offset + 12 > block.Length)
            {
                break;
            }

            var eeBlock = BinaryPrimitives.ReadUInt32LittleEndian(block[offset..(offset + 4)]);
            var eeRawLength = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 4)..(offset + 6)]);
            var eeStartHi = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 6)..(offset + 8)]);
            var eeStartLo = BinaryPrimitives.ReadUInt32LittleEndian(block[(offset + 8)..(offset + 12)]);
            // An ee_len with the top bit set marks an unwritten extent; its actual length is
            // ee_len & 0x7FFF and its contents read as zeros (a hole).
            // ee_len 高位为 1 表示未写 extent：实际长度是 ee_len & 0x7FFF，内容按零读取（洞）。
            var isUnwritten = eeRawLength > MaxExtentLength;
            var eeLength = isUnwritten ? eeRawLength & (MaxExtentLength - 1) : eeRawLength;
            if (logicalBlock >= eeBlock && logicalBlock - eeBlock < eeLength)
            {
                if (isUnwritten)
                {
                    return null;
                }

                var start = (long)(((ulong)eeStartHi << 32) | eeStartLo);
                return start + (logicalBlock - eeBlock);
            }

            offset += 12;
        }

        return null;
    }

    private static long? ResolveIndex(
        ReadOnlySpan<byte> block, uint logicalBlock, int entryCount, int remainingDepth, Func<ulong, byte[]> readBlock)
    {
        // Binary search the last index whose ei_block <= logicalBlock.
        // 二分查找最后一个 ei_block <= logicalBlock 的索引项。
        var low = 0;
        var high = entryCount - 1;
        var found = -1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var entryOffset = 12 + mid * 12;
            if (entryOffset + 12 > block.Length)
            {
                high = mid - 1;
                continue;
            }

            var eiBlock = BinaryPrimitives.ReadUInt32LittleEndian(block[entryOffset..(entryOffset + 4)]);
            if (eiBlock <= logicalBlock)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        var leafOffset = 12 + found * 12;
        // +4: ei_leaf_lo (physical block lo), +8: ei_leaf_hi (physical block hi).
        // +0 是 ei_block（逻辑块号），不可用作物理地址。
        var eiLeafLo = BinaryPrimitives.ReadUInt32LittleEndian(block[(leafOffset + 4)..(leafOffset + 8)]);
        var eiLeafHi = BinaryPrimitives.ReadUInt16LittleEndian(block[(leafOffset + 8)..(leafOffset + 10)]);
        var leafBlock = ((ulong)eiLeafHi << 32) | eiLeafLo;
        var leafBytes = readBlock(leafBlock);
        return ResolveAtDepth(leafBytes, logicalBlock, readBlock, remainingDepth);
    }
}