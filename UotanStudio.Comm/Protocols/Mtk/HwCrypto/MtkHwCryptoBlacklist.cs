namespace UotanStudio.Comm.Protocols.Mtk.HwCrypto;

/// <summary>
/// The order in which a MediaTek hardware-crypto engine is brought up, its caches refreshed and its range
/// blacklist lifted, exactly as the reference dispatcher performs it per engine type.
/// <para>联发科硬件加密引擎的启动、缓存刷新与范围黑名单解除顺序，与参考实现分发器按引擎类型的做法完全一致。</para>
/// </summary>
/// <remarks>
/// <para><c>mtkclient/Library/Hardware/hwcrypto.py</c>, lines 125-140 is the only dispatcher for this: the
/// <c>"gcpu"</c> branch (named by <c>mtkclient/Library/Exploit/amonet.py</c>, line 50) initialises and acquires the
/// engine twice, refreshes the caches and then lifts the ranges, while the <c>"cqdma"</c> branch (named by
/// <c>mtkclient/Library/Exploit/hashimoto.py</c>, line 29) refreshes the caches first and then lifts the ranges.
/// Any other engine name the reference accepts falls through and runs nothing, which is why the Kamakiri families
/// never needed these engines at all.</para>
/// <para><c>mtkclient/Library/Hardware/hwcrypto.py</c> 第 125-140 行是此处唯一的分发器：<c>"gcpu"</c> 分支（由
/// <c>mtkclient/Library/Exploit/amonet.py</c> 第 50 行指名）两次初始化并获取引擎、刷新缓存，然后解除范围；
/// <c>"cqdma"</c> 分支（由 <c>mtkclient/Library/Exploit/hashimoto.py</c> 第 29 行指名）先刷新缓存，再解除范围。参考实现
/// 接受的其他引擎名称都会落空且不执行任何操作，这也正是 Kamakiri 各家族从不需要这些引擎的原因。</para>
/// </remarks>
internal static class MtkHwCryptoBlacklist
{
    /// <summary>Brings up the GCPU engine and lifts every range its chip entry records.
    /// <para>启动 GCPU 引擎并解除其芯片条目记录的全部范围。</para></summary>
    /// <param name="engine">The GCPU engine to drive. <para>要驱动的 GCPU 引擎。</para></param>
    /// <param name="access">The download-agent seam used for the cache refresh.
    /// <para>用于缓存刷新的下载代理接缝。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused a register access, an engine command never
    /// completed, or a range never programmed. <para>下载代理拒绝寄存器访问、引擎命令始终未完成，或某条范围始终未能编程。</para>
    /// </exception>
    /// <remarks>
    /// <para>The double init/acquire pair is not an accident: <c>mtkclient/Library/Hardware/hwcrypto.py</c>,
    /// lines 127-131 runs <c>init</c>, <c>acquire</c>, <c>init</c>, <c>acquire</c> before the refresh and the lift.
    /// <c>mtkclient/Library/Exploit/amonet.py</c>, lines 22-23 and 93-94 additionally guard the whole call with
    /// <c>if chipconfig.blacklist</c>, so a chip without recorded ranges never reaches the engine.</para>
    /// <para>两次 init/acquire 并非偶然：<c>mtkclient/Library/Hardware/hwcrypto.py</c> 第 127-131 行在刷新与解除之前依次运行
    /// <c>init</c>、<c>acquire</c>、<c>init</c>、<c>acquire</c>。此外
    /// <c>mtkclient/Library/Exploit/amonet.py</c> 第 22-23 与 93-94 行用 <c>if chipconfig.blacklist</c> 守护整个调用，因此
    /// 没有记录范围的芯片永远不会触及该引擎。</para>
    /// </remarks>
    internal static async Task DisableGcpuRangesAsync(MtkGcpuCryptoEngine engine, MtkDaRegisterAccess access,
        CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await engine.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await engine.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await access.RefreshCachesAsync(cancellationToken).ConfigureAwait(false);
        await engine.DisableRangeBlacklistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refreshes the caches and lifts every CQDMA range the chip entry records.
    /// <para>刷新缓存并解除芯片条目记录的全部 CQDMA 范围。</para></summary>
    /// <param name="engine">The CQDMA engine to drive. <para>要驱动的 CQDMA 引擎。</para></param>
    /// <param name="access">The download-agent seam used for the cache refresh.
    /// <para>用于缓存刷新的下载代理接缝。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused a register access or a transfer never
    /// completed. <para>下载代理拒绝寄存器访问，或某次传输始终未完成。</para></exception>
    internal static async Task DisableCqdmaRangesAsync(MtkCqdmaCryptoEngine engine, MtkDaRegisterAccess access,
        CancellationToken cancellationToken)
    {
        await access.RefreshCachesAsync(cancellationToken).ConfigureAwait(false);
        await engine.DisableRangeBlacklistAsync(cancellationToken).ConfigureAwait(false);
    }
}