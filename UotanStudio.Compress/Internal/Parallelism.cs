namespace UotanStudio.Compress.Internal;

internal static class Parallelism
{
    /// <summary>Resolves the degree: null or ≤1 returns 1 (sequential); otherwise min(requested, workItems, physical cores).
    /// <para>解析并行度：null 或 ≤1 返回 1（串行）；否则取 requested、workItems 与物理核心数三者的最小值。</para></summary>
    /// <param name="requested">Caller request; <see langword="null"/> and any value of 1 or below mean sequential work.
    /// <para>调用方请求；<see langword="null"/> 与不大于 1 的值均表示串行执行。</para></param>
    /// <param name="workItems">Number of independent work items available; it is returned as the ceiling for <paramref name="requested"/> and is not clamped, so a zero or negative count is returned unchanged.
    /// <para>可用独立工作项数量；作为 <paramref name="requested"/> 的上限返回且不做钳制，因此为 0 或负数时原样返回。</para></param>
    /// <returns>1 for sequential work, otherwise the smaller of <paramref name="requested"/>, <paramref name="workItems"/> and the detected physical core count.
    /// <para>串行执行时返回 1，否则返回 <paramref name="requested"/>、<paramref name="workItems"/> 与探测到的物理核心数 中的最小值。</para></returns>
    public static int Resolve(int? requested, int workItems)
    {
        if (requested is null or <= 1)
        {
            return 1;
        }

        var dop = requested.Value;
        if (dop > workItems)
        {
            dop = workItems;
        }

        var cores = PhysicalCoreCount.Value;
        return dop > cores ? cores : dop;
    }
}