using System.Xml;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Sla;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Queries XML DA2 SLA under exclusive session ownership and refuses to continue when the agent requires
    /// an authorization exchange.
    /// <para>在独占会话期间查询 XML DA2 的 SLA，并在代理要求认证交换时拒绝继续。</para></summary>
    /// <param name="expectedEnvelopeName">The property envelope asserted from the loader profile, or <see langword="null"/>
    /// to accept the envelope the agent reports.
    /// <para>加载器配置断言的属性信封名称；为 <see langword="null"/> 时接受代理实际上报的信封。</para></param>
    /// <param name="timeoutMs">The cooperative deadline from 1 ms through 24 hours.
    /// <para>范围为 1 毫秒至 24 小时的协作式截止时间。</para></param>
    /// <param name="cancellationToken">Cancellation for the wait and the query.
    /// <para>等待与查询的取消令牌。</para></param>
    /// <exception cref="InvalidOperationException">DA2 hardware is not ready, or DA2 requires SLA and there is no
    /// authorized key source to answer it. <para>DA2 硬件未就绪，或 DA2 要求 SLA 且不存在可应答的授权密钥来源。</para></exception>
    /// <exception cref="MtkProtocolException">The status is unknown or a protocol exchange is rejected or malformed.
    /// <para>状态未知，或协议交换被拒绝或格式错误。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired.
    /// <para>协作式截止时间已到。</para></exception>
    /// <remarks>
    /// Only the SLA indication is read here. The device-information fetch, the bounded <c>sla</c> envelope, the
    /// flash-policy upload and an accepted-response outcome all require an authorized key source, and Comm carries no
    /// DA2 SLA key material and exposes no type to receive one, so an enabled indication ends the session instead.
    /// The property read itself is shared with the other XML property flows.
    /// <para>这里只读取 SLA 指示。设备信息读取、有界 <c>sla</c> 信封、刷写策略上传与"响应被接受"结果都要求一个授权密钥来源，
    /// 而 Comm 不携带 DA2 SLA 密钥、也不暴露可接收它的类型，因此启用状态改为结束会话。属性读取本身与其他 XML 属性流程共用。</para>
    /// </remarks>
    public async Task RequireXmlStageTwoSlaInactiveAsync(string? expectedEnvelopeName,
        int timeoutMs = 60000, CancellationToken cancellationToken = default)
    {
        if (expectedEnvelopeName is { } asserted)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(asserted);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(asserted.Length, 128);
            XmlConvert.VerifyNCName(asserted);
        }
        ValidateSlaTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStageTwoHardwareInitialized);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            var refusalRecorded = false;
            try
            {
                var property = await QueryXmlSystemPropertyOwnedAsync(channel, "DA.SLA",
                    expectedEnvelopeName, token).ConfigureAwait(false);
                var indication = new MtkXmlSlaReport(property).Indication;
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (indication == MtkXmlSlaIndication.DisabledReported)
                {
                    return;
                }
                if (indication != MtkXmlSlaIndication.EnabledReported)
                {
                    throw new MtkProtocolException("The XML DA2 returned an unknown SLA indication.");
                }
                RecordSlaEvidence(MtkSlaFlowKind.DaSlaXml, MtkSlaEvidenceStage.DeviceRequired, null,
                    CommandOutcomeKind.Completed, "sla-required; XML DA2 reported SLA enabled.");
                RecordSlaFailure(MtkSlaFlowKind.DaSlaXml, MtkSlaEvidenceStage.Rejected, "SlaNoProvider", null,
                    "XML DA2 requires an explicitly authorized SLA response provider; no challenge response bytes were written.");
                refusalRecorded = true;
                throw new InvalidOperationException("XML DA2 requires an explicitly authorized SLA response provider.");
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                RecordSlaFailure(MtkSlaFlowKind.DaSlaXml, MtkSlaEvidenceStage.Timeout, "SlaTimeout",
                    null, error.Message);
                CaptureUploadFailure(error);
                FaultXml();
                throw new TimeoutException("The XML DA2 SLA query deadline expired; reconnect before continuing.", error);
            }
            catch (Exception error)
            {
                if (!refusalRecorded)
                {
                    var (stage, classification) = MtkSlaEvidence.ClassifyFailure(error, deadlineExpired: false);
                    RecordSlaFailure(MtkSlaFlowKind.DaSlaXml, stage, classification, null, error.Message);
                }
                CaptureUploadFailure(error);
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }
}