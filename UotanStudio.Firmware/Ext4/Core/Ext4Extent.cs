using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Core;

internal static class Ext4Extent
{
    public const ushort MagicValue = 0xF30A;

    public const uint MaxExtentLength = 32768;

    public const int MaxExtentDepth = 5;

    /// <summary>
    /// Resolves a logical block to its physical block, returning null for holes and unwritten extents.
    /// <para>将逻辑块解析为物理块，稀疏洞与未写 extent 返回 null。</para>
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


        var eiLeafLo = BinaryPrimitives.ReadUInt32LittleEndian(block[(leafOffset + 4)..(leafOffset + 8)]);
        var eiLeafHi = BinaryPrimitives.ReadUInt16LittleEndian(block[(leafOffset + 8)..(leafOffset + 10)]);
        var leafBlock = ((ulong)eiLeafHi << 32) | eiLeafLo;
        var leafBytes = readBlock(leafBlock);
        return ResolveAtDepth(leafBytes, logicalBlock, readBlock, remainingDepth);
    }
}
