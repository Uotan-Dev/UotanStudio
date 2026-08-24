namespace UotanStudio.Firmware.Compress.Internal;

/// <summary>
/// 并行度解析辅助：统一把 <see cref="CompressionOptions.MaxDegreeOfParallelism"/>
/// 解析为实际使用的并行度。
/// <para>Parallelism helper: resolves <see cref="CompressionOptions.MaxDegreeOfParallelism"/>
/// into the effective degree of parallelism.</para>
/// </summary>
internal static class Parallelism
{
    /// <summary>
    /// 解析并行度：null 或 ≤1 返回 1（串行）；否则取 requested 与 workItems 的较小值。
    /// <para>Resolves the degree: null or ≤1 returns 1 (sequential); otherwise
    /// min(requested, workItems).</para>
    /// </summary>
    public static int Resolve(int? requested, int workItems)
    {
        if (requested is null or <= 1)
        {
            return 1;
        }

        var dop = requested.Value;
        return dop > workItems ? workItems : dop;
    }
}