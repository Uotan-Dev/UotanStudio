using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Sla;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Queries DA2 SLA under exclusive session ownership and refuses to continue when the agent requires an
    /// authorization exchange. An unknown status never means authentication is disabled.
    /// <para>在独占会话期间查询 DA2 的 SLA，并在代理要求认证交换时拒绝继续。未知状态不表示认证已禁用。</para></summary>
    /// <param name="timeoutMs">The cooperative query deadline, from 1 ms to 24 hours.
    /// <para>协作式查询截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for the wait and the query.
    /// <para>等待与查询的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The reply is malformed or carries an unknown SLA status.
    /// <para>回复格式错误，或带有未知的 SLA 状态。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 is not ready, or DA2 requires SLA and there is no authorized
    /// key source to answer it. <para>DA2 未就绪，或 DA2 要求 SLA 且不存在可应答的授权密钥来源。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired.
    /// <para>协作式截止时间已到。</para></exception>
    /// <remarks>
    /// <para>DA2 SLA has no response-provider type in Comm and Comm carries no SLA key material, so an enabled status
    /// is terminal for the session: the challenge is never read and no response byte is written. Re-opening this
    /// exchange is a deliberate re-entry decision that needs device evidence for the specific chip and a
    /// caller-supplied authorized key source, not a missing adapter in the host.</para>
    /// <para>Comm 内没有 DA2 SLA 的响应提供方类型，也不携带 SLA 密钥，因此启用状态对该会话即终局：从不读取挑战，也不写出任何响应字节。
    /// 重新打开这一交换是有意的重新立项决定，需要针对具体机型的设备证据与调用方提供的授权密钥来源，而不是宿主缺少一个适配器。</para>
    /// </remarks>
    public async Task RequireXFlashStageTwoSlaInactiveAsync(int timeoutMs = 60000,
        CancellationToken cancellationToken = default)
    {
        ValidateSlaTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageTwoStarted);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            var refusalRecorded = false;
            try
            {
                var status = await QueryXFlashControlAsync(channel, 0x040016, token, 4).ConfigureAwait(false);
                if (status.Length != 4)
                {
                    throw new MtkProtocolException("The DA2 SLA status must contain one word.");
                }
                var enabled = BinaryPrimitives.ReadUInt32LittleEndian(status);
                if (enabled == 0)
                {
                    return;
                }
                if (enabled != 1)
                {
                    throw new MtkProtocolException("The DA2 returned an unknown SLA status.");
                }
                RecordSlaEvidence(MtkSlaFlowKind.DaSlaXFlash, MtkSlaEvidenceStage.DeviceRequired, null,
                    CommandOutcomeKind.Completed, "sla-required; DA2 reported SLA enabled.");
                RecordSlaFailure(MtkSlaFlowKind.DaSlaXFlash, MtkSlaEvidenceStage.Rejected, "SlaNoProvider", null,
                    "DA2 requires an explicitly authorized SLA response provider; no challenge response bytes were written.");
                refusalRecorded = true;
                throw new InvalidOperationException("DA2 requires an explicitly authorized SLA response provider.");
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                RecordSlaFailure(MtkSlaFlowKind.DaSlaXFlash, MtkSlaEvidenceStage.Timeout, "SlaTimeout",
                    null, error.Message);
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw new TimeoutException("The DA2 SLA query deadline expired; reconnect before continuing.", error);
            }
            catch (Exception error)
            {
                if (!refusalRecorded)
                {
                    var (stage, classification) = MtkSlaEvidence.ClassifyFailure(error, deadlineExpired: false);
                    RecordSlaFailure(MtkSlaFlowKind.DaSlaXFlash, stage, classification, null, error.Message);
                }
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }
}