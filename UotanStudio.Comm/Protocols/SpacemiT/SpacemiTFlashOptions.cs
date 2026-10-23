namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Options for a SpacemiT flash run. Writing any byte to device storage is a destructive
/// operation, so every run must carry the exact confirmation phrase before execution.
/// <para>一次 SpacemiT 烧录运行的选项。向设备存储写入任何字节都是破坏性操作，
/// 因此每次运行都必须先携带精确的确认短语。</para>
/// </summary>
public sealed class SpacemiTFlashOptions
{
    /// <summary>
    /// Confirmation phrase every flash run must present in <see cref="FlashConfirmation"/>;
    /// execution fails before any wire I/O otherwise.
    /// <para>每次烧录运行必须在 <see cref="FlashConfirmation"/> 中提供的确认短语；
    /// 否则在任何线上 I/O 之前即失败。</para>
    /// </summary>
    public const string RequiredConfirmation = "FLASH";

    /// <summary>Gets or sets the confirmation phrase; must equal <see cref="RequiredConfirmation"/>. <para>获取或设置确认短语；必须等于 <see cref="RequiredConfirmation"/>。</para></summary>
    public string FlashConfirmation { get; set; } = string.Empty;

    /// <summary>Gets or sets the partition names to flash exclusively; <see langword="null"/> or empty flashes every partition the tables reference. <para>获取或设置独占刷写的分区名集合；为 <see langword="null"/> 或空时刷写分区表引用的全部分区。</para></summary>
    public IReadOnlyList<string>? OnlyPartitions { get; set; }

    /// <summary>Gets or sets partition names never flashed, applied after <see cref="OnlyPartitions"/>. <para>获取或设置永不刷写的分区名集合，在 <see cref="OnlyPartitions"/> 之后生效。</para></summary>
    public IReadOnlyList<string> SkipPartitions { get; set; } = [];

    /// <summary>Gets or sets the gzip level used for chunked oversized images, matching the package "gzip-N" hint default of 5. <para>获取或设置超大镜像分块传输使用的 gzip 级别；与包内 "gzip-N" 提示的默认值 5 一致。</para></summary>
    public int GzipLevel { get; set; } = 5;

    /// <summary>Gets or sets the settle delay in milliseconds after a <c>continue</c> action before the re-open handler is invoked. <para>获取或设置 <c>continue</c> 动作之后、调用重开回调前的等待毫秒数。</para></summary>
    public int ReEnumerateSettleMs { get; set; } = SpacemiTConstants.ReEnumerateSettleMs;

    /// <summary>Gets or sets whether a plain <c>reboot</c> is issued after the script completes. <para>获取或设置脚本执行完成后是否发送普通 <c>reboot</c>。</para></summary>
    public bool RebootAfterFlash { get; set; }

    /// <summary>
    /// Validates the options before any device I/O; a rejected run never touches the wire.
    /// <para>在任何设备 I/O 之前校验选项；被拒绝的运行绝不触线。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The confirmation phrase is missing or wrong, or the option values are out of range. <para>确认短语缺失或错误，或选项值越界。</para></exception>
    internal void Validate()
    {
        if (!string.Equals(FlashConfirmation, RequiredConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SpacemiT flash requires FlashConfirmation to be exactly '{RequiredConfirmation}'.");
        }

        if (GzipLevel is < 1 or > 9)
        {
            throw new InvalidOperationException($"GzipLevel {GzipLevel} is outside 1..9.");
        }

        if (ReEnumerateSettleMs is < 0 or > 120_000)
        {
            throw new InvalidOperationException($"ReEnumerateSettleMs {ReEnumerateSettleMs} is outside 0..120000.");
        }
    }
}