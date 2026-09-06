namespace UotanStudio.Firmware.Erofs;

public sealed class ErofsOptions
{
    public int BlockSize { get; set; } = 4096;

    public bool Compress { get; set; } = true;

    public ErofsCompressionAlgorithm Algorithm { get; set; } = ErofsCompressionAlgorithm.Lz4;

    public int CompressionLevel { get; set; } = 1;

    public bool UseCompactedIndexes { get; set; } = true;

    public string VolumeName { get; set; } = "erofs";

    public IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>>? FileXattrs { get; set; }

    public bool TailPacking { get; set; } = true;

    public bool Fragments { get; set; }

    public bool Dedup { get; set; }

    public bool Metabox { get; set; }

    public int ExtraDeviceCount { get; set; }

    /// <summary>
    /// Validates and normalizes the options.
    /// <para>校验并规整选项。</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize < 512 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize),
                "BlockSize must be a power of two of at least 512 bytes.");
        }

        CompressionLevel = Math.Clamp(CompressionLevel, 1, 9);
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm,
                "Unknown EROFS compression algorithm.");
        }

        if (string.IsNullOrEmpty(VolumeName))
        {
            VolumeName = "erofs";
        }

        if (ExtraDeviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtraDeviceCount), ExtraDeviceCount,
                "ExtraDeviceCount must be non-negative.");
        }


        if (Fragments)
        {
            TailPacking = false;
        }


        if (Metabox)
        {
            TailPacking = false;
        }
    }
}
