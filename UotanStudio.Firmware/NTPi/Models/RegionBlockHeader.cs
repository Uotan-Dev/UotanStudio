namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 固件包中区域块头部的结构体，包含当前区域头部、下一个区域头部和实际数据大小。</para>
/// Represents the block header of a region within an NTPI firmware package, containing the current region header, the next region header, and the actual data size.
/// </summary>
/// <remarks>
/// <para>该结构体使用顺序内存布局，大小为 40 字节。序列化由 StructHelper 以手工 Span 方式完成。</para>
/// This struct uses sequential memory layout and is 40 bytes in size. Serialization is done manually via StructHelper with spans.
/// </remarks>
public struct RegionBlockHeader
{
    /// <summary>
    /// <para>当前区域的头部信息。</para>
    /// The header information of the current region.
    /// </summary>
    public RegionHeader ThisHeader;

    /// <summary>
    /// <para>下一个区域的头部信息。</para>
    /// The header information of the next region.
    /// </summary>
    public RegionHeader NextHeader;

    /// <summary>
    /// <para>区域中实际数据的大小（以字节为单位），不包括头部。</para>
    /// The actual data size in the region in bytes, excluding headers.
    /// </summary>
    public ulong RealSize;
}