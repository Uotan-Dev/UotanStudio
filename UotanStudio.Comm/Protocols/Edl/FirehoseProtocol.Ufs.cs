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
    /// <param name="common">The common configuration. 公共配置。</param>
    /// <param name="bodies">The per-LUN body configurations. 每 LUN 主体配置。</param>
    /// <param name="epilogue">The epilogue configuration. 结语配置。</param>
    /// <param name="commit">Whether to commit the configuration. 是否提交配置。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The epilogue Firehose response. 结语的 Firehose 响应。</returns>
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
    internal async Task<FirehoseResponse> ProvisionUfsOwnedAsync(UfsProvisionPlan plan, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(plan);
        FirehoseResponse? last = null;
        for (var index = 0; index < plan.Commands.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log($"Sending UFS provisioning command {index + 1}/{plan.Commands.Count}.");
            last = await SendCommandAndWaitResponseAsync(plan.Commands[index], cancellationToken).ConfigureAwait(false);
            if (last.IsRawMode)
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
    /// Resets pending UFS provisioning configuration on the target (a &lt;ufs&gt; epilogue with
    /// commit="0" that discards without persisting).
    /// <para>复位目标端待生效的 UFS provisioning 配置（commit="0" 的 &lt;ufs&gt; 结语，丢弃且
    /// 不持久化）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> UfsResetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        Log("Sending <ufs> reset (commit=0)");
        return await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildUfsReset(EffectiveSlot), cancellationToken);
    }
}