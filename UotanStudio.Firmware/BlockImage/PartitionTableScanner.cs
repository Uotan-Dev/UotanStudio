using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Mbr;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.BlockImage;

/// <summary>
/// Locates a partition table that does not begin at the start of the bytes being examined. Some MediaTek eMMC
/// regions leave erased space ahead of the table, so a reader that only inspects the first sector reports no
/// table at all even though one exists further in.
/// <para>定位并不位于被检查字节起始处的分区表。部分联发科 eMMC 区域在分区表之前留有擦除空间，只检查首个扇区的读取器
/// 会报告无分区表，即使更靠后的位置确实存在一张。</para>
/// </summary>
/// <remarks>
/// A candidate is accepted only after structural validation, because raw storage legitimately contains stray
/// <c>EFI PART</c> strings and <c>55 AA</c> bytes that a signature-only check would report as tables. Every GPT
/// candidate is considered before any MBR candidate so a protective MBR cannot mask the outer layout. The
/// reported offset is the base a table reader must start from: for GPT that is the sector holding the protective
/// MBR, one sector before the header, and for MBR it is the sector carrying the table. A table found away from
/// the extent start may describe a nested volume rather than the storage being scanned, so callers must present
/// the offset instead of assuming either meaning.
/// <para>候选位置只有通过结构校验才会被接受，因为原始存储中本来就含有零散的 <c>EFI PART</c> 字符串与 <c>55 AA</c> 字节，
/// 仅凭签名判断会把它们当成分区表。所有 GPT 候选先于任何 MBR 候选处理，避免保护性 MBR 掩盖外层布局。上报的偏移是分区表
/// 读取器必须从其开始的基址：GPT 为承载保护性 MBR 的扇区，即头部之前的一个扇区；MBR 为承载该表的扇区。位于范围起始处之外
/// 的表可能描述的是嵌套卷而不是被扫描的存储，因此调用方必须如实呈现该偏移，而不是对两种含义做假定。</para>
/// </remarks>
public static class PartitionTableScanner
{
    /// <summary>The logical sector size every candidate is aligned to.
    /// <para>每个候选位置对齐到的逻辑扇区长度。</para></summary>
    public const int SectorSize = 512;

    /// <summary>The largest partition-entry count a GPT candidate may declare.
    /// <para>GPT 候选项允许声明的最大分区条目数。</para></summary>
    private const int MaximumGptEntryCount = 1024;

    /// <summary>The smallest partition-entry size a GPT candidate may declare.
    /// <para>GPT 候选项允许声明的最小分区条目长度。</para></summary>
    private const int MinimumGptEntrySize = 128;

    /// <summary>The entry size every GPT candidate must use a power-of-two multiple of.
    /// <para>GPT 候选使用的条目长度必须是该值的二的幂倍数。</para></summary>
    private const int MaximumGptEntrySize = 4096;

    /// <summary>
    /// Finds the first sector-aligned partition table inside <paramref name="window"/>.
    /// <para>在 <paramref name="window"/> 中查找首个扇区对齐的分区表。</para>
    /// </summary>
    /// <param name="window">The bytes to scan, starting at <paramref name="windowOffset"/>. <para>从 <paramref name="windowOffset"/> 开始待扫描的字节。</para></param>
    /// <param name="windowOffset">The extent-relative offset of <paramref name="window"/>'s first byte, which must lie inside the extent. <para><paramref name="window"/> 首字节相对于整体范围的偏移，必须位于该范围之内。</para></param>
    /// <param name="extentBytes">The full byte length of the extent the window belongs to. <para>窗口所属整体范围的字节总长。</para></param>
    /// <param name="tableOffset">The extent-relative base of the accepted table, or <c>-1</c> on failure. <para>被接受分区表的整体相对基址；失败时为 <c>-1</c>。</para></param>
    /// <param name="kind">The accepted layout, or <see cref="PartitionTableKind.Unknown"/> on failure. <para>被接受的布局；失败时为 <see cref="PartitionTableKind.Unknown"/>。</para></param>
    /// <returns><see langword="true"/> when a validated table was located. <para>定位到通过校验的分区表时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowOffset"/> is negative or <paramref name="extentBytes"/> is not positive.
    /// <para><paramref name="windowOffset"/> 为负，或 <paramref name="extentBytes"/> 不为正。</para></exception>
    public static bool TryFind(ReadOnlySpan<byte> window, long windowOffset, long extentBytes,
        out long tableOffset, out PartitionTableKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(extentBytes);

        tableOffset = -1;
        kind = PartitionTableKind.Unknown;
        if (windowOffset >= extentBytes)
        {
            return false;
        }

        for (var relative = 0; relative + SectorSize <= window.Length; relative += SectorSize)
        {
            var headerOffset = windowOffset + relative;
            if (headerOffset < SectorSize)
            {
                continue;
            }

            if (IsValidGptHeader(window.Slice(relative, SectorSize), headerOffset, extentBytes))
            {
                tableOffset = headerOffset - SectorSize;
                kind = PartitionTableKind.Gpt;
                return true;
            }
        }

        for (var relative = 0; relative + SectorSize <= window.Length; relative += SectorSize)
        {
            if (MbrTable.HasPartitionTable(window.Slice(relative, SectorSize)))
            {
                tableOffset = windowOffset + relative;
                kind = PartitionTableKind.Mbr;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates one sector as the primary GPT header of the table based at the preceding sector.
    /// <para>把一个扇区作为基址位于前一扇区的分区表的主 GPT 头部进行校验。</para>
    /// </summary>
    private static bool IsValidGptHeader(ReadOnlySpan<byte> sector, long headerOffset, long extentBytes)
    {
        if (!sector[..GptLayout.Signature.Length].SequenceEqual(GptLayout.Signature)
            || BinaryPrimitives.ReadUInt32LittleEndian(sector[8..]) != GptLayout.Revision)
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector[12..]);
        if (headerSize < GptLayout.HeaderSize || headerSize > SectorSize)
        {
            return false;
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector[GptLayout.HeaderCrcOffset..]);
        Span<byte> scratch = stackalloc byte[(int)headerSize];
        sector[..(int)headerSize].CopyTo(scratch);
        scratch.Slice(GptLayout.HeaderCrcOffset, 4).Clear();
        if (Crc32Ieee.Compute(scratch) != storedCrc)
        {
            return false;
        }

        var baseOffset = headerOffset - SectorSize;
        var sectors = (extentBytes - baseOffset) / SectorSize;
        var myLba = BinaryPrimitives.ReadUInt64LittleEndian(sector[24..]);
        var alternateLba = BinaryPrimitives.ReadUInt64LittleEndian(sector[32..]);
        var firstUsable = BinaryPrimitives.ReadUInt64LittleEndian(sector[40..]);
        var lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(sector[48..]);
        var entryLba = BinaryPrimitives.ReadUInt64LittleEndian(sector[72..]);
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(sector[84..]);
        return myLba == GptLayout.PrimaryHeaderLba
            && alternateLba > myLba
            && alternateLba < (ulong)sectors
            && firstUsable > GptLayout.PrimaryHeaderLba
            && lastUsable >= firstUsable
            && lastUsable < alternateLba
            && entryLba > GptLayout.PrimaryHeaderLba
            && entryLba <= (ulong)sectors
            && entryCount is > 0 and <= MaximumGptEntryCount
            && entrySize is >= MinimumGptEntrySize and <= MaximumGptEntrySize
            && (entrySize & (entrySize - 1)) == 0;
    }
}