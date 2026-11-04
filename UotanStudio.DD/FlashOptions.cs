using UotanStudio.DD.Drives;

namespace UotanStudio.DD;

/// <summary>
/// Safety and throughput switches applied to one flash operation.
/// <para>单次烧录操作使用的安全开关与吞吐参数。</para>
/// </summary>
public sealed record FlashOptions
{
    /// <summary>
    /// Minimum accepted write buffer size.
    /// <para>允许的最小写入缓冲区大小。</para>
    /// </summary>
    public const int MinimumBufferBytes = 64 * 1024;

    /// <summary>
    /// Maximum accepted write buffer size.
    /// <para>允许的最大写入缓冲区大小。</para>
    /// </summary>
    public const int MaximumBufferBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Gets whether the caller explicitly accepted that every selected target will be overwritten.
    /// <para>获取调用方是否已明确接受所有选中目标都将被覆盖。</para>
    /// </summary>
    /// <value>
    /// Defaults to <see langword="false"/>; <see cref="ImageFlasher"/> refuses to write until this is set.
    /// <para>默认 <see langword="false"/>；在设置之前 <see cref="ImageFlasher"/> 拒绝写入。</para>
    /// </value>
    public bool ConfirmOverwrite { get; init; }

    /// <summary>
    /// Gets whether the written bytes are read back and compared with the source image.
    /// <para>获取写入字节是否回读并与源镜像比较。</para>
    /// </summary>
    /// <value>Defaults to <see langword="true"/> so success is reported only after read-back comparison.
    /// <para>默认 <see langword="true"/>，仅在回读比较通过后报告成功。</para></value>
    public bool VerifyAfterWrite { get; init; } = true;

    /// <summary>
    /// Gets whether targets larger than <see cref="DriveCompatibilityEvaluator.LargeDriveThresholdBytes"/> are allowed.
    /// <para>获取是否允许大于 <see cref="DriveCompatibilityEvaluator.LargeDriveThresholdBytes"/> 的目标。</para>
    /// </summary>
    /// <value>Defaults to <see langword="false"/>; the large-drive warning becomes a rejection until set.
    /// <para>默认 <see langword="false"/>；未设置时大容量驱动警告会升级为拒绝。</para></value>
    public bool AllowLargeDrive { get; init; }

    /// <summary>
    /// Gets the size of each buffer read from the source and fanned out to all targets.
    /// <para>获取从源读取并扇出到全部目标的单块缓冲区大小。</para>
    /// </summary>
    /// <value>Defaults to 4 MiB and must be between <see cref="MinimumBufferBytes"/> and <see cref="MaximumBufferBytes"/>.
    /// <para>默认 4 MiB，必须位于 <see cref="MinimumBufferBytes"/> 与 <see cref="MaximumBufferBytes"/> 之间。</para></value>
    public int BufferBytes { get; init; } = 4 * 1024 * 1024;
}