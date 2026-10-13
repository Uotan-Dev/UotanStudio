using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Sla;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private ProtocolEvidence? _pendingSlaEvidence;

    internal static void ValidateSlaTimeout(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
    }

    /// <summary>
    /// Records one authorization observation as session evidence and keeps failed observations
    /// pending so the next frozen upload result carries the SLA classification alongside its
    /// generic upload phase.
    /// <para>记录一条授权观测为会话证据，并保留失败观测，使下一个定格的上传结果在通用上传阶段之外
    /// 同时携带 SLA 分类。
    /// </para>
    /// </summary>
    /// <param name="flow">The authorization flow. 授权流程。</param>
    /// <param name="stage">The observation point. 观测点。</param>
    /// <param name="responderKind">The responder class that served or should have served. 服务本流程的响应方类别。</param>
    /// <param name="challengeDigest">The challenge SHA-256 hex digest, when one was read. 已读到挑战的十六进制摘要。</param>
    /// <param name="outcome">The observed outcome. 观测结果。</param>
    /// <param name="detail">Neutral detail text, carrying the classification token on failures. 中性详情文本；失败时携带分类标记。</param>
    private void RecordSlaEvidence(MtkSlaFlowKind flow, MtkSlaEvidenceStage stage,
        MtkSlaResponderKind responderKind, string? challengeDigest, CommandOutcomeKind outcome, string? detail)
    {
        var evidence = new MtkSlaEvidence
        {
            Flow = flow,
            Stage = stage,
            ResponderKind = responderKind,
            ChallengeSha256Hex = challengeDigest,
            Outcome = outcome,
            Detail = detail
        }.ToProtocolEvidence();
        _evidence.Add(evidence);
        if (outcome != CommandOutcomeKind.Completed)
        {
            _pendingSlaEvidence = evidence;
        }
    }

    private void RecordSlaFailure(MtkSlaFlowKind flow, object? responder, MtkSlaEvidenceStage stage,
        string classification, string? challengeDigest, string message)
    {
        RecordSlaEvidence(flow, stage, ClassifySlaResponder(responder), challengeDigest,
            stage switch
            {
                MtkSlaEvidenceStage.Timeout => CommandOutcomeKind.Timeout,
                MtkSlaEvidenceStage.ProviderCancelled => CommandOutcomeKind.Cancelled,
                _ => CommandOutcomeKind.Rejected
            },
            $"{classification}; {message}");
    }

    private static MtkSlaResponderKind ClassifySlaResponder(object? responder) => responder switch
    {
        null => MtkSlaResponderKind.None,
        _ => MtkSlaResponderKind.ExternalProvider
    };
}