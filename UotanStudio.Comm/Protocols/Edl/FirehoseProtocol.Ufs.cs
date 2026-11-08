namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol UFS provisioning operations.
/// <para>Firehose 协议的 UFS provisioning 操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Provisions the UFS storage: sends the common configuration, one body per LUN, and the
    /// epilogue (LUNtoGrow + commit).
    /// <para>配置 UFS 存储：发送公共配置、每个 LUN 一条主体命令以及结语（LUNtoGrow + commit）。</para>
    /// </summary>
    /// <param name="common">The common configuration carrying device-level descriptor attributes.<para>承载设备级描述符属性的公共配置。</para></param>
    /// <param name="bodies">The per-LUN body configurations, one command each.<para>每 LUN 主体配置，每个生成一条命令。</para></param>
    /// <param name="epilogue">The epilogue configuration carrying the LUN-to-grow value.<para>携带 LUNtoGrow 值的结语配置。</para></param>
    /// <param name="commit">Whether to persist the configuration on the device; <see langword="false"/> leaves it pending until a later commit.<para>是否在设备上持久化配置；<see langword="false"/> 表示待后续提交后才生效。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The epilogue Firehose response returned by the last command in the sequence.<para>命令序列最后一条（结语）的 Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> ProvisionUfsAsync(UfsCommon common, IReadOnlyList<UfsBody> bodies, UfsEpilogue epilogue, bool commit = false, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ProvisionUfsOwnedAsync(common, bodies, epilogue, commit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Provisions UFS while the caller holds the operation lease.</summary>
    internal Task<FirehoseResponse> ProvisionUfsOwnedAsync(UfsCommon common, IReadOnlyList<UfsBody> bodies,
        UfsEpilogue epilogue, bool commit, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var plan = UfsProvisionPlan.Prepare(common, bodies, epilogue, commit, EffectiveSlot);
        return ProvisionUfsOwnedAsync(plan, cancellationToken);
    }

    /// <summary>Sends a preflighted immutable command snapshot while the caller owns the operation lease.</summary>
    /// <remarks>When the plan commits, some programmers reset the target right after writing the UFS
    /// configuration descriptor and drop the link before the last epilogue acknowledgement. A transport
    /// failure, read timeout or disconnect on that final command alone is therefore completed with a
    /// synthesized acknowledgement instead of failing the provisioning: every earlier descriptor was
    /// already accepted without a NAK, and a real rejection would have produced one. Cancellation and
    /// protocol violations are never exempted.
    /// <para>计划提交时，部分 programmer 会在写入 UFS 配置描述符后立即复位目标，在其确认最后一条结语命令
    /// 之前断开链路。因此仅当最后一条命令出现传输失败、读取超时或断开时，以合成确认完成该次 provisioning
    /// 而不是判定失败：此前的描述符均已被无 NAK 接受，真正的拒绝会更早产生。取消与协议违规绝不豁免。</para></remarks>
    internal async Task<FirehoseResponse> ProvisionUfsOwnedAsync(UfsProvisionPlan plan, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(plan);
        await RequireDestructiveCapabilityAsync(EdlFirehoseCommand.UfsProvisioning, cancellationToken).ConfigureAwait(false);
        FirehoseResponse? last = null;
        for (var index = 0; index < plan.Commands.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log($"Sending UFS provisioning command {index + 1}/{plan.Commands.Count}.");
            var commitEpilogue = plan.Commit && index == plan.Commands.Count - 1;
            try
            {
                last = await SendCommandAndWaitResponseAsync(plan.Commands[index], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (commitEpilogue && IsCommitResetOutcome(ex))
            {
                _faulted = false;
                Log("UFS provisioning commit: the device reset without acknowledging the epilogue; " +
                    "the provisioning is treated as applied.");
                last = new FirehoseResponse { IsAck = true, RawValue = "ACK (device reset without acknowledgement)" };
                break;
            }

            if (last!.IsRawMode)
            {
                _faulted = true;
                throw new EdlException(EdlErrorCode.UfsProvisioningFailed, "UFS provisioning unexpectedly entered raw mode.");
            }
            if (!last.IsAck)
            {
                throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                    $"UFS provisioning command {index + 1} failed: {last.RawValue}");
            }
        }
        return last ?? throw new InvalidOperationException("The UFS plan contains no commands.");
    }

    /// <summary>
    /// Reports whether an exception on the committing epilogue matches the reset-without-acknowledgement
    /// outcome: a transport failure, a read or write timeout, a lost connection, or the raw disconnect
    /// errors the transport raises. Cancellation and protocol violations never match.
    /// <para>报告提交结语上的异常是否符合“复位且无确认”结果：传输失败、读取或写入超时、连接丢失，以及
    /// 传输层抛出的原始断开错误。取消与协议违规永不匹配。</para>
    /// </summary>
    /// <param name="error">The exception observed while waiting for the epilogue acknowledgement.
    /// <para>等待结语确认时观测到的异常。</para></param>
    /// <returns><see langword="true"/> when the outcome is exempted as a device reset.
    /// <para>结果可豁免为设备复位时为 <see langword="true"/>。</para></returns>
    private static bool IsCommitResetOutcome(Exception error) => error switch
    {
        EdlException
        {
            ErrorCode: EdlErrorCode.TransportTimeout or EdlErrorCode.ReadPortTimeout
            or EdlErrorCode.WritePortTimeout or EdlErrorCode.ConnectionLost
        } => true,
        IOException or TimeoutException => true,
        _ => false
    };

    /// <summary>
    /// Resets pending UFS provisioning configuration on the target (a &lt;ufs&gt; epilogue with
    /// commit="0" that discards without persisting).
    /// <para>复位目标端待生效的 UFS provisioning 配置（commit="0" 的 &lt;ufs&gt; 结语，丢弃且
    /// 不持久化）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The Firehose response for the reset command.<para>复位命令的 Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> UfsResetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        Log("Sending <ufs> reset (commit=0)");
        return await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildUfsReset(EffectiveSlot), cancellationToken);
    }
}