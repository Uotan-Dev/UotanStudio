namespace UotanStudio.Firmware.Dat;

public sealed class DatWriteOptions
{
    public bool CompressWithBrotli { get; set; }

    public int MaxCommandRangeBlocks { get; set; }
}
