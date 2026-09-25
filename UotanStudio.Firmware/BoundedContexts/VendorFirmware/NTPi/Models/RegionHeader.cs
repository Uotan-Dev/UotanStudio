namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

/// <summary>
/// Fixed 16-byte little-endian header describing one encrypted region in the NTPI chain.
/// <para>描述 NTPI 链中一个加密区域的固定 16 字节小端头部。</para>
/// </summary>
public struct RegionHeader
{
    /// <summary>Raw region type value; cast it to <see cref="T:UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models.RegionType"/>.
    /// <para>原始区域类型值；可转换为 <see cref="T:UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models.RegionType"/>。</para></summary>
    public ulong RegionType;

    /// <summary>Encrypted size in bytes of the region on disk, AES padding included; 0 ends the chain unless the type is Region6.
    /// <para>磁盘上该区域加密后的字节大小，含 AES 填充；为 0 表示链结束，类型为 Region6 时除外。</para></summary>
    public ulong RegionSize;
}