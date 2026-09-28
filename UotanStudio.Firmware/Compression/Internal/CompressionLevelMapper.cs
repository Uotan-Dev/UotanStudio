using System.IO.Compression;

namespace UotanStudio.Firmware.Compression.Internal;

internal static class CompressionLevelMapper
{
    /// <summary>
    /// Maps to the built-in CompressionLevel (gzip / deflate / brotli).
    /// <para>映射到 .NET 内置 CompressionLevel（gzip / deflate / brotli）。</para>
    /// </summary>
    public static CompressionLevel ToBclCompressionLevel(int? level, CompressionLevel @default = CompressionLevel.Optimal)
    {
        return !level.HasValue
            ? @default
            : level.Value switch
            {
                <= 0 => CompressionLevel.NoCompression,
                <= 3 => CompressionLevel.Fastest,
                >= 9 => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal
            };
    }

    /// <summary>
    /// Maps the unified integer level (0-9) to a Brotli quality value (0-11).
    /// <para>映射到 BrotliSharpLib 的质量值（0-11）。</para>
    /// </summary>
    public static int ToBrotliQuality(int? level)
    {
        if (!level.HasValue)
        {
            return 4;
        }

        return level.Value switch
        {
            <= 0 => 0,
            <= 3 => 1,
            >= 9 => 11,
            _ => 5
        };
    }

    /// <summary>
    /// Maps to SharpCompress's SharpCompress.Compressors.Deflate.CompressionLevel (zlib).
    /// <para>映射到 SharpCompress 的 SharpCompress.Compressors.Deflate.CompressionLevel（zlib）。</para>
    /// </summary>
    public static SharpCompress.Compressors.Deflate.CompressionLevel ToSharpCompressDeflate(int? level)
    {
        return level switch
        {
            null => SharpCompress.Compressors.Deflate.CompressionLevel.Default,
            <= 0 => SharpCompress.Compressors.Deflate.CompressionLevel.None,
            <= 3 => SharpCompress.Compressors.Deflate.CompressionLevel.BestSpeed,
            >= 9 => SharpCompress.Compressors.Deflate.CompressionLevel.BestCompression,
            _ => SharpCompress.Compressors.Deflate.CompressionLevel.Default
        };
    }
}