namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Runs the read-only device queries that gate a <c>flash:</c> before any payload is transferred.
/// Every check is a getvar, so a rejected flash leaves the device byte-identical; the alternative is
/// transferring a whole image only to have the bootloader reject the <c>flash:</c> that follows it.
/// A check only rejects on evidence the device affirmatively supplied: an unknown variable, a FAIL, a
/// timeout or an unparsable answer all mean "no evidence" and let the flash proceed, because a
/// bootloader that does not implement a variable must not turn into an unflashable device.
/// <para>在传输任何负载之前执行用于门控 <c>flash:</c> 的只读设备查询。每项检查都是一次 getvar，因此被拒绝
/// 的刷写会让设备保持逐字节不变；否则就要把整个镜像传完，才发现其后的 <c>flash:</c> 被 bootloader 拒绝。
/// 检查只在设备明确给出证据时才拒绝：未知变量、FAIL、超时或无法解析的应答都表示“无证据”并放行刷写，
/// 因为未实现某个变量的 bootloader 不应因此变成无法刷写的设备。</para>
/// </summary>
internal static class FlashPartitionPreflight
{
    /// <summary>
    /// Validates <paramref name="imageLength"/> and <paramref name="target"/> against the device
    /// according to <paramref name="options"/>.
    /// <para>按 <paramref name="options"/> 把 <paramref name="imageLength"/> 与
    /// <paramref name="target"/> 对照设备状态进行校验。</para>
    /// </summary>
    /// <param name="session">Session the queries are issued through. <para>发出查询所用的会话。</para></param>
    /// <param name="target">Slot-resolved partition name addressed by the following <c>flash:</c>.
    /// <para>其后 <c>flash:</c> 所指向的、已完成槽位解析的分区名。</para></param>
    /// <param name="imageLength">Exact payload size about to be downloaded. <para>即将下载的精确负载字节数。</para></param>
    /// <param name="options">Policy selecting which queries to run. <para>选择执行哪些查询的策略。</para></param>
    /// <param name="cancellationToken">Checked before the queries start. <para>在查询开始前检查。</para></param>
    /// <exception cref="FastbootException">A check the policy enabled was not satisfied by the device.
    /// <para>策略启用的某项检查未被设备满足。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    internal static void Validate(FastbootSession session, string target, long imageLength,
        FlashPartitionOptions options, CancellationToken cancellationToken)
    {
        if (!options.Preflight)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (options.VerifyDownloadLimit && imageLength > session.GetMaxDownloadSize())
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"image for '{target}' is {imageLength} bytes, which exceeds the device max-download-size; split it into sparse chunks first");
        }

        bool? logical = null;
        if (options.VerifyLogicalTarget && !session.IsUserspace())
        {
            logical = session.IsLogical(target);
            if (logical is true)
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"'{target}' is a logical partition, which bootloader fastboot cannot address; reboot into fastbootd first");
            }
        }

        if (options.VerifyCapacity)
        {
            ValidateCapacity(session, target, imageLength, logical);
        }
    }

    /// <summary>
    /// Compares the payload against the size the device reports for <paramref name="target"/>. A size the
    /// device answers with is used as evidence; a FAIL, a timeout, an empty answer or an unparsable one
    /// is treated as "the device does not expose <c>partition-size</c> here" and lets the flash proceed,
    /// because many bootloaders omit the variable for partitions that do exist. A literal zero is read as
    /// "partition absent" only from fastbootd, which enumerates every partition it exposes; a bootloader
    /// answering zero for a partition it can still flash is left alone. A logical target is exempted from
    /// the oversize rejection because its caller resizes it first and <c>partition-size</c> may still
    /// report the pre-resize value; that exemption reuses the mode check's answer where one was already
    /// made, and otherwise costs one extra query only when a rejection was already about to happen.
    /// <para>把负载与设备为 <paramref name="target"/> 上报的大小对照。设备给出应答的尺寸被用作证据；FAIL、
    /// 超时、空应答或无法解析的应答按“此处设备未暴露 <c>partition-size</c>”处理并放行刷写，因为很多
    /// bootloader 对确实存在的分区也不上报该变量。字面量零只在来自 fastbootd 时按“分区不存在”解读——
    /// fastbootd 会枚举它暴露的每个分区；对于仍能刷写的分区却应答零的 bootloader 则不予理会。逻辑分区
    /// 目标豁免于超限拒绝，因为其调用方会先调整分区大小，而 <c>partition-size</c> 可能仍上报调整前的值；
    /// 该豁免在模式检查已给出结果时直接复用，否则只在即将拒绝时才多付一次查询。</para>
    /// </summary>
    /// <param name="session">Session the queries are issued through. <para>发出查询所用的会话。</para></param>
    /// <param name="target">Slot-resolved partition name. <para>已完成槽位解析的分区名。</para></param>
    /// <param name="imageLength">Exact payload size about to be downloaded. <para>即将下载的精确负载字节数。</para></param>
    /// <param name="logical">Result of the <c>is-logical</c> query already made for this flash, or
    /// <see langword="null"/> when the policy skipped it or the device is in fastbootd.
    /// <para>本次刷写已执行过的 <c>is-logical</c> 查询结果；策略跳过该查询或设备处于 fastbootd 时为
    /// <see langword="null"/>。</para></param>
    /// <exception cref="FastbootException">Fastbootd reports a zero size for the target, or the payload exceeds a reported partition size.
    /// <para>fastbootd 为该目标上报零大小，或负载超过所上报的分区大小。</para></exception>
    private static void ValidateCapacity(FastbootSession session, string target, long imageLength, bool? logical)
    {
        var reported = session.GetVarWithResult(FastbootConstants.VarPartitionSizePrefix + target, useCache: false);
        if (!reported.Success)
        {
            return;
        }

        var trimmed = reported.Value.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (IsLiteralZero(trimmed))
        {
            if (session.IsUserspace())
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"fastbootd reports a zero size for partition '{target}', which does not exist on this device");
            }

            return;
        }

        var capacity = FastbootSession.ParseDeviceSize(trimmed);
        if (capacity <= 0 || imageLength <= capacity || (logical ?? session.IsLogical(target)))
        {
            return;
        }

        throw new FastbootException(FastbootResponseState.Fail,
            $"image for '{target}' is {imageLength} bytes but the partition is only {capacity} bytes");
    }

    /// <summary>
    /// Tells an affirmative zero size apart from an unparsable reply, both of which
    /// <see cref="FastbootSession.ParseDeviceSize(string)"/> reports as 0.
    /// <para>区分“明确上报的零大小”与“无法解析的应答”，两者在
    /// <see cref="FastbootSession.ParseDeviceSize(string)"/> 中都表现为 0。</para>
    /// </summary>
    /// <param name="trimmed">Trimmed getvar payload. <para>去除首尾空白后的 getvar 负载。</para></param>
    /// <returns><see langword="true"/> when the device literally answered zero in decimal or hexadecimal form.
    /// <para>设备以十进制或十六进制形式明确应答零时为 <see langword="true"/>。</para></returns>
    private static bool IsLiteralZero(string trimmed)
        => trimmed is "0" || trimmed.Equals("0x0", StringComparison.OrdinalIgnoreCase);
}