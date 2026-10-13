namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Selects which read-only device queries <see cref="FastbootSession.FlashPartition"/> runs before it
/// sends <c>download:</c>. A rejected flash therefore never transfers a payload and leaves the device
/// untouched; the opt-outs exist because a caller that already performed a check would otherwise pay a
/// second getvar round trip per partition.
/// <para>选择 <see cref="FastbootSession.FlashPartition"/> 在发送 <c>download:</c> 之前执行哪些只读设备
/// 查询。因此被拒绝的刷写绝不传输负载，设备保持原状；提供退出开关是因为已经完成过某项检查的调用方否则
/// 会按分区多付一次 getvar 往返。</para>
/// </summary>
public sealed class FlashPartitionOptions
{
    /// <summary>
    /// Gets the default policy: every preflight check enabled.
    /// <para>获取默认策略：启用全部预检。</para>
    /// </summary>
    /// <value>A shared instance with <see cref="Preflight"/>, <see cref="VerifyDownloadLimit"/>,
    /// <see cref="VerifyLogicalTarget"/> and <see cref="VerifyCapacity"/> all set.
    /// <para>共享实例：<see cref="Preflight"/>、<see cref="VerifyDownloadLimit"/>、
    /// <see cref="VerifyLogicalTarget"/> 与 <see cref="VerifyCapacity"/> 均置位。</para></value>
    public static FlashPartitionOptions Default { get; } = new();

    /// <summary>
    /// Gets the policy that issues no preflight query at all, restoring the bare
    /// <c>download:</c> + <c>flash:</c> sequence for a caller that validated the target itself.
    /// <para>获取完全不发出预检查询的策略，为已自行校验目标的调用方恢复裸的
    /// <c>download:</c> + <c>flash:</c> 序列。</para>
    /// </summary>
    /// <value>A shared instance with <see cref="Preflight"/> cleared. <para><see cref="Preflight"/> 未置位的共享实例。</para></value>
    public static FlashPartitionOptions NoPreflight { get; } = new() { Preflight = false };

    /// <summary>
    /// Gets the policy for a caller that already resolved the A/B slot, queried <c>is-logical</c> and
    /// resized a logical partition before transferring.
    /// <para>获取供已自行完成 A/B 槽位解析、查询过 <c>is-logical</c> 并调整过逻辑分区大小的调用方使用的策略。</para>
    /// Re-asking the device those two questions would only add a round trip. The download-limit check stays
    /// on — the negotiated limit is cached in the session — and so does the capacity check, which compares
    /// the payload against <c>partition-size</c>, a query that caller never made for a physical partition.
    /// <para>再问设备同样两个问题只会多一次往返。下载上限检查保持开启——协商上限已在会话内缓存；
    /// 容量检查也保持开启，它把负载与 <c>partition-size</c> 对照，而这是该调用方对物理分区从未做过的
    /// 查询。</para>
    /// </summary>
    /// <value>A shared instance with <see cref="VerifyLogicalTarget"/> cleared and the other checks set.
    /// <para><see cref="VerifyLogicalTarget"/> 未置位、其余检查均置位的共享实例。</para></value>
    public static FlashPartitionOptions PreparedTarget { get; } = new() { VerifyLogicalTarget = false };

