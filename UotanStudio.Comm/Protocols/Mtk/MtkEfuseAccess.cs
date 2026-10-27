using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>Resolves eFuse cells and describes what a resolved cell means on the wire.
/// <para>解析 eFuse 单元，并说明解析结果在线路上的含义。</para></summary>
/// <remarks>
/// This client resolves a cell index through its per-chip table and then either reports
/// the value or peeks four bytes at it: an address below 0x1000 is returned as the four
/// little-endian bytes of the value itself, and any other address is read from the target
/// . The same source reads the boot public key as
/// the 0x30 bytes beginning at the eFuse base plus 0x90. No other eFuse semantics are claimed.
/// <para>本客户端先按逐芯片表解析单元索引，然后要么上报取值、要么在该地址窥探 4 字节：小于 0x1000
/// 的地址按该值本身的 4 个小端字节上报，其余地址则从目标读取。同一份源码把引导公钥读作从 eFuse 基址加 0x90
/// 开始的 0x30 字节。
/// 除此之外不声称任何 eFuse 语义。</para>
/// </remarks>
public static class MtkEfuseAccess
{
    /// <summary>The resolved value below which the reader reports the value instead of
    /// reading an address.
    /// <para>读取器在该解析值之下上报取值本身而不按地址读取。</para></summary>
    public const uint NonAddressThreshold = 0x1000;

    /// <summary>The byte width the reader asks for at every readable cell.
    /// <para>读取器在每个可读单元请求的字节宽度。</para></summary>
    public const int CellLength = 4;

    /// <summary>The offset of the boot public key from the eFuse base.
    /// <para>引导公钥相对 eFuse 基址的偏移。</para></summary>
    public const uint PublicKeyOffset = 0x90;

    /// <summary>The byte length of the boot public key window.
    /// <para>引导公钥窗口的字节长度。</para></summary>
    public const int PublicKeyLength = 0x30;

    /// <summary>The largest cell range a single request may cover.
    /// <para>单次请求可覆盖的最大单元数量。</para></summary>
    /// <remarks>
    /// The longest transcribed table has fewer cells than this, so the bound never rejects a
    /// documented request while still capping a caller-supplied range before any buffer is sized.
    /// 转录表中最长一条也不超过该数量，因此该上限不会拒绝任何已记载请求，同时会在分配缓冲区前约束
    /// 调用方给出的范围。
    /// </remarks>
    public const int MaximumRequestedCells = 256;

    /// <summary>Resolves one encoded cell against a caller supplied eFuse base.
    /// <para>依据调用方提供的 eFuse 基址解析一个编码单元。</para></summary>
    /// <param name="cell">One value from <see cref="MtkEfuseFieldTable.TryGetCells"/>.
    /// <para><see cref="MtkEfuseFieldTable.TryGetCells"/> 返回的一个取值。</para></param>
    /// <param name="efuseBase">The peripheral base address of the eFuse block.
    /// <para>eFuse 外设块的基地址。</para></param>
    /// <returns>The resolved cell. 解析后的单元。</returns>
    /// <exception cref="OverflowException">The base plus a relative offset would leave the 32-bit
    /// address space. <para>基址加相对偏移会超出 32 位地址空间。</para></exception>
    public static MtkEfuseCell ResolveCell(ulong cell, uint efuseBase)
    {
        if (MtkEfuseFieldTable.IsAbsolute(cell))
        {
            var absolute = (uint)cell;
            return new MtkEfuseCell(absolute, absolute < NonAddressThreshold);
        }

        var resolved = checked(efuseBase + (uint)cell);
        return new MtkEfuseCell(resolved, resolved < NonAddressThreshold);
    }

