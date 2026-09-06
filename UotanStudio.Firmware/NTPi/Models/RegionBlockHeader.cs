namespace UotanStudio.Firmware.NTPi.Models;

public struct RegionBlockHeader
{
    public RegionHeader ThisHeader;

    public RegionHeader NextHeader;

    public ulong RealSize;
}