    /// <summary>
    /// Gets the policy for one sparse chunk of an already-prepared target.
    /// <para>获取“已准备好的目标”的单个稀疏分片所使用的策略。</para>
    /// The mode check is off for the same reason as <see cref="PreparedTarget"/>, and the capacity check is
    /// off too: what is transferred here is a sparse file whose length says nothing about the expanded bytes
    /// it will occupy, so comparing it against <c>partition-size</c> would repeat the query per chunk without
    /// any evidence to act on. The download-limit check stays on because the negotiated limit is cached in the
    /// session and re-checking it costs nothing.
    /// <para>模式检查关闭的理由与 <see cref="PreparedTarget"/> 相同；容量检查同样关闭：此处传输的是稀疏
    /// 文件，其长度完全不能说明它将占用的展开字节数，因此拿它与 <c>partition-size</c> 对照只会在每个分片
    /// 上重复查询却得不到任何可用证据。下载上限检查保持开启，因为协商上限已在会话内缓存，重复检查不产生
    /// 开销。</para>
    /// </summary>
    /// <value>A shared instance with only <see cref="VerifyDownloadLimit"/> set.
    /// <para>只有 <see cref="VerifyDownloadLimit"/> 置位的共享实例。</para></value>
    public static FlashPartitionOptions PreparedSparseChunks { get; } = new()
    {
        VerifyLogicalTarget = false,
        VerifyCapacity = false
    };

    /// <summary>
    /// Gets or sets whether any preflight query is issued at all. Clearing it short-circuits every
    /// other flag, which is what a caller that already ran the whole validation itself wants.
    /// <para>获取或设置是否发出任何预检查询。清除它会使其余所有开关短路，这正是已自行完成全部校验的
    /// 调用方所需要的。</para>
    /// </summary>
    /// <value><see langword="true"/> to run the enabled checks before the download; <see langword="false"/> to flash immediately.
    /// <para>为 <see langword="true"/> 时在下载前执行已启用的检查；为 <see langword="false"/> 时立即刷写。</para></value>
    public bool Preflight { get; init; } = true;

    /// <summary>
    /// Gets or sets whether the image length is checked against <c>max-download-size</c> before the
    /// download starts. A caller that already split the image at exactly that limit gains nothing from
    /// asking again, but the negotiated limit is cached per session so leaving this on costs one query
    /// at most.
    /// <para>获取或设置在下载开始前是否把镜像长度与 <c>max-download-size</c> 对照。已按该上限完成分片的
    /// 调用方再次查询没有收益；协商上限按会话缓存，因此保留开启最多只花一次查询。</para>
    /// </summary>
    /// <value><see langword="true"/> to reject an oversized payload with a message that names the limit; <see langword="false"/> to let the download path raise it.
    /// <para>为 <see langword="true"/> 时以指出上限的消息拒绝超限负载；为 <see langword="false"/> 时交由下载路径抛出。</para></value>
    public bool VerifyDownloadLimit { get; init; } = true;

    /// <summary>
    /// Gets or sets whether a logical target is checked against the current fastboot mode. Logical partitions live inside super and the bootloader cannot address them, so flashing one outside fastbootd fails only after the whole image has been transferred.
    /// <para>获取或设置是否把逻辑目标与当前 fastboot 模式比对。逻辑分区位于 super 内部、bootloader 无法寻址，因此在 fastbootd 之外刷写会在整份镜像传输完毕后才失败。</para>
    /// </summary>
    /// <value><see langword="true"/> to reject a logical target while the device is not in userspace fastboot; <see langword="false"/> to skip the mode query.
    /// <para>为 <see langword="true"/> 时，设备不处于用户空间 fastboot 就拒绝逻辑分区目标；为 <see langword="false"/> 时跳过模式查询。</para></value>
    public bool VerifyLogicalTarget { get; init; } = true;

    /// <summary>
    /// Gets or sets whether the image length is checked against the reported partition size. Only an affirmatively reported size counts as evidence; a timeout, an empty answer or an unparsable one means unknown and never rejects.
    /// <para>获取或设置是否把镜像长度与上报的分区大小比对。只有明确上报的大小才算证据；超时、空应答或无法解析均视为未知，不会拒绝。</para>
    /// </summary>
    /// <value><see langword="true"/> to reject an image larger than the reported partition; <see langword="false"/> to skip the size query.
    /// <para>为 <see langword="true"/> 时拒绝大于所上报分区的镜像；为 <see langword="false"/> 时跳过尺寸查询。</para></value>
    public bool VerifyCapacity { get; init; } = true;
}