namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Derives bounded operation deadlines from the byte count an erase has to cover, so a whole-device
/// erase is not judged with the same budget as a single small partition.
/// <para>按擦除需覆盖的字节数推导有界操作期限，使整片擦除不会与单个小分区共用同一预算。</para>
/// </summary>
internal static class MtkTimeoutPolicy
{
    /// <summary>The floor every erase budget is raised to, in milliseconds.
    /// <para>所有擦除预算被抬升到的下限，单位毫秒。</para></summary>
    internal const int MinimumEraseTimeoutMs = 120_000;

    /// <summary>The ceiling every erase budget is clamped to, in milliseconds.
    /// <para>所有擦除预算被限制到的上限，单位毫秒。</para></summary>
    internal const int MaximumEraseTimeoutMs = 86_400_000;

    /// <summary>
    /// The throughput floor used to derive a budget, in bytes per second.
    /// <para>用于推导预算的吞吐下限，单位字节每秒。</para>
    /// </summary>
    /// <remarks>
    /// 512 KiB/s is deliberately conservative: slow eMMC parts and NAND block erases with retirement
    /// can fall well below the peak figure, and a budget that expires while the device is still erasing
    /// is worse than one that waits too long.
    /// <para>512 KiB/s 是刻意保守的取值：较慢的 eMMC 与带退役处理的 NAND 块擦除会明显低于峰值，
    /// 而"设备仍在擦除时预算就到期"比"等得过久"更糟。</para>
    /// </remarks>
    private const long EraseThroughputFloorBytesPerSecond = 512 * 1024;

    /// <summary>
    /// Computes the deadline for erasing <paramref name="bytes"/> bytes, never below
    /// <paramref name="floorMs"/> and never above the 24-hour ceiling.
    /// <para>计算擦除 <paramref name="bytes"/> 字节的期限，不低于 <paramref name="floorMs"/>，也不超过 24 小时上限。</para>
    /// </summary>
    /// <param name="bytes">The erase length in bytes; negative values are rejected.
    /// <para>擦除长度（字节）；负值会被拒绝。</para></param>
    /// <param name="floorMs">The caller-supplied minimum budget in milliseconds.
    /// <para>调用方提供的最小预算，单位毫秒。</para></param>
    /// <returns>The effective deadline in milliseconds.
    /// <para>生效的期限，单位毫秒。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative, or
    /// <paramref name="floorMs"/> is not positive. <para><paramref name="bytes"/> 为负，或
    /// <paramref name="floorMs"/> 不为正数。</para></exception>
    internal static int ComputeEraseTimeout(long bytes, int floorMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(floorMs);

        var scaled = MinimumEraseTimeoutMs + ((bytes / EraseThroughputFloorBytesPerSecond) * 1000);
        var effective = Math.Max(scaled, floorMs);
        return (int)Math.Min(effective, MaximumEraseTimeoutMs);
    }
}