namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 固件包中区域头部的结构体，包含区域类型和区域大小。</para>
/// Represents the header of a region within an NTPI firmware package, containing the region type and size.
/// </summary>
/// <remarks>
/// <para>该结构体使用顺序内存布局，大小为 16 字节。序列化由 StructHelper 以手工 Span 方式完成。</para>
/// This struct uses sequential memory layout and is 16 bytes in size. Serialization is done manually via StructHelper with spans.
/// </remarks>
public struct RegionHeader
{
    /// <summary>
    /// <para>区域类型标识符，对应 <see cref="RegionType"/> 枚举值。</para>
    /// The region type identifier, corresponding to <see cref="RegionType"/> enum values.
    /// </summary>
    public ulong RegionType;

    /// <summary>
    /// <para>区域数据的大小（以字节为单位）。</para>
    /// The size of the region data in bytes.
    /// </summary>
    public ulong RegionSize;
}