namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// Per-codec compression level bounds. The level is a codec property rather than an image-format property,
/// so one shared range cannot describe every algorithm: LZ4HC carries 1..9 on the EROFS path, MicroLZMA
/// reaches 109 with 100..109 selecting its extreme preset, and Zstandard reaches 22. A level above a codec's
/// ceiling is capped at that ceiling, because the on-disk format records no level and an over-range request
/// still means "as much compression as this codec offers"; capping is therefore not a lossy downgrade but the
/// codec's own contract. A level below a codec's floor is an error, because there is no meaningful smaller
/// setting to fall back to.
/// <para>逐编解码器的压缩等级边界。等级属于编解码器而非
/// 镜像格式属性，因此单一区间无法描述所有算法：EROFS 路径上的 LZ4HC 取 1..9，MicroLZMA 可达 109（100..109
/// 选择其 extreme 预设），Zstandard 可达 22。超过编解码器上限的等级会被截到该上限，因为磁盘格式不记录等级、
/// 且超范围请求的含义仍是「该编解码器能给出的最大压缩」；因此截断不是有损降级，而是编解码器自身的契约。
/// 低于编解码器下限的等级属于错误，因为不存在有意义的更小设置可回退。</para>
/// </summary>
internal static class ErofsCompressionLevel
{
    /// <summary>Highest LZ4HC level the EROFS path applies, the strongest hash-chain level
    /// (<c>LZ4HC_CLEVEL_DEFAULT</c>). liblz4's levels 10-12 select the optimal parser, whose
    /// destination-filling mode is not monotonic on the small spans EROFS encodes: it is far slower than
    /// level 9 and can emit a larger frame, which would shrink the accepted pcluster span and grow the image,
    /// so the format path clamps them to this level.
    /// <para>EROFS 路径应用的最高 LZ4HC 等级，即最强的哈希链等级（<c>LZ4HC_CLEVEL_DEFAULT</c>）。liblz4 的
    /// 10-12 级选择最优解析器，其填满目标模式在 EROFS 编码的小跨度上并不单调：不仅远慢于 9 级，还可能输出更大的帧，
    /// 从而缩短被接受的 pcluster 跨度并让镜像变大，因此格式路径把它们截断到该等级。</para></summary>
    internal const int Lz4HighCompressionBest = 9;

    /// <summary>Highest MicroLZMA level; 100..109 select the extreme preset.<para>MicroLZMA 的最高等级；100..109 选择 extreme 预设。</para></summary>
    internal const int LzmaBest = 109;

    /// <summary>Lowest MicroLZMA level, which selects the library default preset.
    /// <para>MicroLZMA 的最低等级，选择库默认预设。</para></summary>
    internal const int LzmaLowest = -1;

    /// <summary>Highest DEFLATE level.<para>DEFLATE 的最高等级。</para></summary>
    internal const int DeflateBest = 9;

    /// <summary>Highest Zstandard level.<para>Zstandard 的最高等级。</para></summary>
    internal const int ZstdBest = 22;

    /// <summary>
    /// Validates a requested level against the selected codec and returns the level the EROFS encoder applies,
    /// capping an over-range request exactly as the codec's own contract does. LZ4 accepts the level through
    /// the fast path's own mapping, but an LZ4HC request above <see cref="Lz4HighCompressionBest"/> returns
    /// that ceiling.
    /// <para>按所选编解码器校验请求的等级并返回 EROFS 编码器实际应用的等级，超范围请求按编解码器自身的契约截断。
    /// LZ4 的等级交由快速路径自身的映射处理，但超过 <see cref="Lz4HighCompressionBest"/> 的 LZ4HC 请求返回该上限。</para>
    /// </summary>
    /// <param name="algorithm">Codec whose range applies; an undefined value raises rather than guessing.
    /// <para>适用范围的编解码器；未定义值直接抛错而不是猜测。</para></param>
    /// <param name="level">Requested level.<para>请求的等级。</para></param>
    /// <returns>The level the EROFS encoder applies.<para>EROFS 编码器实际应用的等级。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside the codec's range,
    /// or <paramref name="algorithm"/> is not a defined codec.
    /// <para><paramref name="level"/> 超出编解码器范围，或 <paramref name="algorithm"/> 不是已定义的编解码器。</para></exception>
    internal static int Validate(ErofsCompressionAlgorithm algorithm, int level)
    {
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm.");
        }

        if (algorithm == ErofsCompressionAlgorithm.Lz4)
        {
            return Math.Min(level, Lz4HighCompressionBest);
        }

        var lowest = algorithm == ErofsCompressionAlgorithm.Zstd ? int.MinValue
            : algorithm == ErofsCompressionAlgorithm.Lzma ? LzmaLowest
            : 0;
        var best = algorithm switch
        {
            ErofsCompressionAlgorithm.Lzma => LzmaBest,
            ErofsCompressionAlgorithm.Deflate => DeflateBest,
            ErofsCompressionAlgorithm.Zstd => ZstdBest,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown EROFS compression algorithm."),
        };
        if (level < lowest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"EROFS {algorithm} compression level must be at least {lowest}.");
        }

        return Math.Min(level, best);
    }

    /// <summary>
    /// Reports the highest level a codec accepts, used by the settings-facing surfaces that need to bound a
    /// user-supplied value without knowing the codec internals.
    /// <para>返回编解码器接受的最高等级，供需要在不了解编解码器内部实现的前提下约束用户输入的界面使用。</para>
    /// </summary>
    /// <param name="algorithm">Codec to describe.<para>待描述的编解码器。</para></param>
    /// <param name="highCompression">Whether the LZ4 payload uses the HC encoder, which is what carries the
    /// level for the LZ4 family.<para>LZ4 数据是否使用 HC 编码器，LZ4 族的等级正是由它承载。</para></param>
    /// <returns>The codec's highest accepted level.<para>该编解码器接受的最高等级。</para></returns>
    internal static int Best(ErofsCompressionAlgorithm algorithm, bool highCompression) => algorithm switch
    {
        ErofsCompressionAlgorithm.Lz4 => highCompression ? Lz4HighCompressionBest : 0,
        ErofsCompressionAlgorithm.Lzma => LzmaBest,
        ErofsCompressionAlgorithm.Deflate => DeflateBest,
        ErofsCompressionAlgorithm.Zstd => ZstdBest,
        _ => 0,
    };
}