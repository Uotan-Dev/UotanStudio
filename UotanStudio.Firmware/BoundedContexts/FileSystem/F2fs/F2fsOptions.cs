namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

public sealed class F2fsOptions
{
    public int BlockSize { get; init; } = 4096;

    public long SegmentSize { get; init; } = 2L * 1024 * 1024;

    public int SegmentCount { get; init; }

    public string VolumeName { get; init; } = "f2fs";

    public bool EnableSparse { get; init; } = true;

    public Guid? Uuid { get; init; }

    public bool EnableCompression { get; init; }

    public CompressionAlgorithm CompressionAlgorithm { get; init; } = CompressionAlgorithm.Lz4;

    public int CompressionLevel { get; init; }

    /// <summary>
    /// Validates and normalizes the options.
    /// <para>校验并规整选项。</para>
    /// </summary>
    internal void Validate()
    {
        if (BlockSize != 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSize), "F2FS block size must be 4096 bytes.");
        }

        if (SegmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentSize), "Segment size must be positive.");
        }

        if (SegmentSize / BlockSize != 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentSize),
                "F2FS segment size must yield exactly 512 blocks (2 MB for 4096-byte blocks).");
        }

        if (SegmentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentCount), "Segment count must not be negative.");
        }

        if (CompressionLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompressionLevel), "Compression level must not be negative.");
        }
    }
}