    /// <summary>Validates a cell range before any device request is built.
    /// <para>在构造任何设备请求之前校验单元范围。</para></summary>
    /// <param name="cells">The encoded table to index. 要索引的编码表。</param>
    /// <param name="startIndex">The first requested cell index. 请求的首个单元索引。</param>
    /// <param name="count">The requested cell count. 请求的单元数量。</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty, exceeds
    /// <see cref="MaximumRequestedCells"/>, or runs past the end of <paramref name="cells"/>;
    /// an unknown index is refused rather than clamped.
    /// <para>范围为空、超过 <see cref="MaximumRequestedCells"/> 或越过 <paramref name="cells"/> 末尾时拒绝；
    /// 未知索引会被拒绝而不会被截断容纳。</para></exception>
    public static void ValidateRange(ReadOnlySpan<ulong> cells, int startIndex, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumRequestedCells);
        if (startIndex >= cells.Length || checked(startIndex + count) > cells.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"The requested cells {startIndex}..{checked(startIndex + count - 1)} are outside the table of {cells.Length} cells.");
        }
    }

    /// <summary>Formats the four bytes the reader would report for a non-address cell.
    /// <para>格式化读取器为非地址单元上报的 4 个字节。</para></summary>
    /// <param name="address">The resolved value below <see cref="NonAddressThreshold"/>.
    /// <para>解析后小于 <see cref="NonAddressThreshold"/> 的取值。</para></param>
    /// <returns>Four little-endian bytes. 4 个小端字节。</returns>
    public static byte[] EncodeReportedValue(uint address)
    {
        var bytes = new byte[CellLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, address);
        return bytes;
    }
}

/// <summary>A resolved eFuse cell and how it must be obtained.
/// <para>已解析的 eFuse 单元及其获取方式。</para></summary>
/// <param name="Address">The absolute cell address, or the reported value itself.
/// <para>绝对单元地址，或即为待上报的取值。</para></param>
/// <param name="IsReportedWithoutRead">Whether the documented reader reports this value instead of
/// reading the target at it.
/// <para>已记载的读取器是否上报该取值而不按该地址读取目标。</para></param>
public readonly record struct MtkEfuseCell(uint Address, bool IsReportedWithoutRead);

/// <summary>One eFuse cell together with the bytes obtained for it.
/// <para>一个 eFuse 单元以及为其取得的字节。</para></summary>
/// <param name="Index">The zero-based cell index in the per-chip table.
/// <para>逐芯片表中从零开始的单元索引。</para></param>
/// <param name="Cell">How the index resolved against the eFuse base.
/// <para>该索引依据 eFuse 基址解析得到的结果。</para></param>
/// <param name="BytesHex">The exact bytes obtained, as uppercase hex.
/// <para>实际取得字节的大写十六进制表示。</para></param>
public sealed record MtkEfuseCellReading(int Index, MtkEfuseCell Cell, string BytesHex)
{
    /// <summary>Gets the decoded little-endian dword of <see cref="BytesHex"/>.
    /// <para>获取 <see cref="BytesHex"/> 的小端双字解码值。</para></summary>
    /// <value>
    /// The 32-bit value read or reported for this cell.
    /// <para>该单元读出或上报的 32 位取值。</para>
    /// </value>
    public uint Value => BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(BytesHex));
}

/// <summary>An eFuse read pass over one chip's cells.
/// <para>对某个芯片单元执行的一轮 eFuse 读取。</para></summary>
/// <param name="HardwareCode">The hardware code whose table was used.
/// <para>所用表对应的硬件代码。</para></param>
/// <param name="EfuseBase">The eFuse base address the cells were resolved against.
/// <para>解析单元所用的 eFuse 基址。</para></param>
/// <param name="Cells">The per-cell readings, in table order.
/// <para>逐单元读取结果，按表顺序排列。</para></param>
public sealed record MtkEfuseReading(
    ushort HardwareCode,
    uint EfuseBase,
    IReadOnlyList<MtkEfuseCellReading> Cells)
{
    /// <summary>Gets the count of cells that were read from the target rather than reported.
    /// <para>获取实际从目标读取（而非直接上报）的单元数量。</para></summary>
    /// <value>
    /// The number of entries whose bytes came from a device read.
    /// <para>字节来自设备读取的条目数量。</para>
    /// </value>
    public int ReadCellCount => Cells.Count(cell => !cell.Cell.IsReportedWithoutRead);
}