namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// What happens to each partition's existing contents before it is written.
/// <para>写入前对每个分区已有内容的处理方式。</para>
/// </summary>
public enum UnisocFlashMode
{
    /// <summary>Writes the selected partitions without erasing them first.
    /// <para>直接写入所选分区，不先擦除。</para></summary>
    KeepData = 0,

    /// <summary>Erases each selected partition right before writing it.
    /// <para>写每个所选分区前先擦除该分区。</para></summary>
    PartitionErase = 1,

    /// <summary>Erases every partition the device reports before any write; requires the wipe confirmation.
    /// <para>在任何写入前擦除设备报告的每个分区；需要擦除确认。</para></summary>
    FullErase = 2,
}

/// <summary>
/// What the device does after the flash pass ends. <para>刷写流程结束后设备的去向。</para>
/// </summary>
public enum UnisocCompletionMode
{
    /// <summary>Leaves the device in download mode. <para>设备保持在下载模式。</para></summary>
    Stay = 0,

    /// <summary>Resets the device into normal mode. <para>把设备重启进正常模式。</para></summary>
    Reboot = 1,

    /// <summary>Powers the device off. <para>让设备关机。</para></summary>
    PowerOff = 2,
}

/// <summary>
/// Options of one flash pass. Destructive values require the exact confirmation phrases; validation happens before
/// any device I/O, so an unconfirmed request never writes a byte.
/// <para>一次刷写流程的选项。破坏性取值需要精确的确认短语；校验发生在任何设备 I/O 之前，未确认的请求不会写一个
/// 字节。</para>
/// </summary>
public sealed record UnisocFlashOptions
{
    /// <summary>The exact phrase a flash request must carry. <para>刷写请求必须携带的精确短语。</para></summary>
    public const string ConfirmationPhrase = "FLASH";

    /// <summary>The exact phrase a full-storage erase must additionally carry.
    /// <para>整存储擦除额外必须携带的精确短语。</para></summary>
    public const string WipeConfirmationPhrase = "WIPE";

    /// <summary>The exact phrase a partition-table rebuild must carry; this operation destroys the table and all
    /// partition boundaries.
    /// <para>重建分区表必须携带的精确短语；该操作会销毁分区表和所有分区边界。</para></summary>
    public const string RepartitionConfirmationPhrase = "REPARTITION";

    /// <summary>Gets the erase mode. <para>获取擦除模式。</para></summary>
    public UnisocFlashMode Mode { get; init; } = UnisocFlashMode.PartitionErase;

    /// <summary>Gets the completion mode. <para>获取收尾模式。</para></summary>
    public UnisocCompletionMode Completion { get; init; } = UnisocCompletionMode.Reboot;

    /// <summary>
    /// Gets the flash confirmation; it must equal <see cref="ConfirmationPhrase"/> or every write is refused.
    /// <para>刷写确认；必须等于 <see cref="ConfirmationPhrase"/>，否则一切写入都被拒绝。</para></summary>
    public string? FlashConfirmation { get; init; }

    /// <summary>
    /// Gets the wipe confirmation required by <see cref="UnisocFlashMode.FullErase"/>; it must equal
    /// <see cref="WipeConfirmationPhrase"/>.
    /// <para><see cref="UnisocFlashMode.FullErase"/> 要求的擦除确认；必须等于 <see cref="WipeConfirmationPhrase"/>。</para></summary>
    public string? WipeConfirmation { get; init; }

    /// <summary>Gets the separate confirmation for rebuilding the partition table. It destroys every partition
    /// boundary and is refused unless it equals <see cref="RepartitionConfirmationPhrase"/>.
    /// <para>重建分区表的单独确认；该操作销毁所有分区边界，必须等于 <see cref="RepartitionConfirmationPhrase"/> 才会执行。</para></summary>
    public string? RepartitionConfirmation { get; init; }

    /// <summary>Gets whether the plan's ExtTable should be applied. <para>获取是否应用计划的 ExtTable。</para></summary>
    public bool ApplyRepartition { get; init; }

    /// <summary>
    /// Validates the options and returns the confirmed flash phrase for the destructive calls.
    /// <para>校验选项并返回已确认的刷写短语，供破坏性调用使用。</para>
    /// </summary>
    /// <returns>The confirmed flash confirmation phrase. <para>已确认的刷写确认短语。</para></returns>
    /// <exception cref="InvalidOperationException">The flash confirmation is missing or wrong, or the full-erase
    /// mode lacks the wipe confirmation. <para>刷写确认缺失或错误，或全擦模式缺少擦除确认。</para></exception>
    public string Validate()
    {
        var confirmation = FlashConfirmation;
        if (confirmation is null || !string.Equals(confirmation, ConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The flash request is not confirmed with the exact phrase.");
        }

        if (Mode == UnisocFlashMode.FullErase &&
            !string.Equals(WipeConfirmation, WipeConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The full erase request is not confirmed with the exact phrase.");
        }

        return confirmation;
    }
}