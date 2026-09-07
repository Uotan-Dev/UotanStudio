namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;

public sealed class DatWriteOptions
{
    public bool CompressWithBrotli { get; set; }

    public int MaxCommandRangeBlocks { get; set; }
}
