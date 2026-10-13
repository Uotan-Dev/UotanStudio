using System.IO.Compression;

namespace UotanStudio.Firmware.Compression.Internal;

internal static class CompressionLevelMapper
{
    /// <summary>
    /// Maps to the built-in CompressionLevel (gzip / deflate / brotli).
    /// <para>映射到 .NET 内置 CompressionLevel（gzip / deflate / brotli）。</para>
    /// </summary>
    /// <param name="level">Unified level on the 0-9 scale, or <see langword="null"/> to keep
    /// <paramref name="default"/>; values at or below 0 select no compression, 1 through 3 the fastest
    /// setting, 9 and above the smallest-size setting, and 4 through 8 the optimal setting.
    /// <para>统一的 0-9 等级，为 <see langword="null"/> 时沿用 <paramref name="default"/>；不超过 0 选择不压缩，
    /// 1 至 3 选最快，9 及以上选最小体积，4 至 8 选最优。</para></param>
    /// <param name="default">Result returned when <paramref name="level"/> is <see langword="null"/>;
    /// defaults to <see cref="System.IO.Compression.CompressionLevel.Optimal"/>.
    /// <para>当 <paramref name="level"/> 为 <see langword="null"/> 时的返回值；默认为
    /// <see cref="System.IO.Compression.CompressionLevel.Optimal"/>。</para></param>
    /// <returns>The equivalent built-in compression level.
    /// <para>等价的内置压缩等级。</para></returns>
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
    /// <para>把统一整数等级（0-9）映射为 Brotli 质量值（0-11）。</para>
    /// </summary>
    /// <param name="level">Unified level on the 0-9 scale, or <see langword="null"/> for quality 4; values
    /// at or below 0 map to 0, 1 through 3 to 1, 9 and above to 11, and 4 through 8 to 5.
    /// <para>统一的 0-9 等级，为 <see langword="null"/> 时取质量 4；不超过 0 映射为 0，1 至 3 映射为 1，
    /// 9 及以上映射为 11，4 至 8 映射为 5。</para></param>
    /// <returns>The Brotli quality in the 0-11 range.
    /// <para>0-11 范围内的 Brotli 质量值。</para></returns>
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
    /// <param name="level">Unified level on the 0-9 scale, or <see langword="null"/> for the zlib default;
    /// values at or below 0 map to no compression, 1 through 3 to the fastest setting, 9 and above to the
    /// best compression, and 4 through 8 to the zlib default.
    /// <para>统一的 0-9 等级，为 <see langword="null"/> 时取 zlib 默认值；不超过 0 映射为不压缩，1 至 3 映射为
    /// 最快，9 及以上映射为最佳压缩，4 至 8 映射为 zlib 默认值。</para></param>
    /// <returns>The equivalent SharpCompress deflate level.
    /// <para>等价的 SharpCompress deflate 压缩等级。</para></returns>
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