namespace UotanStudio.Firmware.Compress;

public sealed class CompressionOptions
{
    public int? Level { get; set; }

    public uint? DictionarySize { get; set; }

    public int? MaxDegreeOfParallelism { get; set; }
}
