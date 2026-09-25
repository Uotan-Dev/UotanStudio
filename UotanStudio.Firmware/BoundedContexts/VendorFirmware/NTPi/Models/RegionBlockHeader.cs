namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

/// <summary>
/// Fixed 40-byte little-endian header stored at the start of each decrypted region, before the region payload.
/// <para>存储在每个解密区域开头、位于区域载荷之前的固定 40 字节小端头部。</para>
/// </summary>
public struct RegionBlockHeader
{
    /// <summary>Header describing this region's own encrypted block (type and padded size on disk).
    /// <para>描述本区域自身加密块的头部（类型与磁盘上的填充大小）。</para></summary>
    public RegionHeader ThisHeader;

    /// <summary>Header of the next region in the chain; a zero <see cref="RegionHeader.RegionSize"/> ends the chain unless the type is Region6.
    /// <para>链中下一个区域的头部；<see cref="RegionHeader.RegionSize"/> 为 0 表示链结束，类型为 Region6 时除外。</para></summary>
    public RegionHeader NextHeader;

    /// <summary>Exact byte length of the payload that follows this header once decrypted; reads require at least 40 + RealSize bytes.
    /// <para>解密后紧随本头部的载荷的精确字节长度；读取要求至少 40 + RealSize 字节。</para></summary>
    public ulong RealSize;
}