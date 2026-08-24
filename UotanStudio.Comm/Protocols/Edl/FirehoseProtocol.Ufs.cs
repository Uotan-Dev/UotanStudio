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
        ThrowIfDisposed();
        if (common == null)
        {
            throw new ArgumentNullException(nameof(common));
        }

        if (bodies == null)
        {
            throw new ArgumentNullException(nameof(bodies));
        }

        if (epilogue == null)
        {
            throw new ArgumentNullException(nameof(epilogue));
        }

        Log("Starting UFS provisioning...");
        var slot = EffectiveSlot;

        Log("Sending UFS common configuration...");
        var commonResponse = await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildUfsCommon(common, slot), cancellationToken);
        if (!commonResponse.IsAck)
        {
            throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                $"UFS common configuration failed: {commonResponse.RawValue}");
        }

        foreach (var body in bodies)
        {
            Log($"Sending UFS body for LUN {body.LUNum}...");
            var bodyResponse = await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildUfsBody(body, slot), cancellationToken);
            if (!bodyResponse.IsAck)
            {
                throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                    $"UFS body LUN {body.LUNum} failed: {bodyResponse.RawValue}");
            }
        }

        Log("Sending UFS epilogue...");
        var epilogueResponse = await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildUfsEpilogue(epilogue, commit, slot), cancellationToken);
        if (!epilogueResponse.IsAck)
        {
            throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                $"UFS epilogue failed: {epilogueResponse.RawValue}");
        }

        Log("UFS provisioning succeeded.");
        return epilogueResponse;
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
        ThrowIfDisposed();
        Log("Sending <ufs> reset (commit=0)");
        return await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildUfsReset(EffectiveSlot), cancellationToken);
    }
}