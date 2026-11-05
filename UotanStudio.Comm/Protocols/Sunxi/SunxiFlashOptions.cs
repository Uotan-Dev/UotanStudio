namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Erase and partition scope of one Sunxi flash run.
/// <para>一次全志烧录运行的擦除与分区范围。</para>
/// </summary>
public enum SunxiFlashMode
{
    /// <summary>Write the package without erasing and keep every partition. <para>不擦除写入整包，保留所有分区。</para></summary>
    Partition,

    /// <summary>Erase nothing and skip the user data partitions the vendor list marks as user storage.
    /// <para>不擦除，并跳过厂商清单标记为用户存储的数据分区。</para></summary>
    KeepData,

    /// <summary>Erase the selected partitions before writing. <para>写入前擦除所选分区。</para></summary>
    PartitionErase,

    /// <summary>Erase the whole storage before writing. <para>写入前擦除整片存储。</para></summary>
    FullErase
}

/// <summary>
/// Options for a Sunxi flash run. Writing any byte to device storage is a destructive operation, so every run
/// must carry the exact confirmation phrase before execution.
/// <para>一次全志烧录运行的选项。向设备存储写入任何字节都是破坏性操作，因此每次运行都必须先携带精确的确认短语。</para>
/// </summary>
public sealed class SunxiFlashOptions
{
    /// <summary>
    /// Confirmation phrase every flash run must present in <see cref="FlashConfirmation"/>; execution fails
    /// before any wire I/O otherwise.
    /// <para>每次烧录运行必须在 <see cref="FlashConfirmation"/> 中提供的确认短语；否则在任何线上 I/O 之前即失败。</para>
    /// </summary>
    public const string RequiredConfirmation = "FLASH";

    /// <summary>Gets or sets the confirmation phrase; must equal <see cref="RequiredConfirmation"/>. <para>获取或设置确认短语；必须等于 <see cref="RequiredConfirmation"/>。</para></summary>
    public string FlashConfirmation { get; set; } = string.Empty;

    /// <summary>Gets or sets the erase and partition scope of the run. <para>获取或设置本次运行的擦除与分区范围。</para></summary>
    public SunxiFlashMode Mode { get; set; } = SunxiFlashMode.FullErase;

    /// <summary>Gets or sets the partition names a <see cref="SunxiFlashMode.Partition"/> run writes; an empty
    /// list writes every partition. <para>获取或设置 <see cref="SunxiFlashMode.Partition"/> 运行写入的分区名；
    /// 空列表表示写入全部分区。</para></summary>
    public IReadOnlyList<string> Partitions { get; set; } = [];

    /// <summary>Gets or sets whether the run verifies each download against the service-reported CRC. The
    /// verification fails a partition or table write only when the device reports a valid flag with a mismatch;
    /// boot images and the erase flag are reported as warnings because not every medium answers them.
    /// <para>获取或设置是否用服务上报的 CRC 校验每次下载。只有设备给出有效标志且两侧不一致时才判定分区或表写入
    /// 失败；引导镜像与擦除标志按警告上报，因为并非所有介质都会应答。</para></summary>
    public bool VerifyAfter { get; set; } = true;

    /// <summary>Gets or sets the mode the service switches to after the last transfer; it is sent as the
    /// follow-up mode of the tool-mode command while the tool itself stays in the normal mode.
    /// <para>获取或设置最后一次传输之后服务切换到的模式；它作为工具模式命令的后续模式发送，工具本身保持正常模式。</para></summary>
    public SunxiToolMode CompletionMode { get; set; } = SunxiToolMode.Reboot;

    /// <summary>Erase flag implied by <see cref="Mode"/>. <para>由 <see cref="Mode"/> 隐含的擦除标志。</para></summary>
    internal SunxiEraseFlag EraseFlag => Mode switch
    {
        SunxiFlashMode.FullErase => SunxiEraseFlag.Full,
        SunxiFlashMode.PartitionErase => SunxiEraseFlag.Partition,
        _ => SunxiEraseFlag.None
    };

    /// <summary>
    /// Validates the options before any device I/O; a rejected run never touches the wire.
    /// <para>在任何设备 I/O 之前校验选项；被拒绝的运行绝不触线。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The confirmation phrase is missing or wrong, the mode is not
    /// defined, or the completion mode is not a defined tool mode. <para>确认短语缺失或错误、模式未定义，或完成
    /// 模式不是已定义的工具模式。</para></exception>
    internal void Validate()
    {
        if (!string.Equals(FlashConfirmation, RequiredConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Sunxi flash requires FlashConfirmation to be exactly '{RequiredConfirmation}'.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new InvalidOperationException($"Mode {Mode} is not a defined flash mode.");
        }

        if (!Enum.IsDefined(CompletionMode))
        {
            throw new InvalidOperationException($"CompletionMode {CompletionMode} is not a defined tool mode.");
        }
    }
}