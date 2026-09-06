namespace UotanStudio.Firmware.Compress.Internal;

internal static class Parallelism
{
    /// <summary>
    /// Resolves the degree: null or ≤1 returns 1 (sequential); otherwise min(requested, workItems).
    /// <para>解析并行度：null 或 ≤1 返回 1（串行）；否则取 requested 与 workItems 的较小值。</para>
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
